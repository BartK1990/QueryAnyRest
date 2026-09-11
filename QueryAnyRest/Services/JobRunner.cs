using System.Diagnostics;
using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>
/// The record that goes into the history, plus the response and transform kept in
/// memory for the caller. Only <see cref="Record"/> is persisted.
/// </summary>
public sealed record RunOutcome(RunRecord Record, QueryResult? Response, TransformResult? Transform);

/// <summary>
/// One execution end to end: call, transform, write. Shared by the Run button
/// and by the scheduler so both behave identically, and so a manual run with
/// "write file" ticked calls the endpoint exactly once.
/// </summary>
public sealed class JobRunner
{
    private readonly RestQueryService _rest;
    private readonly TransformService _transform;
    private readonly ExportService _export;
    private readonly RunHistory _history;
    private readonly JobStateStore _jobStates;
    private readonly ILogger<JobRunner> _log;

    public JobRunner(
        RestQueryService rest,
        TransformService transform,
        ExportService export,
        RunHistory history,
        JobStateStore jobStates,
        ILogger<JobRunner> log)
    {
        _rest = rest;
        _transform = transform;
        _export = export;
        _history = history;
        _jobStates = jobStates;
        _log = log;
    }

    public async Task<RunOutcome> RunAsync(
        ApiConfiguration configuration,
        RunTrigger trigger,
        bool writeFile,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var record = new RunRecord
        {
            ConfigurationId = configuration.Id,
            ConfigurationName = configuration.Name,
            Trigger = trigger
        };

        QueryResult? response = null;
        TransformResult? transformed = null;

        try
        {
            response = await _rest.ExecuteAsync(configuration, ct);
            record.StatusCode = response.StatusCode == 0 ? null : response.StatusCode;

            if (!response.Success)
            {
                record.Error = response.Error ?? "The request failed.";
                return await FinishAsync(configuration, record, response, transformed, stopwatch);
            }

            transformed = _transform.Apply(configuration, response.Body);
            record.ScriptLog.AddRange(transformed.Log);

            if (!transformed.Success)
            {
                record.Error = transformed.Error;
                return await FinishAsync(configuration, record, response, transformed, stopwatch);
            }

            if (writeFile)
            {
                var outcome = await _export.WriteAsync(configuration, transformed.Content, ct);
                record.FilePath = outcome.Path;
                record.BytesWritten = outcome.Bytes;

                if (!outcome.Written && outcome.Reason is not null)
                    record.ScriptLog.Add(outcome.Reason);
            }

            record.Success = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            record.Error = "The run was cancelled because the service is stopping.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Run failed for {Name}", configuration.Name);
            record.Error = ex.Message;
        }

        return await FinishAsync(configuration, record, response, transformed, stopwatch);
    }

    private async Task<RunOutcome> FinishAsync(
        ApiConfiguration configuration,
        RunRecord record,
        QueryResult? response,
        TransformResult? transformed,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        record.DurationMs = stopwatch.ElapsedMilliseconds;

        // Bookkeeping must survive a shutdown mid-run, so it ignores the caller's token.
        var state = await _jobStates.GetAsync(configuration.Id, CancellationToken.None);
        state.LastRunUtc = record.StartedUtc;
        state.LastError = record.Error;
        state.LastFilePath = record.FilePath ?? state.LastFilePath;

        if (record.Success)
        {
            state.LastSuccessUtc = record.StartedUtc;
            state.ConsecutiveFailures = 0;
        }
        else
        {
            state.ConsecutiveFailures++;
        }

        // A scheduled run counts from the slot it was due at; a manual one restarts the rhythm.
        var slot = record.Trigger == RunTrigger.Scheduled && state.NextDueUtc is { } due
            ? due
            : record.StartedUtc;

        state.NextDueUtc = ScheduleCalculator.GetNextAfterRun(configuration.Schedule, slot, DateTimeOffset.UtcNow);
        state.ScheduleFingerprint = configuration.Schedule.Fingerprint();

        await _jobStates.SetAsync(state, CancellationToken.None);
        await _history.AddAsync(record, CancellationToken.None);

        return new RunOutcome(record, response, transformed);
    }
}
