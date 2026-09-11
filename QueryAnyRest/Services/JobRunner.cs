using System.Diagnostics;
using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>
/// The record that goes into the history, plus every step's outcome and the final
/// transform kept in memory for the caller. Only <see cref="Record"/> is persisted.
/// </summary>
public sealed record RunOutcome(RunRecord Record, IReadOnlyList<StepOutcome> Steps, TransformResult? Transform);

/// <summary>
/// One execution end to end: run the chain, transform, write. Shared by the Run
/// button and by the scheduler so both behave identically, and so a manual run
/// with "write file" ticked calls every endpoint in the chain exactly once.
/// </summary>
public sealed class JobRunner
{
    private readonly QueryChainService _chain;
    private readonly TransformService _transform;
    private readonly ExportService _export;
    private readonly RunHistory _history;
    private readonly JobStateStore _jobStates;
    private readonly ILogger<JobRunner> _log;

    public JobRunner(
        QueryChainService chain,
        TransformService transform,
        ExportService export,
        RunHistory history,
        JobStateStore jobStates,
        ILogger<JobRunner> log)
    {
        _chain = chain;
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

        ChainOutcome? chainOutcome = null;
        TransformResult? transformed = null;

        try
        {
            chainOutcome = await _chain.RunAsync(configuration, ct);

            record.ScriptLog.AddRange(chainOutcome.Steps
                .SelectMany(step => step.BuildScriptLog.Select(line => $"[{step.StepName}] {line}")));

            var lastStatus = chainOutcome.Steps.Count > 0 ? chainOutcome.Steps[^1].Response.StatusCode : 0;
            record.StatusCode = lastStatus == 0 ? null : lastStatus;

            if (!chainOutcome.Success)
            {
                record.Error = chainOutcome.Error ?? "The request failed.";
                return await FinishAsync(configuration, record, chainOutcome, transformed, stopwatch);
            }

            transformed = _transform.Apply(configuration, chainOutcome.Steps, chainOutcome.FinalContextJson);
            record.ScriptLog.AddRange(transformed.Log);

            if (!transformed.Success)
            {
                record.Error = transformed.Error;
                return await FinishAsync(configuration, record, chainOutcome, transformed, stopwatch);
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

        return await FinishAsync(configuration, record, chainOutcome, transformed, stopwatch);
    }

    private async Task<RunOutcome> FinishAsync(
        ApiConfiguration configuration,
        RunRecord record,
        ChainOutcome? chainOutcome,
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

        return new RunOutcome(record, chainOutcome?.Steps ?? Array.Empty<StepOutcome>(), transformed);
    }
}
