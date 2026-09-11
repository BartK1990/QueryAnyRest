using System.Text.Json.Serialization;

namespace QueryAnyRest.Models;

public enum ScheduleMode
{
    /// <summary>Only runs when you press Run.</summary>
    Manual,
    /// <summary>Every N minutes.</summary>
    EveryNMinutes,
    /// <summary>Every hour, at a chosen minute.</summary>
    Hourly,
    /// <summary>Once a day, at a chosen time.</summary>
    Daily,
    /// <summary>Once a week, on a chosen day and time.</summary>
    Weekly,
    /// <summary>Once a month, on a chosen day and time.</summary>
    Monthly
}

public sealed class ScheduleSettings
{
    public bool Enabled { get; set; }

    public ScheduleMode Mode { get; set; } = ScheduleMode.Manual;

    /// <summary>Used by EveryNMinutes.</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Used by Hourly.</summary>
    public int MinuteOfHour { get; set; }

    /// <summary>Used by Daily, Weekly and Monthly. Stored as HH:mm in machine local time.</summary>
    public string TimeOfDay { get; set; } = "06:00";

    /// <summary>Used by Weekly.</summary>
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;

    /// <summary>Used by Monthly. Clamped to the length of the month.</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Run once at start-up when the machine was off at the scheduled moment.</summary>
    public bool CatchUpMissedRuns { get; set; } = true;

    [JsonIgnore]
    public TimeSpan TimeOfDayValue =>
        TimeSpan.TryParse(TimeOfDay, out var parsed) ? parsed : new TimeSpan(6, 0, 0);

    /// <summary>Changes whenever an edit would move the next run, so the scheduler can re-plan.</summary>
    public string Fingerprint() =>
        $"{Enabled}|{Mode}|{IntervalMinutes}|{MinuteOfHour}|{TimeOfDayValue}|{DayOfWeek}|{DayOfMonth}|{CatchUpMissedRuns}";

    public string Describe() => Mode switch
    {
        ScheduleMode.EveryNMinutes => IntervalMinutes == 1 ? "Every minute" : $"Every {IntervalMinutes} minutes",
        ScheduleMode.Hourly => $"Every hour at :{MinuteOfHour:00}",
        ScheduleMode.Daily => $"Every day at {TimeOfDayValue:hh\\:mm}",
        ScheduleMode.Weekly => $"Every {DayOfWeek} at {TimeOfDayValue:hh\\:mm}",
        ScheduleMode.Monthly => $"Day {DayOfMonth} of each month at {TimeOfDayValue:hh\\:mm}",
        _ => "Manual"
    };
}
