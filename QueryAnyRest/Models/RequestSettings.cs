using System.Text;
using System.Text.Json.Serialization;

namespace QueryAnyRest.Models;

public sealed class RequestSettings
{
    /// <summary>Root of the API, e.g. http://10.64.2.142/oneview4/api/nbi</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Appended to the API URL, e.g. /alarms/active</summary>
    public string Path { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public List<KeyValueItem> QueryParameters { get; set; } = new();
    public List<KeyValueItem> Headers { get; set; } = new();

    public string Body { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/json";

    public int TimeoutSeconds { get; set; } = 100;

    /// <summary>For appliances with a self-signed certificate. Off by default, on purpose.</summary>
    public bool AllowInvalidCertificates { get; set; }

    [JsonIgnore]
    public bool SendsBody =>
        !string.IsNullOrWhiteSpace(Body) &&
        !string.Equals(Method, "GET", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Method, "HEAD", StringComparison.OrdinalIgnoreCase);

    /// <summary>Joins the API URL, the path and the enabled query parameters into one address.</summary>
    public string BuildUrl()
    {
        var root = (BaseUrl ?? string.Empty).Trim();
        var path = (Path ?? string.Empty).Trim();

        var url = new StringBuilder(root.TrimEnd('/'));
        if (path.Length > 0)
        {
            url.Append('/');
            url.Append(path.TrimStart('/'));
        }

        var pairs = QueryParameters
            .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Key))
            .Select(p => $"{Uri.EscapeDataString(p.Key.Trim())}={Uri.EscapeDataString(p.Value ?? string.Empty)}")
            .ToList();

        if (pairs.Count > 0)
        {
            var full = url.ToString();
            url.Append(full.Contains('?') ? '&' : '?');
            url.Append(string.Join('&', pairs));
        }

        return url.ToString();
    }
}
