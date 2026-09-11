namespace QueryAnyRest.Models;

/// <summary>The raw HTTP answer, before any transform.</summary>
public sealed class QueryResult
{
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string ReasonPhrase { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long ElapsedMs { get; set; }
    public string RequestUrl { get; set; } = string.Empty;
    public string RequestMethod { get; set; } = string.Empty;

    /// <summary>
    /// The headers this step (or its build script) configured — never the injected
    /// Authorization value, so this is always safe to show and keep in the history.
    /// </summary>
    public Dictionary<string, string> RequestHeaders { get; set; } = new();
    public string RequestBody { get; set; } = string.Empty;

    public string? Error { get; set; }
    public Dictionary<string, string> ResponseHeaders { get; set; } = new();

    public bool LooksLikeJson
    {
        get
        {
            if (ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return true;
            var trimmed = Body.TrimStart();
            return trimmed.StartsWith('{') || trimmed.StartsWith('[');
        }
    }
}

/// <summary>The result of running the transform over a response body.</summary>
public sealed class TransformResult
{
    public bool Success { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? Error { get; set; }
    public List<string> Log { get; set; } = new();
    public long ElapsedMs { get; set; }
}

/// <summary>One step's place in a chain: which step it was, what it asked, what came back.</summary>
public sealed class StepOutcome
{
    public string StepId { get; set; } = string.Empty;
    public string StepName { get; set; } = string.Empty;
    public QueryResult Response { get; set; } = new();
    public List<string> BuildScriptLog { get; set; } = new();
}
