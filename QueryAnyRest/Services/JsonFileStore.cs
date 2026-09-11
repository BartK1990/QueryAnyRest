using System.Text.Json;
using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>
/// Small helper for the JSON files this app owns. Writes go to a temp file first
/// and are then moved over the target, so a power cut cannot leave a half-written
/// configuration behind.
/// </summary>
public static class JsonFileStore
{
    public static async Task<T> ReadAsync<T>(string path, Func<T> fallback, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path)) return fallback();

            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonDefaults.Storage, ct);
            return value ?? fallback();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection rather than silently replacing it.
            var broken = path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Move(path, broken); } catch { /* best effort */ }
            return fallback();
        }
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonDefaults.Storage, ct);
        }

        File.Move(temp, path, overwrite: true);
    }
}
