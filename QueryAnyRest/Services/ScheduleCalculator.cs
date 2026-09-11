using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>
/// Works out when a configuration is next due. All wall-clock modes use machine
/// local time, so a 06:00 daily export stays at 06:00 across a DST change.
/// </summary>
public static class ScheduleCalculator
{
    public static DateTimeOffset? GetNextRun(ScheduleSettings schedule, DateTimeOffset? lastRunUtc, DateTimeOffset nowUtc)
    {
        if (!schedule.Enabled || schedule.Mode == ScheduleMode.Manual) return null;

        // With no history, the first run happens at the next slot, except for the
        // interval mode, where running straight away is expected.
        if (lastRunUtc is null && schedule.Mode == ScheduleMode.EveryNMinutes)
            return nowUtc;

        var next = NextAfter(schedule, lastRunUtc ?? nowUtc);

        // The machine may have been off. Either fire once on the next poll, or skip
        // ahead to the coming slot and let the missed one go.
        if (next < nowUtc && !schedule.CatchUpMissedRuns)
            next = SkipForward(next, schedule, nowUtc);

        return next.ToUniversalTime();
    }

    /// <summary>
    /// After a run: the first slot after <paramref name="slotUtc"/> that is still ahead. Counting
    /// from the slot rather than the moment the poll noticed it stops the poll delay from
    /// accumulating as drift, and skipping past slots means a catch-up fires only once.
    /// </summary>
    public static DateTimeOffset? GetNextAfterRun(ScheduleSettings schedule, DateTimeOffset slotUtc, DateTimeOffset nowUtc)
    {
        if (!schedule.Enabled || schedule.Mode == ScheduleMode.Manual) return null;

        var next = NextAfter(schedule, slotUtc);
        if (next <= nowUtc) next = SkipForward(next, schedule, nowUtc);

        return next.ToUniversalTime();
    }

    /// <summary>The first slot strictly after the reference moment.</summary>
    private static DateTimeOffset NextAfter(ScheduleSettings schedule, DateTimeOffset reference) => schedule.Mode switch
    {
        ScheduleMode.EveryNMinutes => reference.AddMinutes(IntervalOf(schedule)),
        ScheduleMode.Hourly => NextHourly(reference, schedule),
        ScheduleMode.Daily => NextDaily(reference, schedule),
        ScheduleMode.Weekly => NextWeekly(reference, schedule),
        ScheduleMode.Monthly => NextMonthly(reference, schedule),
        _ => reference
    };

    private static int IntervalOf(ScheduleSettings schedule) => Math.Clamp(schedule.IntervalMinutes, 1, 40320);

    private static DateTimeOffset NextHourly(DateTimeOffset reference, ScheduleSettings schedule)
    {
        var local = ToLocalWallClock(reference);
        var minute = Math.Clamp(schedule.MinuteOfHour, 0, 59);
        var candidate = AtLocal(local.Date.AddHours(local.Hour).AddMinutes(minute));

        // Adding elapsed time rather than wall-clock time keeps an hourly job hourly
        // through the repeated hour in autumn.
        return candidate <= reference ? candidate.AddHours(1) : candidate;
    }

    private static DateTimeOffset NextDaily(DateTimeOffset reference, ScheduleSettings schedule)
    {
        var local = ToLocalWallClock(reference);
        var time = schedule.TimeOfDayValue;

        var candidate = AtLocal(local.Date + time);
        return candidate <= reference ? AtLocal(local.Date.AddDays(1) + time) : candidate;
    }

    private static DateTimeOffset NextWeekly(DateTimeOffset reference, ScheduleSettings schedule)
    {
        var local = ToLocalWallClock(reference);
        var time = schedule.TimeOfDayValue;

        var delta = ((int)schedule.DayOfWeek - (int)local.DayOfWeek + 7) % 7;
        var date = local.Date.AddDays(delta);

        var candidate = AtLocal(date + time);
        return candidate <= reference ? AtLocal(date.AddDays(7) + time) : candidate;
    }

    private static DateTimeOffset NextMonthly(DateTimeOffset reference, ScheduleSettings schedule)
    {
        var local = ToLocalWallClock(reference);
        var time = schedule.TimeOfDayValue;
        var year = local.Year;
        var month = local.Month;

        for (var i = 0; i < 24; i++)
        {
            var day = Math.Clamp(schedule.DayOfMonth, 1, DateTime.DaysInMonth(year, month));
            var candidate = AtLocal(new DateTime(year, month, day) + time);

            if (candidate > reference) return candidate;

            month++;
            if (month > 12) { month = 1; year++; }
        }

        return reference.AddMonths(1);
    }

    private static DateTimeOffset SkipForward(DateTimeOffset missed, ScheduleSettings schedule, DateTimeOffset now)
    {
        if (schedule.Mode == ScheduleMode.EveryNMinutes)
        {
            // Arithmetic rather than a loop: a one-minute job off for a month is 40,000 slots.
            var interval = TimeSpan.FromMinutes(IntervalOf(schedule));
            var slots = (long)Math.Ceiling((now - missed) / interval);
            return missed + interval * slots;
        }

        var next = missed;
        for (var i = 0; i < 100_000 && next < now; i++)
            next = NextAfter(schedule, next);

        return next;
    }

    private static DateTime ToLocalWallClock(DateTimeOffset moment) =>
        TimeZoneInfo.ConvertTime(moment, TimeZoneInfo.Local).DateTime;

    /// <summary>
    /// Pins a local wall-clock time to the offset in force on that date, so 06:00 in
    /// January and 06:00 in July are both 06:00 even though their UTC offsets differ.
    /// </summary>
    private static DateTimeOffset AtLocal(DateTime wallClock)
    {
        var zone = TimeZoneInfo.Local;
        wallClock = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);

        // A time inside the spring-forward gap does not exist; run just after the gap.
        if (zone.IsInvalidTime(wallClock))
            wallClock = wallClock.AddHours(1);

        return new DateTimeOffset(wallClock, zone.GetUtcOffset(wallClock));
    }
}
