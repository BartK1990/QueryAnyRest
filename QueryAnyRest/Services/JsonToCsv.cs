using System.Text;
using System.Text.Json;

namespace QueryAnyRest.Services;

/// <summary>
/// The no-script CSV path: finds the first array of objects in the payload and
/// flattens it. Nested objects become dotted column names. When the shape is
/// unusual, write a transform script instead.
/// </summary>
public static class JsonToCsv
{
    public static string Convert(string json, char separator = ',')
    {
        using var document = JsonDocument.Parse(json);

        var array = FindArray(document.RootElement);
        if (array is null) throw new InvalidOperationException(
            "No array was found in the response, so it cannot be flattened into rows. Use a transform script.");

        var rows = new List<Dictionary<string, string>>();
        var columns = new List<string>();

        foreach (var element in array.Value.EnumerateArray())
        {
            var row = new Dictionary<string, string>();
            Flatten(element, prefix: string.Empty, row);

            foreach (var key in row.Keys)
                if (!columns.Contains(key))
                    columns.Add(key);

            rows.Add(row);
        }

        if (rows.Count == 0) return string.Empty;
        if (columns.Count == 0) columns.Add("value");

        var builder = new StringBuilder();
        builder.Append(string.Join(separator, columns.Select(c => Escape(c, separator))));
        builder.Append("\r\n");

        foreach (var row in rows)
        {
            var cells = columns.Select(c => Escape(row.TryGetValue(c, out var v) ? v : string.Empty, separator));
            builder.Append(string.Join(separator, cells));
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    private static JsonElement? FindArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) return element;

        if (element.ValueKind == JsonValueKind.Object)
        {
            // Prefer the conventional wrappers before falling back to the first array found.
            foreach (var name in new[] { "items", "data", "results", "value", "rows" })
            {
                if (element.TryGetProperty(name, out var wrapped) && wrapped.ValueKind == JsonValueKind.Array)
                    return wrapped;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array) return property.Value;
            }
        }

        return null;
    }

    private static void Flatten(JsonElement element, string prefix, IDictionary<string, string> row)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                    Flatten(property.Value, name, row);
                }
                break;

            case JsonValueKind.Array:
                // Arrays inside a row stay as compact JSON, one cell wide.
                row[prefix.Length == 0 ? "value" : prefix] = element.GetRawText();
                break;

            case JsonValueKind.Null or JsonValueKind.Undefined:
                row[prefix.Length == 0 ? "value" : prefix] = string.Empty;
                break;

            case JsonValueKind.Number:
                // The literal as sent: going through double would round large ids
                // and turn big values into exponent notation.
                row[prefix.Length == 0 ? "value" : prefix] = element.GetRawText();
                break;

            default:
                row[prefix.Length == 0 ? "value" : prefix] = element.ToString();
                break;
        }
    }

    private static string Escape(string value, char separator)
    {
        if (value.IndexOfAny(new[] { separator, '"', '\r', '\n' }) < 0) return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
