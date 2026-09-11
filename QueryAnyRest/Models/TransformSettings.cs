namespace QueryAnyRest.Models;

public enum OutputFormat
{
    /// <summary>The response body exactly as it arrived.</summary>
    Raw,
    /// <summary>Indented JSON.</summary>
    Json,
    /// <summary>Comma-separated rows.</summary>
    Csv
}

public sealed class TransformSettings
{
    public OutputFormat Format { get; set; } = OutputFormat.Json;

    /// <summary>When off, JSON is only re-indented and CSV is flattened automatically.</summary>
    public bool UseScript { get; set; }

    /// <summary>
    /// JavaScript run against every response. Receives <c>response</c> (the raw text) and
    /// <c>json</c> (the parsed body, or null when the body is not JSON). Return a string,
    /// or an object that will be stringified.
    /// </summary>
    public string Script { get; set; } = DefaultCsvScript;

    public int ScriptTimeoutSeconds { get; set; } = 10;

    public const string DefaultCsvScript = """
// Runs on every response.
//   response - raw body text
//   json     - parsed body, or null when the body is not JSON
//   log(x)   - writes to the run log
// Return a string; anything else is stringified.

var rows = Array.isArray(json) ? json : (json && json.items) || [];
if (!rows.length) return '';

var columns = Object.keys(rows[0]);

function cell(value) {
  if (value === null || value === undefined) return '';
  var text = (typeof value === 'object') ? JSON.stringify(value) : String(value);
  return /[",\n;]/.test(text) ? '"' + text.replace(/"/g, '""') + '"' : text;
}

var lines = [columns.join(',')];
for (var i = 0; i < rows.length; i++) {
  lines.push(columns.map(function (c) { return cell(rows[i][c]); }).join(','));
}
return lines.join('\r\n');
""";

    public const string DefaultJsonScript = """
// Runs on every response.
//   response - raw body text
//   json     - parsed body, or null when the body is not JSON
//   log(x)   - writes to the run log

var rows = Array.isArray(json) ? json : (json && json.items) || [];

return {
  capturedUtc: new Date().toISOString(),
  count: rows.length,
  items: rows
};
""";
}
