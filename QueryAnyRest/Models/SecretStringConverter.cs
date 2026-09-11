using System.Text.Json;
using System.Text.Json.Serialization;
using QueryAnyRest.Services;

namespace QueryAnyRest.Models;

/// <summary>
/// Put <c>[JsonConverter(typeof(SecretStringConverter))]</c> on a string property and its
/// value is encrypted on the way to disk and decrypted on the way back. Everything in
/// memory stays plaintext, so the rest of the code needs no changes.
///
/// The protector is handed over once at start-up rather than injected, because
/// System.Text.Json builds converters from the attribute without a DI scope.
/// Program.cs calls <see cref="Use"/> before any store touches a file.
/// </summary>
public sealed class SecretStringConverter : JsonConverter<string>
{
    private static ISecretProtector? _protector;

    public static void Use(ISecretProtector protector) => _protector = protector;

    /// <summary>True once encryption is wired up. Used by the UI to warn when it is not.</summary>
    public static bool IsActive => _protector is not null;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        // Without a protector the value is returned as written, so tests and a
        // first run before configuration still behave.
        return _protector is null ? raw : _protector.Unprotect(raw);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.WriteStringValue(string.Empty);
            return;
        }

        writer.WriteStringValue(_protector is null ? value : _protector.Protect(value));
    }
}
