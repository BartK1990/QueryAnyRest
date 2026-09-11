namespace QueryAnyRest.Services;

public static class HttpClientNames
{
    public const string Default = "rest";

    /// <summary>Skips certificate validation, for appliances with a self-signed certificate.</summary>
    public const string Insecure = "rest-insecure";
}
