using System.Text.Json.Serialization;

namespace QueryAnyRest.Models;

public enum AuthMode
{
    /// <summary>Open API; nothing is sent.</summary>
    None,
    /// <summary>OAuth 2.0 password grant with refresh-token rotation.</summary>
    OAuth2Password,
    /// <summary>A token you already have, sent as-is.</summary>
    StaticBearer
}

public enum ClientCredentialPlacement
{
    /// <summary>client_id and client_secret travel in the form body.</summary>
    RequestBody,
    /// <summary>client_id and client_secret travel in an HTTP Basic header.</summary>
    BasicHeader
}

public sealed class AuthSettings
{
    public AuthMode Mode { get; set; } = AuthMode.None;

    // --- OAuth 2.0 password grant ---
    public string AuthUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    [JsonConverter(typeof(SecretStringConverter))]
    public string Password { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    [JsonConverter(typeof(SecretStringConverter))]
    public string ClientSecret { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;

    public ClientCredentialPlacement CredentialPlacement { get; set; } = ClientCredentialPlacement.RequestBody;

    /// <summary>Use the refresh token when the access token expires instead of re-sending the password.</summary>
    public bool UseRefreshToken { get; set; } = true;

    /// <summary>Refresh this many seconds before the server-stated expiry.</summary>
    public int RefreshSkewSeconds { get; set; } = 60;

    /// <summary>Assumed lifetime when the token endpoint omits expires_in.</summary>
    public int FallbackLifetimeSeconds { get; set; } = 300;

    /// <summary>Anything else the token endpoint wants, e.g. acr_values or a tenant id.</summary>
    public List<KeyValueItem> ExtraTokenParameters { get; set; } = new();

    // --- Static bearer ---
    [JsonConverter(typeof(SecretStringConverter))]
    public string StaticToken { get; set; } = string.Empty;

    /// <summary>Header the token is written into. Almost always Authorization.</summary>
    public string HeaderName { get; set; } = "Authorization";

    /// <summary>Prefix in front of the token value.</summary>
    public string TokenPrefix { get; set; } = "Bearer";
}
