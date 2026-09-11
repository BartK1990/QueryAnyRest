using System.Text.Json.Nodes;
using Jint;
using Jint.Runtime;
using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>
/// Creates the sandboxed Jint engine shared by every script this app runs
/// (the transform and the per-step request builder), and turns its failure
/// modes into one friendly message.
/// </summary>
public static class JintSandbox
{
    public static Engine CreateEngine(int timeoutSeconds, long memoryBudget) =>
        new(options =>
        {
            options.TimeoutInterval(TimeSpan.FromSeconds(timeoutSeconds));
            options.LimitMemory(memoryBudget);
        });

    /// <summary>
    /// Jint's memory limit counts every byte allocated during the run, not what is alive,
    /// so a fixed cap rejects ordinary scripts on large responses. The budget is a flat
    /// allowance plus a multiple of the data the script can see, which still stops a
    /// runaway loop.
    /// </summary>
    public static long MemoryBudgetFor(int dataLength) =>
        256L * 1024 * 1024 + 64L * dataLength;

    public static InvalidOperationException Translate(Exception ex, int timeoutSeconds) => ex switch
    {
        JavaScriptException jse => new InvalidOperationException($"Script error: {jse.Message}"),
        TimeoutException => new InvalidOperationException($"The script was stopped after {timeoutSeconds}s."),
        MemoryLimitExceededException mle => new InvalidOperationException($"The script was stopped for using too much memory ({mle.Message})."),
        _ => new InvalidOperationException($"Script error: {ex.Message}")
    };

    /// <summary>
    /// Serializes earlier steps into the shape scripts see as <c>results</c>: each entry's
    /// <c>json</c> is the actually-parsed body (or null), not a string to parse again.
    /// </summary>
    public static string BuildResultsJson(IReadOnlyList<StepOutcome> steps)
    {
        var array = new JsonArray();

        foreach (var step in steps)
        {
            var response = step.Response;

            JsonNode? parsed;
            try { parsed = JsonNode.Parse(response.Body); }
            catch { parsed = null; }

            var headers = new JsonObject();
            foreach (var header in response.ResponseHeaders)
                headers[header.Key] = header.Value;

            array.Add(new JsonObject
            {
                ["name"] = step.StepName,
                ["success"] = response.Success,
                ["status"] = response.StatusCode == 0 ? null : JsonValue.Create(response.StatusCode),
                ["statusText"] = response.ReasonPhrase,
                ["error"] = response.Error,
                ["response"] = response.Body,
                ["json"] = parsed,
                ["headers"] = headers
            });
        }

        return array.ToJsonString();
    }
}
