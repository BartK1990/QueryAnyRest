using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>Every step that ran, whether the chain made it through, and its shared context.</summary>
public sealed record ChainOutcome(IReadOnlyList<StepOutcome> Steps, bool Success, string? Error, string FinalContextJson);

/// <summary>
/// Runs a configuration's steps in order, building each one's request from the
/// steps before it when it asks to. Stops at the first failing step.
/// </summary>
public sealed class QueryChainService
{
    private readonly RequestBuilderService _builder;
    private readonly RestQueryService _rest;

    public QueryChainService(RequestBuilderService builder, RestQueryService rest)
    {
        _builder = builder;
        _rest = rest;
    }

    public async Task<ChainOutcome> RunAsync(ApiConfiguration configuration, CancellationToken ct)
    {
        var steps = new List<StepOutcome>();
        var contextJson = "{}";

        foreach (var step in configuration.Steps)
        {
            var buildLog = new List<string>();
            RequestSettings effectiveRequest;

            try
            {
                effectiveRequest = _builder.Build(step, steps, ref contextJson, buildLog);
            }
            catch (Exception ex)
            {
                var failed = new StepOutcome { StepId = step.Id, StepName = step.Name, BuildScriptLog = buildLog };
                failed.Response.Error = ex.Message;
                steps.Add(failed);
                return new ChainOutcome(steps, false, $"Step '{step.Name}': {ex.Message}", contextJson);
            }

            var response = await _rest.ExecuteAsync(configuration, effectiveRequest, ct);
            steps.Add(new StepOutcome
            {
                StepId = step.Id,
                StepName = step.Name,
                Response = response,
                BuildScriptLog = buildLog
            });

            if (!response.Success)
                return new ChainOutcome(steps, false, $"Step '{step.Name}': {response.Error}", contextJson);
        }

        return new ChainOutcome(steps, true, null, contextJson);
    }
}
