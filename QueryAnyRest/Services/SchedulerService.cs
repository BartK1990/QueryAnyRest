using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>
/// Polls the saved configurations and runs the ones that are due. Deliberately
/// simple: a short poll interval beats a timer per job when configurations can
/// be edited while the service is running.
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    private readonly ConfigurationStore _configurations;
    private readonly JobStateStore _jobStates;
    private readonly JobRunner _runner;
    private readonly AppOptions _options;
    private readonly ILogger<SchedulerService> _log;

    public SchedulerService(
        ConfigurationStore configurations,
        JobStateStore jobStates,
        JobRunner runner,
        AppOptions options,
        ILogger<SchedulerService> log)
    {
        _configurations = configurations;
        _jobStates = jobStates;
        _runner = runner;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.SchedulerPollSeconds, 5, 3600));
        _log.LogInformation("Scheduler started, polling every {Seconds}s.", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A bad configuration must never take the scheduler down.
                _log.LogError(ex, "Scheduler poll failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }

        _log.LogInformation("Scheduler stopped.");
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var configurations = await _configurations.GetAllAsync(ct);

        foreach (var configuration in configurations)
        {
            if (!configuration.Schedule.Enabled || configuration.Schedule.Mode == ScheduleMode.Manual)
                continue;

            var state = await _jobStates.GetAsync(configuration.Id, ct);

            // Keep the stored due time in step with edits made in the UI.
            var fingerprint = configuration.Schedule.Fingerprint();
            if (state.ScheduleFingerprint != fingerprint)
            {
                // A state written before fingerprints existed keeps the time it already planned.
                if (state.ScheduleFingerprint is not null || state.NextDueUtc is null)
                    state.NextDueUtc = PlanAfterEdit(configuration.Schedule, state.LastRunUtc, now);

                state.ScheduleFingerprint = fingerprint;
                await _jobStates.SetAsync(state, ct);
            }

            var due = state.NextDueUtc;
            if (due is null || due > now) continue;

            _log.LogInformation("Running {Name}, due at {Due:u}.", configuration.Name, due);
            await _runner.RunAsync(configuration, RunTrigger.Scheduled, writeFile: true, ct);
        }
    }

    /// <summary>
    /// A schedule that was just switched on or edited counts from its last run, but a slot
    /// that fell before the edit was not missed while the machine was off, so it is not
    /// caught up: the schedule then starts from now.
    /// </summary>
    private static DateTimeOffset? PlanAfterEdit(ScheduleSettings schedule, DateTimeOffset? lastRunUtc, DateTimeOffset now)
    {
        var next = ScheduleCalculator.GetNextRun(schedule, lastRunUtc, now);
        return next < now ? ScheduleCalculator.GetNextRun(schedule, now, now) : next;
    }
}
