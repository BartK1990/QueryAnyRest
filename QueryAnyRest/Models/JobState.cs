namespace QueryAnyRest.Models;

/// <summary>What the scheduler remembers between polls and across restarts.</summary>
public sealed class JobState
{
    public string ConfigurationId { get; set; } = string.Empty;
    public DateTimeOffset? LastRunUtc { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public DateTimeOffset? NextDueUtc { get; set; }

    /// <summary>The schedule <see cref="NextDueUtc"/> was planned for. A mismatch means it was edited.</summary>
    public string? ScheduleFingerprint { get; set; }
    public string? LastError { get; set; }
    public string? LastFilePath { get; set; }
    public int ConsecutiveFailures { get; set; }
}
