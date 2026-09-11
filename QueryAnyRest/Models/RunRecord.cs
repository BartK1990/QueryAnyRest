namespace QueryAnyRest.Models;

public enum RunTrigger { Manual, Scheduled }

/// <summary>One execution, kept for the run history page.</summary>
public sealed class RunRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ConfigurationId { get; set; } = string.Empty;
    public string ConfigurationName { get; set; } = string.Empty;
    public RunTrigger Trigger { get; set; }
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public long DurationMs { get; set; }

    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public string? Error { get; set; }
    public string? FilePath { get; set; }
    public long? BytesWritten { get; set; }
    public List<string> ScriptLog { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusText => Success
        ? (FilePath is null ? "OK" : "Written")
        : "Failed";
}
