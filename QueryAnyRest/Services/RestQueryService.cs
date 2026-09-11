using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>Builds and sends the configured HTTP call, attaching the token when there is one.</summary>
public sealed class RestQueryService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OAuthTokenService _tokens;
    private readonly ILogger<RestQueryService> _log;

    public RestQueryService(
        IHttpClientFactory httpClientFactory,
        OAuthTokenService tokens,
        ILogger<RestQueryService> log)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _log = log;
    }

    public async Task<QueryResult> ExecuteAsync(ApiConfiguration configuration, CancellationToken ct)
    {
        var url = configuration.Request.BuildUrl();
        var result = new QueryResult { RequestUrl = url };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            string? authorization;
            try
            {
                authorization = await _tokens.GetAuthorizationValueAsync(configuration, ct);
            }
            catch (TokenAcquisitionException ex)
            {
                result.Error = ex.Message;
                result.ElapsedMs = stopwatch.ElapsedMilliseconds;
                return result;
            }

            using var request = BuildRequest(configuration, url, authorization);

            var client = _httpClientFactory.CreateClient(
                configuration.Request.AllowInvalidCertificates
                    ? HttpClientNames.Insecure
                    : HttpClientNames.Default);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.Request.TimeoutSeconds, 1, 3600)));

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);

            result.StatusCode = (int)response.StatusCode;
            result.ReasonPhrase = response.ReasonPhrase ?? string.Empty;
            result.Success = response.IsSuccessStatusCode;
            result.Body = await response.Content.ReadAsStringAsync(ct);
            result.ContentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;

            foreach (var header in response.Headers)
                result.ResponseHeaders[header.Key] = string.Join(", ", header.Value);
            foreach (var header in response.Content.Headers)
                result.ResponseHeaders[header.Key] = string.Join(", ", header.Value);

            if (!result.Success)
                result.Error = $"{result.StatusCode} {result.ReasonPhrase}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Error = $"The request did not answer within {configuration.Request.TimeoutSeconds} seconds.";
        }
        catch (HttpRequestException ex)
        {
            result.Error = $"The request failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected failure calling {Url}", url);
            result.Error = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        }

        return result;
    }

    private static HttpRequestMessage BuildRequest(ApiConfiguration configuration, string url, string? authorization)
    {
        var settings = configuration.Request;

        var request = new HttpRequestMessage(new HttpMethod(settings.Method.ToUpperInvariant()), url);

        if (settings.SendsBody)
        {
            var mediaType = string.IsNullOrWhiteSpace(settings.ContentType) ? "application/json" : settings.ContentType;
            request.Content = new StringContent(settings.Body, Encoding.UTF8, mediaType);
        }

        foreach (var header in settings.Headers.Where(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Key)))
        {
            var key = header.Key.Trim();
            if (!request.Headers.TryAddWithoutValidation(key, header.Value) && request.Content is not null)
            {
                // Content headers such as Content-Type replace the default instead of adding a second value.
                request.Content.Headers.Remove(key);
                request.Content.Headers.TryAddWithoutValidation(key, header.Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(authorization))
        {
            var headerName = string.IsNullOrWhiteSpace(configuration.Auth.HeaderName)
                ? "Authorization"
                : configuration.Auth.HeaderName.Trim();

            request.Headers.Remove(headerName);
            request.Headers.TryAddWithoutValidation(headerName, authorization);
        }

        if (!request.Headers.Accept.Any())
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return request;
    }
}
