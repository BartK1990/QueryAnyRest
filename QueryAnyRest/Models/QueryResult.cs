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
