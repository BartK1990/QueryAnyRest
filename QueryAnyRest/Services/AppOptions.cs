namespace QueryAnyRest.Services;

public sealed class AppOptions
{
    public const string SectionName = "QueryAnyRest";

    /// <summary>Where configurations.json, tokens.json, jobstate.json and runs.json live.</summary>
    public string DataDirectory { get; set; } = @"C:\ProgramData\QueryAnyRest";

    public int SchedulerPollSeconds { get; set; } = 20;

    public int RunHistoryLimit { get; set; } = 200;

    public string ConfigurationFile => Path.Combine(DataDirectory, "configurations.json");
    public string TokenFile => Path.Combine(DataDirectory, "tokens.json");
    public string JobStateFile => Path.Combine(DataDirectory, "jobstate.json");
    public string RunHistoryFile => Path.Combine(DataDirectory, "runs.json");
}
