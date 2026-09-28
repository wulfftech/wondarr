using Compilarr.Core;
using Compilarr.Core.Configuration;
using Compilarr.Core.Logging;
using Compilarr.Sources.Slskd;
using Compilarr.Sources.Torznab;
using Compilarr.Sources.YouTube;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var environment = Environment.GetEnvironmentVariables();
var paths = CompilarrPaths.Resolve(builder.Configuration, environment);

// First run writes config.yml (with a generated API key) before anything reads the settings.
ConfigFileInitializer.EnsureInitialized(paths, builder.Configuration);
builder.Configuration.AddCompilarrConfiguration(paths, environment);

builder.Services.AddCompilarrConfiguration(builder.Configuration, paths);
builder.Services.AddCompilarrPersistence($"Data Source={paths.DatabaseFile}");
builder.Services.AddCompilarrCore();
builder.Services.AddCompilarrSlskd();
builder.Services.AddCompilarrYouTube();
builder.Services.AddCompilarrTorznab();

// Every sink is behind the secret registry, so nothing logged can leak the API key.
builder.Host.UseSerilog((_, services, loggerConfiguration) => LoggingSetup.Configure(
    loggerConfiguration,
    services.GetRequiredService<IOptions<LogOptions>>().Value,
    paths,
    services.GetRequiredService<ISecretRegistry>()),
    // Keep the process-wide Log.Logger untouched: several hosts (tests) can share one process.
    preserveStaticLogger: true);

var serverOptions = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
var bindAddress = string.IsNullOrWhiteSpace(serverOptions.BindAddress) ? "*" : serverOptions.BindAddress;
builder.WebHost.UseUrls($"http://{bindAddress}:{serverOptions.Port}");

var app = builder.Build();

LoggingSetup.RegisterServerApiKey(
    app.Services.GetRequiredService<ISecretRegistry>(),
    app.Services.GetRequiredService<IOptionsMonitor<ServerOptions>>());

// P0-04 (forwarded headers) has not landed yet, so the request logger goes first.
app.UseSerilogRequestLogging(options =>
{
    options.Logger = app.Services.GetRequiredService<Serilog.ILogger>();

    // Path only: the query string can carry ?apikey=.
    options.IncludeQueryInRequestPath = false;
});

// Health probe used by the container images and the *arr-style clients.
app.MapGet("/ping", () => Results.Ok());

app.Run();

/// <summary>
/// Entry point marker so integration tests can host the API with <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program
{
}
