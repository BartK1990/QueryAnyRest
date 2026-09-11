using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

public sealed class TokenAcquisitionException : Exception
{
    public TokenAcquisitionException(string message, string? responseBody = null) : base(message)
        => ResponseBody = responseBody;

    public string? ResponseBody { get; }
}

/// <summary>
/// OAuth 2.0 password grant with refresh-token rotation.
///
/// First call posts grant_type=password. Later calls reuse the cached access token
/// until it is close to expiry, then post grant_type=refresh_token. Servers that
/// rotate refresh tokens return a new one in that response and invalidate the old
/// one immediately, so the new value is written to disk before it is used again.
/// If a refresh is rejected, the password grant is retried once.
/// </summary>
public sealed class OAuthTokenService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenStore _tokens;
    private readonly ILogger<OAuthTokenService> _log;

    // One refresh at a time per configuration: a rotated refresh token is single-use,
    // so two parallel refreshes would invalidate each other.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public OAuthTokenService(IHttpClientFactory httpClientFactory, TokenStore tokens, ILogger<OAuthTokenService> log)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _log = log;
    }

    /// <summary>Returns the header value to send, or null when the endpoint needs no authentication.</summary>
    public async Task<string?> GetAuthorizationValueAsync(ApiConfiguration configuration, CancellationToken ct)
    {
        var auth = configuration.Auth;

        switch (auth.Mode)
        {
            case AuthMode.None:
                return null;

            case AuthMode.StaticBearer:
                return Combine(auth.TokenPrefix, auth.StaticToken);

            case AuthMode.OAuth2Password:
                var state = await GetTokenAsync(configuration, forceNew: false, ct);
                return Combine(auth.TokenPrefix, state.AccessToken);

            default:
                return null;
        }
    }

    /// <summary>Used by the Test connection button and by the forced re-authentication action.</summary>
    public async Task<TokenState> GetTokenAsync(ApiConfiguration configuration, bool forceNew, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(configuration.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var auth = configuration.Auth;
            var existing = await _tokens.GetAsync(configuration.Id, ct);

            if (!forceNew && existing is not null && existing.IsUsable(auth.RefreshSkewSeconds))
                return existing;

            if (!forceNew && auth.UseRefreshToken && !string.IsNullOrWhiteSpace(existing?.RefreshToken))
            {
                try
                {
                    return await RequestAsync(configuration, existing!.RefreshToken!, existing, ct);
                }
                catch (TokenAcquisitionException ex)
                {
                    _log.LogWarning(ex,
                        "Refresh failed for {Name}, falling back to the password grant.", configuration.Name);
                }
            }

            return await RequestAsync(configuration, refreshToken: null, existing, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task InvalidateAsync(string configurationId, CancellationToken ct) =>
        _tokens.ClearAsync(configurationId, ct);

    private async Task<TokenState> RequestAsync(
        ApiConfiguration configuration,
        string? refreshToken,
        TokenState? previous,
        CancellationToken ct)
    {
        var auth = configuration.Auth;
        var isRefresh = !string.IsNullOrWhiteSpace(refreshToken);

        var form = new Dictionary<string, string>();

        if (isRefresh)
        {
            form["grant_type"] = "refresh_token";
            form["refresh_token"] = refreshToken!;
        }
        else
        {
            form["grant_type"] = "password";
            form["username"] = auth.Username;
            form["password"] = auth.Password;
        }

        if (!string.IsNullOrWhiteSpace(auth.Scope))
            form["scope"] = auth.Scope.Trim();

        if (auth.CredentialPlacement == ClientCredentialPlacement.RequestBody)
        {
            if (!string.IsNullOrWhiteSpace(auth.ClientId)) form["client_id"] = auth.ClientId;
            if (!string.IsNullOrWhiteSpace(auth.ClientSecret)) form["client_secret"] = auth.ClientSecret;
        }

        foreach (var extra in auth.ExtraTokenParameters.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Key)))
            form[extra.Key.Trim()] = extra.Value ?? string.Empty;

        using var request = new HttpRequestMessage(HttpMethod.Post, auth.AuthUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (auth.CredentialPlacement == ClientCredentialPlacement.BasicHeader)
        {
            var raw = $"{Uri.EscapeDataString(auth.ClientId)}:{Uri.EscapeDataString(auth.ClientSecret)}";
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }

        // Auth is shared by the whole chain; the first step's request settings stand in
        // for the certificate and timeout behavior of the token call itself.
        var requestSettings = configuration.Steps.Count > 0 ? configuration.Steps[0].Request : new RequestSettings();

        var client = _httpClientFactory.CreateClient(
            requestSettings.AllowInvalidCertificates
                ? HttpClientNames.Insecure
                : HttpClientNames.Default);

        // The token call shares the request timeout, so a silent token endpoint cannot hang a run.
        var seconds = Math.Clamp(requestSettings.TimeoutSeconds, 1, 3600);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, timeout.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new TokenAcquisitionException($"Could not reach the token endpoint: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TokenAcquisitionException($"The token endpoint did not answer within {seconds} seconds.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var detail = DescribeOAuthError(body);
                throw new TokenAcquisitionException(
                    $"Token endpoint answered {(int)response.StatusCode} {response.ReasonPhrase}{detail}", body);
            }

            return await StoreAsync(configuration, body, previous, isRefresh, ct);
        }
    }

    private async Task<TokenState> StoreAsync(
        ApiConfiguration configuration,
        string body,
        TokenState? previous,
        bool wasRefresh,
        CancellationToken ct)
    {
        string accessToken;
        string? rotatedRefreshToken = null;
        string tokenType = "Bearer";
        int lifetime = configuration.Auth.FallbackLifetimeSeconds;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("access_token", out var accessElement) ||
                accessElement.ValueKind != JsonValueKind.String)
            {
                throw new TokenAcquisitionException("The token response contained no access_token.", body);
            }

            accessToken = accessElement.GetString()!;

            if (root.TryGetProperty("refresh_token", out var refreshElement) &&
                refreshElement.ValueKind == JsonValueKind.String)
            {
                rotatedRefreshToken = refreshElement.GetString();
            }

            if (root.TryGetProperty("token_type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String)
            {
                tokenType = typeElement.GetString()!;
            }

            if (root.TryGetProperty("expires_in", out var expiresElement))
            {
                if (expiresElement.ValueKind == JsonValueKind.Number && expiresElement.TryGetInt32(out var seconds))
                    lifetime = seconds;
                else if (expiresElement.ValueKind == JsonValueKind.String &&
                         int.TryParse(expiresElement.GetString(), out var parsed))
                    lifetime = parsed;
            }
        }
        catch (JsonException)
        {
            throw new TokenAcquisitionException("The token response was not JSON.", body);
        }

        var state = new TokenState
        {
            ConfigurationId = configuration.Id,
            AccessToken = accessToken,
            // Keep the previous refresh token only when the server did not rotate it.
            RefreshToken = rotatedRefreshToken ?? previous?.RefreshToken,
            TokenType = tokenType,
            ObtainedUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, lifetime)),
            LastGrantUsed = wasRefresh ? "refresh_token" : "password",
            RefreshCount = wasRefresh ? (previous?.RefreshCount ?? 0) + 1 : 0
        };

        await _tokens.SetAsync(state, ct);

        _log.LogInformation(
            "Token for {Name} obtained via {Grant}, valid for {Seconds}s, refresh token {Rotation}.",
            configuration.Name, state.LastGrantUsed, lifetime,
            rotatedRefreshToken is null ? "unchanged" : "rotated");

        return state;
    }

    private static string DescribeOAuthError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var code = root.TryGetProperty("error", out var e) ? e.ToString() : null;
            var description = root.TryGetProperty("error_description", out var d) ? d.ToString() : null;

            if (code is null && description is null) return string.Empty;
            return $" ({code}{(description is null ? "" : ": " + description)})";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Combine(string prefix, string token) =>
        string.IsNullOrWhiteSpace(prefix) ? token : $"{prefix.Trim()} {token}";
}
