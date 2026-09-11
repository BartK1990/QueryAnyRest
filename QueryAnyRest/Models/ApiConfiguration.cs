using System.Text.Json;

namespace QueryAnyRest.Models;

/// <summary>
/// One saved endpoint definition: how to authenticate, what to call,
/// how to reshape the answer, where to put it and how often.
/// </summary>
public sealed class ApiConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New configuration";
    public string Description { get; set; } = string.Empty;

    public AuthSettings Auth { get; set; } = new();
    public RequestSettings Request { get; set; } = new();
    public TransformSettings Transform { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public ScheduleSettings Schedule { get; set; } = new();

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;

    public ApiConfiguration Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonDefaults.Storage);
        return JsonSerializer.Deserialize<ApiConfiguration>(json, JsonDefaults.Storage)!;
    }

    /// <summary>Returns the problems that block saving; empty when the configuration is usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            problems.Add("Give the configuration a name.");

        if (string.IsNullOrWhiteSpace(Request.BaseUrl))
            problems.Add("Set the API URL.");
        else if (!Uri.TryCreate(Request.BuildUrl(), UriKind.Absolute, out _))
            problems.Add("The API URL and path do not form a valid absolute address.");

        if (Auth.Mode == AuthMode.OAuth2Password)
        {
            if (string.IsNullOrWhiteSpace(Auth.AuthUrl))
                problems.Add("OAuth 2.0 needs a token URL.");
            if (string.IsNullOrWhiteSpace(Auth.Username))
                problems.Add("The password grant needs a user name.");
            if (string.IsNullOrWhiteSpace(Auth.ClientId))
                problems.Add("OAuth 2.0 needs a client id.");
        }

        if (Auth.Mode == AuthMode.StaticBearer && string.IsNullOrWhiteSpace(Auth.StaticToken))
            problems.Add("Paste the bearer token, or switch the authentication mode.");

        if (Auth.Mode == AuthMode.OAuth2Password && !string.IsNullOrWhiteSpace(Auth.AuthUrl) &&
            !Uri.TryCreate(Auth.AuthUrl.Trim(), UriKind.Absolute, out _))
            problems.Add("The token URL is not a valid absolute address.");

        if (Output.AppendTimestamp && !Output.HasValidTimestampFormat)
            problems.Add("The timestamp format is not a valid date format, e.g. yyyyMMdd-HHmmss.");

        if (Schedule.Enabled && string.IsNullOrWhiteSpace(Output.Directory))
            problems.Add("A scheduled query needs an output folder to write into.");

        if (Schedule.Enabled && Schedule.Mode == ScheduleMode.Manual)
            problems.Add("Pick how often the query should run, or turn the schedule off.");

        return problems;
    }
}
