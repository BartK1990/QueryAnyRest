namespace QueryAnyRest.Models;

public sealed class OutputSettings
{
    /// <summary>Folder the scheduled runs write into. Must be reachable by the service account.</summary>
    public string Directory { get; set; } = @"C:\ProgramData\QueryAnyRest\exports";

    /// <summary>File name without extension; the timestamp is appended to it.</summary>
    public string FileNameBase { get; set; } = "export";

    public bool AppendTimestamp { get; set; } = true;

    public string TimestampFormat { get; set; } = "yyyyMMdd-HHmmss";

    /// <summary>Use local machine time in the file name instead of UTC.</summary>
    public bool UseLocalTimeInName { get; set; } = true;

    /// <summary>Leave empty to derive the extension from the output format.</summary>
    public string? ExtensionOverride { get; set; }

    /// <summary>Skip writing a file when the transform produced nothing.</summary>
    public bool SkipEmptyResults { get; set; } = true;

    public string ExtensionFor(OutputFormat format) =>
        string.IsNullOrWhiteSpace(ExtensionOverride)
            ? format switch
            {
                OutputFormat.Csv => "csv",
                OutputFormat.Json => "json",
                _ => "txt"
            }
            : ExtensionOverride.Trim().TrimStart('.');

    public string BuildFileName(OutputFormat format, DateTimeOffset stamp)
    {
        var name = string.IsNullOrWhiteSpace(FileNameBase) ? "export" : FileNameBase.Trim();

        if (AppendTimestamp)
        {
            var moment = UseLocalTimeInName ? stamp.LocalDateTime : stamp.UtcDateTime;
            // An invalid format falls back to the default rather than failing the run;
            // Validate() reports it before it can be saved.
            name = $"{name}-{FormatStamp(moment, TimestampFormat) ?? FormatStamp(moment, DefaultTimestampFormat)}";
        }

        foreach (var bad in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');

        return $"{name}.{ExtensionFor(format)}";
    }

    public const string DefaultTimestampFormat = "yyyyMMdd-HHmmss";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasValidTimestampFormat =>
        FormatStamp(DateTime.Now, TimestampFormat) is not null;

    /// <summary>Invariant culture, so the separators in the name do not depend on the machine's locale.</summary>
    private static string? FormatStamp(DateTime moment, string? format)
    {
        try
        {
            return moment.ToString(
                string.IsNullOrWhiteSpace(format) ? DefaultTimestampFormat : format,
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
