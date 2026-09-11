using System.Diagnostics;
using System.Text.Json;
using Jint;
using Jint.Native;
using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>
/// Turns a chain's step outcomes into the text that gets written to disk, either by
/// re-shaping the last response automatically or by running the configuration's
/// JavaScript. The script runs in Jint with a time and memory limit and no access
/// to the host.
/// </summary>
public sealed class TransformService
{
    private readonly ILogger<TransformService> _log;

    public TransformService(ILogger<TransformService> log) => _log = log;

    public TransformResult Apply(ApiConfiguration configuration, IReadOnlyList<StepOutcome> steps, string contextJson)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = configuration.Transform;

        try
        {
            var log = new List<string>();
            var content = settings.UseScript
                ? RunScript(settings, steps, contextJson, log)
                : Automatic(settings.Format, steps[^1].Response.Body);

            var result = new TransformResult { Success = true, Content = content };
            result.Log.AddRange(log);
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Transform failed for {Name}", configuration.Name);
            return new TransformResult
            {
                Success = false,
                Error = ex.Message,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };
        }
    }

    private static string Automatic(OutputFormat format, string body) => format switch
    {
        OutputFormat.Raw => body,
        OutputFormat.Csv => JsonToCsv.Convert(body),
        OutputFormat.Json => PrettyPrint(body),
        _ => body
    };

    private static string PrettyPrint(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(document.RootElement, JsonDefaults.Pretty);
        }
        catch (JsonException)
        {
            // Not JSON: pass it through rather than failing the run.
            return body;
        }
    }

    private static string RunScript(TransformSettings settings, IReadOnlyList<StepOutcome> steps, string contextJson, List<string> messages)
    {
        var seconds = Math.Clamp(settings.ScriptTimeoutSeconds, 1, 300);
        var totalLength = steps.Sum(s => s.Response.Body.Length);

        var engine = JintSandbox.CreateEngine(seconds, JintSandbox.MemoryBudgetFor(totalLength));

        engine.SetValue("__resultsJson", JintSandbox.BuildResultsJson(steps));
        engine.SetValue("__contextJson", string.IsNullOrWhiteSpace(contextJson) ? "{}" : contextJson);
        engine.SetValue("__log", new Action<string?>(value =>
        {
            if (messages.Count < 200) messages.Add(value ?? "null");
        }));

        // The user's script becomes the body of a function so that `return` works
        // and its local variables cannot collide with the host globals.
        // log() stringifies objects in JavaScript, where they still are objects.
        // response/json stay bound to the last step, so a single-step script written
        // before chaining existed keeps working unchanged.
        var program = """
            var log = function (x) {
              var text;
              try { text = typeof x === 'string' ? x : JSON.stringify(x); } catch (e) { text = undefined; }
              __log(text === undefined ? String(x) : text);
            };
            var results = JSON.parse(__resultsJson);
            var context = JSON.parse(__contextJson);
            var response = results.length ? results[results.length - 1].response : null;
            var json = results.length ? results[results.length - 1].json : null;
            var __run = function (response, json, results, context) {
            """
            + "\n" + settings.Script + "\n" +
            """
            };
            __run(response, json, results, context);
            """;

        JsValue value;
        try
        {
            value = engine.Evaluate(program);
        }
        catch (Exception ex)
        {
            throw JintSandbox.Translate(ex, seconds);
        }

        if (value.IsNull() || value.IsUndefined()) return string.Empty;
        if (value.IsString()) return value.AsString();

        engine.SetValue("__result", value);
        return engine.Evaluate("JSON.stringify(__result, null, 2)").AsString();
    }
}
