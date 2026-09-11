using System.Text;
using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

public sealed record ExportOutcome(bool Written, string? Path, long Bytes, string? Reason);

/// <summary>Writes the transformed text to the configured folder, timestamping the name.</summary>
public sealed class ExportService
{
    private readonly ILogger<ExportService> _log;

    public ExportService(ILogger<ExportService> log) => _log = log;

    public async Task<ExportOutcome> WriteAsync(ApiConfiguration configuration, string content, CancellationToken ct)
    {
        var output = configuration.Output;

        if (output.SkipEmptyResults && string.IsNullOrWhiteSpace(content))
            return new ExportOutcome(false, null, 0, "The transform produced no content, so no file was written.");

        if (string.IsNullOrWhiteSpace(output.Directory))
            throw new InvalidOperationException("No output folder is set.");

        Directory.CreateDirectory(output.Directory);

        var fileName = output.BuildFileName(configuration.Transform.Format, DateTimeOffset.Now);
        var path = Path.Combine(output.Directory, fileName);

        // A second run inside the same second must not overwrite the first.
        path = MakeUnique(path);

        // GetBytes never emits the preamble, so the BOM that makes Excel read CSV as UTF-8 is added by hand.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: configuration.Transform.Format == OutputFormat.Csv);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();

        await File.WriteAllBytesAsync(path, bytes, ct);

        _log.LogInformation("Wrote {Bytes} bytes to {Path}", bytes.Length, path);
        return new ExportOutcome(true, path, bytes.Length, null);
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path)) return path;

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{name}_{i}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{name}_{Guid.NewGuid():N}{extension}");
    }
}
