namespace QueryAnyRest.Models;

/// <summary>
/// One call in a chain. Its own request, and an optional script that fills in
/// parts of that request from the calls that already ran.
/// </summary>
public sealed class QueryStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Step";

    public RequestSettings Request { get; set; } = new();

    /// <summary>When off, the request above is sent exactly as configured.</summary>
    public bool UseBuildScript { get; set; }

    /// <summary>
    /// JavaScript run before this step's request is sent. Receives <c>request</c> (this
    /// step's call — mutate it in place), <c>results</c> (every earlier step's outcome),
    /// <c>previous</c> (the last entry of <c>results</c>, or null on the first step),
    /// <c>context</c> (a plain object carried across every step and into the final
    /// script) and <c>log(x)</c> (writes to the run log). Nothing needs to be returned.
    /// </summary>
    public string BuildScript { get; set; } = DefaultBuildScript;

    public int BuildScriptTimeoutSeconds { get; set; } = 10;

    public const string DefaultBuildScript = """
// Runs before this step's request is sent.
//   request  - this step's call: request.path, request.query.NAME, request.headers.NAME,
//              request.body, request.method. Mutate it in place.
//   results  - earlier steps: results[0].status, results[0].json, results[0].response, ...
//   previous - results[results.length - 1], or null on the first step
//   context  - a plain object shared across every step and the final script
//   log(x)   - writes to the run log

// Example: use an id found in the previous step's response to build this one.
// request.path = '/devices/' + previous.json.items[0].id;
""";
}
