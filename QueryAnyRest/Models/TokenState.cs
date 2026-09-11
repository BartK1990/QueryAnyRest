using System.Text.Json.Serialization;

namespace QueryAnyRest.Models;

/// <summary>
/// The live token pair for one configuration. Persisted so a service restart
/// does not force a fresh password grant and does not lose a rotated refresh token.
/// </summary>
public sealed class TokenState
{
    public string ConfigurationId { get; set; } = string.Empty;
    [JsonConverter(typeof(SecretStringConverter))]
    public string AccessToken { get; set; } = string.Empty;
    [JsonConverter(typeof(SecretStringConverter))]
    public string? RefreshToken { get; set; }
    public string TokenType { get; set; } = "Bearer";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset ObtainedUtc { get; set; }

    /// <summary>How the current access token was obtained, for the diagnostics panel.</summary>
    public string LastGrantUsed { get; set; } = string.Empty;

    /// <summary>Counts rotations so you can see the refresh token really is changing.</summary>
    public int RefreshCount { get; set; }

    public bool IsUsable(int skewSeconds) =>
        !string.IsNullOrEmpty(AccessToken) &&
        ExpiresAtUtc.AddSeconds(-Math.Max(0, skewSeconds)) > DateTimeOffset.UtcNow;
}
