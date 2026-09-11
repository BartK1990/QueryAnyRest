using System.Diagnostics;
using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Runtime;
using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>
/// Turns a response body into the text that gets written to disk, either by
/// re-shaping it automatically or by running the configuration's JavaScript.
/// The script runs in Jint with a time and memory limit and no access to the host.
/// </summary>
public sealed class TransformService
{
    private readonly ILogger<TransformService> _log;

    public TransformService(ILogger<TransformService> log) => _log = log;

    public TransformResult Apply(ApiConfiguration configuration, string body)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = configuration.Transform;

        try
        {
            var log = new List<string>();
            var content = settings.UseScript
                ? RunScript(settings, body, log)
                : Automatic(settings.Format, body);

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

    private static string RunScript(TransformSettings settings, string body, List<string> messages)
    {
        var seconds = Math.Clamp(settings.ScriptTimeoutSeconds, 1, 300);

        var engine = new Engine(options =>
        {
            options.TimeoutInterval(TimeSpan.FromSeconds(seconds));
            options.LimitMemory(MemoryBudgetFor(body));
        });

        engine.SetValue("responseText", body);
        engine.SetValue("__log", new Action<string?>(value =>
        {
            if (messages.Count < 200) messages.Add(value ?? "null");
        }));

        // The user's script becomes the body of a function so that `return` works
        // and its local variables cannot collide with the host globals.
        // log() stringifies objects in JavaScript, where they still are objects.
        var program = """
            var log = function (x) {
              var text;
              try { text = typeof x === 'string' ? x : JSON.stringify(x); } catch (e) { text = undefined; }
              __log(text === undefined ? String(x) : text);
            };
            var __parsed = null;
            try { __parsed = JSON.parse(responseText); } catch (e) { __parsed = null; }
            var __run = function (response, json) {
            """
            + "\n" + settings.Script + "\n" +
            """
            };
            __run(responseText, __parsed);
            """;

        JsValue value;
        try
        {
            value = engine.Evaluate(program);
        }
        catch (JavaScriptException ex)
        {
            throw new InvalidOperationException($"Script error: {ex.Message}");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"The script was stopped after {seconds}s.");
        }
        catch (MemoryLimitExceededException ex)
        {
            throw new InvalidOperationException($"The script was stopped for using too much memory ({ex.Message}).");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Script error: {ex.Message}");
        }

        if (value.IsNull() || value.IsUndefined()) return string.Empty;
        if (value.IsString()) return value.AsString();

        engine.SetValue("__result", value);
        return engine.Evaluate("JSON.stringify(__result, null, 2)").AsString();
    }

    /// <summary>
    /// Jint's memory limit counts every byte allocated during the run, not what is alive,
    /// so a fixed cap rejects ordinary scripts on large responses. The budget is a flat
    /// allowance plus a multiple of the body, which still stops a runaway loop.
    /// </summary>
    private static long MemoryBudgetFor(string body) =>
        256L * 1024 * 1024 + 64L * body.Length;
}
