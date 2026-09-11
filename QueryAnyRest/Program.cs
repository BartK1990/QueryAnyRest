using Microsoft.AspNetCore.DataProtection;
using QueryAnyRest.Models;
using QueryAnyRest.Components;
using QueryAnyRest.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // A Windows service starts in C:\Windows\System32, so the content root has to be pinned.
    ContentRootPath = AppContext.BaseDirectory,
    ApplicationName = typeof(Program).Assembly.GetName().Name
});

// Runs as a service when the SCM starts it, and as a plain console app when you press F5.
builder.Host.UseWindowsService(options => options.ServiceName = "QueryAnyRest");

if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog(new Microsoft.Extensions.Logging.EventLog.EventLogSettings
    {
        SourceName = "QueryAnyRest"
    });
}

// --- Options -------------------------------------------------------------
var appOptions = builder.Configuration
    .GetSection(AppOptions.SectionName)
    .Get<AppOptions>() ?? new AppOptions();

Directory.CreateDirectory(appOptions.DataDirectory);
builder.Services.AddSingleton(appOptions);

// --- Secret protection ---------------------------------------------------
// Credentials are encrypted with a key ring that lives beside the data and is itself
// protected by DPAPI. Copying configurations.json to another machine gets you nothing,
// and the key ring is only usable on the machine that created it.
var keysDirectory = Path.Combine(appOptions.DataDirectory, "keys");
Directory.CreateDirectory(keysDirectory);

var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("QueryAnyRest")
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

if (OperatingSystem.IsWindows())
{
    // protectToLocalMachine: true survives a change of service account. Switch it to
    // false to bind the key ring to the service account's own profile instead, which is
    // tighter but breaks if the account changes or its profile does not load.
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

// --- Storage and work services ------------------------------------------
builder.Services.AddSingleton<ConfigurationStore>();
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<JobStateStore>();
builder.Services.AddSingleton<RunHistory>();

builder.Services.AddSingleton<OAuthTokenService>();
builder.Services.AddSingleton<RestQueryService>();
builder.Services.AddSingleton<RequestBuilderService>();
builder.Services.AddSingleton<QueryChainService>();
builder.Services.AddSingleton<TransformService>();
builder.Services.AddSingleton<ExportService>();
builder.Services.AddSingleton<JobRunner>();

builder.Services.AddHostedService<SchedulerService>();

// --- HTTP clients --------------------------------------------------------
// HttpClient's own 100-second timeout is switched off: each request carries the
// configuration's timeout instead, which may be longer.
builder.Services.AddHttpClient(HttpClientNames.Default)
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        UseProxy = true
    });

builder.Services.AddHttpClient(HttpClientNames.Insecure)
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        UseProxy = true,
        // Only used by configurations that explicitly opt in.
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });

// --- Blazor --------------------------------------------------------------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Must happen before any store reads or writes a file, so that every secret going to
// disk is encrypted and every secret coming back is decrypted.
SecretStringConverter.Use(app.Services.GetRequiredService<ISecretProtector>());

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Logger.LogInformation(
    "Query Any REST is starting. Data directory: {Directory}", appOptions.DataDirectory);

app.Run();
