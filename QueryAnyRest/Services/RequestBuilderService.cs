using System.Text.Json.Nodes;
using Jint;
using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>
/// Runs a step's "build this request" script, if it has one, and turns the script's
/// output into the <see cref="RequestSettings"/> actually sent. Sandboxed the same
/// way as the final transform script: the script only ever shapes a plain object,
/// it never reaches the network itself.
/// </summary>
public sealed class RequestBuilderService
{
    public RequestSettings Build(QueryStep step, IReadOnlyList<StepOutcome> priorSteps, ref string contextJson, List<string> log)
    {
        // Nothing has run yet for the first step, so a build script here has nothing to
        // read (results is [], previous is null) and can only fail. Ignore the flag
        // rather than trust it — it can be left over from before this step was first,
        // or from hand-edited JSON.
        if (!step.UseBuildScript || priorSteps.Count == 0) return step.Request;

        var seconds = Math.Clamp(step.BuildScriptTimeoutSeconds, 1, 300);
        var requestJson = BuildRequestJson(step.Request);
        var totalLength = requestJson.Length + priorSteps.Sum(s => s.Response.Body.Length);

        var engine = JintSandbox.CreateEngine(seconds, JintSandbox.MemoryBudgetFor(totalLength));

        engine.SetValue("__requestJson", requestJson);
        engine.SetValue("__resultsJson", JintSandbox.BuildResultsJson(priorSteps));
        engine.SetValue("__contextJson", string.IsNullOrWhiteSpace(contextJson) ? "{}" : contextJson);
        engine.SetValue("__log", new Action<string?>(value =>
        {
            if (log.Count < 200) log.Add(value ?? "null");
        }));

        // The user's script becomes the body of a function so that its local variables
        // cannot collide with the host globals. It mutates `request` and `context` in
        // place rather than returning anything.
        var program = """
            var log = function (x) {
              var text;
              try { text = typeof x === 'string' ? x : JSON.stringify(x); } catch (e) { text = undefined; }
              __log(text === undefined ? String(x) : text);
            };
            var request = JSON.parse(__requestJson);
            var results = JSON.parse(__resultsJson);
            var context = JSON.parse(__contextJson);
            var previous = results.length ? results[results.length - 1] : null;
            var __run = function (request, results, previous, context) {
            """
            + "\n" + step.BuildScript + "\n" +
            """
            };
            __run(request, results, previous, context);
            """;

        try
        {
            engine.Evaluate(program);
            var updatedRequestJson = engine.Evaluate("JSON.stringify(request)").AsString();
            contextJson = engine.Evaluate("JSON.stringify(context)").AsString();
            return ApplyRequestJson(step.Request, updatedRequestJson);
        }
        catch (Exception ex)
        {
            throw JintSandbox.Translate(ex, seconds);
        }
    }

    private static string BuildRequestJson(RequestSettings request)
    {
        // A key used more than once (e.g. unitIdentifiers=a&unitIdentifiers=b) becomes
        // a JS array so the script can read and rebuild every value, not just the last one.
        var query = BuildParameterObject(request.QueryParameters);
        var headers = BuildParameterObject(request.Headers);

        var obj = new JsonObject
        {
            ["method"] = request.Method,
            ["baseUrl"] = request.BaseUrl,
            ["path"] = request.Path,
            ["query"] = query,
            ["headers"] = headers,
            ["body"] = request.Body,
            ["contentType"] = request.ContentType
        };

        return obj.ToJsonString();
    }

    private static JsonObject BuildParameterObject(List<KeyValueItem> items)
    {
        var obj = new JsonObject();

        foreach (var group in items
            .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Key))
            .GroupBy(p => p.Key.Trim()))
        {
            var values = group.Select(p => p.Value).ToList();
            obj[group.Key] = values.Count == 1
                ? JsonValue.Create(values[0])
                : new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }

        return obj;
    }

    /// <summary>
    /// Rebuilds a request from the script's output. The timeout and the "accept any
    /// certificate" flag are not exposed to the script, so they always come from
    /// what the step has configured.
    /// </summary>
    private static RequestSettings ApplyRequestJson(RequestSettings original, string json)
    {
        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("The build script did not leave a usable request behind.");

        var result = new RequestSettings
        {
            Method = ValueToString(node["method"]) ?? original.Method,
            BaseUrl = ValueToString(node["baseUrl"]) ?? original.BaseUrl,
            Path = ValueToString(node["path"]) ?? original.Path,
            Body = ValueToString(node["body"]) ?? string.Empty,
            ContentType = ValueToString(node["contentType"]) ?? original.ContentType,
            TimeoutSeconds = original.TimeoutSeconds,
            AllowInvalidCertificates = original.AllowInvalidCertificates
        };

        if (node["query"] is JsonObject query)
            AddParameters(result.QueryParameters, query);

        if (node["headers"] is JsonObject headers)
            AddParameters(result.Headers, headers);

        return result;
    }

    /// <summary>An array value becomes one row per element, so the same key can repeat in the URL.</summary>
    private static void AddParameters(List<KeyValueItem> target, JsonObject source)
    {
        foreach (var pair in source)
        {
            if (pair.Value is JsonArray array)
            {
                foreach (var element in array)
                    target.Add(new KeyValueItem { Key = pair.Key, Value = ValueToString(element) ?? string.Empty, Enabled = true });
            }
            else
            {
                target.Add(new KeyValueItem { Key = pair.Key, Value = ValueToString(pair.Value) ?? string.Empty, Enabled = true });
            }
        }
    }

    /// <summary>
    /// A script naturally sets these fields to whatever it has on hand (a number id,
    /// a boolean flag), not just strings, so this accepts any scalar rather than only
    /// a JSON string.
    /// </summary>
    private static string? ValueToString(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        return node.ToJsonString().Trim('"');
    }
}
