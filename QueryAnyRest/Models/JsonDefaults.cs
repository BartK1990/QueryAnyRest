using System.Text.Json;
using System.Text.Json.Serialization;

namespace QueryAnyRest.Models;

public static class JsonDefaults
{
    /// <summary>Used for the files on disk: readable, and enums stay as names.</summary>
    public static readonly JsonSerializerOptions Storage = new()
    {
        WriteIndented = true,
        // Keeps scripts and URLs legible in the file instead of escaping quotes, & and < >.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Used when pretty-printing an API response for display.</summary>
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
