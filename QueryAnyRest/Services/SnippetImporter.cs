using System.Text.Json;
using QueryAnyRest.Models;

namespace QueryAnyRest.Services;

/// <summary>
/// Reads the appsettings-style block that these APIs are normally configured with:
///
///   "OneView": {
///     "AuthUrl": "...", "ApiUrl": "...", "Username": "...", "Password": "...",
///     "ClientId": "...", "ClientSecret": "...", "Scope": "..."
///   }
///
/// One or more named blocks can be pasted at once. Property names are matched
/// case-insensitively and a few common spellings are accepted.
/// </summary>
public static class SnippetImporter
{
    public static List<ApiConfiguration> Parse(string snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
            throw new InvalidOperationException("Paste a settings block first.");

        var text = snippet.Trim().TrimEnd(',');

        // Accept a bare "Name": { ... } fragment by wrapping it into an object.
        if (!text.StartsWith('{')) text = "{" + text + "}";
        if (text.StartsWith("{") && CountUnclosed(text) > 0) text += "}";

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The snippet must be a JSON object.");

        var results = new List<ApiConfiguration>();

        // Either the object itself is one endpoint, or it holds several named ones.
        if (LooksLikeEndpoint(root))
        {
            results.Add(Build("Imported endpoint", root));
        }
        else
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object && LooksLikeEndpoint(property.Value))
                    results.Add(Build(property.Name, property.Value));
            }
        }

        if (results.Count == 0)
            throw new InvalidOperationException(
                "No endpoint was recognised. The block needs at least an ApiUrl or AuthUrl.");

        return results;
    }

    private static int CountUnclosed(string text)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        foreach (var c in text)
        {
            if (escaped) { escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            if (c == '}') depth--;
        }

        return depth;
    }

    private static bool LooksLikeEndpoint(JsonElement element) =>
        Read(element, "ApiUrl", "BaseUrl", "Url") is not null ||
        Read(element, "AuthUrl", "TokenUrl", "TokenEndpoint") is not null;

    private static string? Read(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }

        return null;
    }

    private static ApiConfiguration Build(string name, JsonElement element)
    {
        var authUrl = Read(element, "AuthUrl", "TokenUrl", "TokenEndpoint");

        var configuration = new ApiConfiguration
        {
            Name = name,
            Description = "Imported from a settings block.",
            Steps =
            {
                new QueryStep
                {
                    Name = "Step 1",
                    Request =
                    {
                        BaseUrl = Read(element, "ApiUrl", "BaseUrl", "Url") ?? string.Empty,
                        Method = "GET"
                    }
                }
            },
            Output =
            {
                FileNameBase = Slug(name)
            }
        };

        if (authUrl is not null)
        {
            configuration.Auth.Mode = AuthMode.OAuth2Password;
            configuration.Auth.AuthUrl = authUrl;
            configuration.Auth.Username = Read(element, "Username", "User") ?? string.Empty;
            configuration.Auth.Password = Read(element, "Password", "Secret") ?? string.Empty;
            configuration.Auth.ClientId = Read(element, "ClientId", "Client_Id") ?? string.Empty;
            configuration.Auth.ClientSecret = Read(element, "ClientSecret", "Client_Secret") ?? string.Empty;
            configuration.Auth.Scope = Read(element, "Scope", "Scopes") ?? string.Empty;

            // offline_access is what makes the server hand out a refresh token at all.
            configuration.Auth.UseRefreshToken =
                configuration.Auth.Scope.Contains("offline_access", StringComparison.OrdinalIgnoreCase);
        }

        return configuration;
    }

    private static string Slug(string value)
    {
        var cleaned = new string(value
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray());

        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        return cleaned.Trim('-') is { Length: > 0 } slug ? slug : "export";
    }
}
