using Wondarr.Api.Extensions;
using Wondarr.Api.YouTube;
using Wondarr.Api.Frontend;
using Wondarr.Api.Middleware;
using Wondarr.Api.SignalR;
using Wondarr.Core;
using Wondarr.Core.Configuration;
using Wondarr.Core.Logging;
using Wondarr.Core.Metadata;
using Wondarr.Sources.Slskd;
using Wondarr.Sources.Torznab;
using Wondarr.Sources.YouTube;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var environment = Environment.GetEnvironmentVariables();
var paths = WondarrPaths.Resolve(builder.Configuration, environment);

// Installs from before the rename (0.0.1-alpha.1) keep their data: compilarr.db becomes wondarr.db.
var adoptedLegacyDatabase = paths.AdoptLegacyDatabase();

// First run writes config.yml (with a generated API key) before anything reads the settings.
ConfigFileInitializer.EnsureInitialized(paths, builder.Configuration);
builder.Configuration.AddWondarrConfiguration(paths, environment);

builder.Services.AddWondarrConfiguration(builder.Configuration, paths);
builder.Services.AddWondarrPersistence($"Data Source={paths.DatabaseFile}");
builder.Services.AddWondarrCore();
builder.Services.AddWondarrMetadata(builder.Configuration);
builder.Services.AddWondarrApi(paths);
builder.Services.AddWondarrFrontend();
builder.Services.AddWondarrSlskd(builder.Configuration);
builder.Services.AddWondarrYouTube();
builder.Services.AddWondarrYouTubeSettings(builder.Configuration);
builder.Services.AddWondarrTorznab();

// Every sink is behind the secret registry, so nothing logged can leak the API key.
builder.Host.UseSerilog((_, services, loggerConfiguration) => LoggingSetup.Configure(
    loggerConfiguration,
    services.GetRequiredService<IOptions<LogOptions>>().Value,
    paths,
    services.GetRequiredService<ISecretRegistry>()),
    // Keep the process-wide Log.Logger untouched: several hosts (tests) can share one process.
    preserveStaticLogger: true);

var configured = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
var bindAddress = string.IsNullOrWhiteSpace(configured.BindAddress) ? "*" : configured.BindAddress;
builder.WebHost.UseUrls($"http://{bindAddress}:{configured.Port}");

var app = builder.Build();

if (adoptedLegacyDatabase)
{
    ProgramLog.AdoptedLegacyDatabase(app.Logger, paths.LegacyDatabaseFile, paths.DatabaseFile);
}

LoggingSetup.RegisterServerApiKey(
    app.Services.GetRequiredService<ISecretRegistry>(),
    app.Services.GetRequiredService<IOptionsMonitor<ServerOptions>>());

// The *arr pipeline order (Lidarr's Startup.Configure): forwarded headers, request logging, URL
// base, static files, routing, authentication, authorization, then the URL-base redirect and the
// endpoints. The SPA fallback is registered last so it only sees paths no endpoint matched.
var urlBase = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.UrlBase;

app.UseForwardedHeaders();
app.UseSerilogRequestLogging(options =>
{
    options.Logger = app.Services.GetRequiredService<Serilog.ILogger>();

    // Path only: the query string can carry ?apikey=.
    options.IncludeQueryInRequestPath = false;
});
app.UsePathBase(new PathString(urlBase));
app.UseWondarrFrontend();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<UrlBaseMiddleware>(urlBase);
app.MapControllers();

// Browsers cannot set headers on a WebSocket handshake, so the SignalR scheme also accepts the key
// as ?access_token= — which is why the hub gets its own policy rather than the API fallback.
app.MapHub<EventsHub>("/signalr/events").RequireAuthorization("SignalR");

// Behind the default API-key policy: the document describes the API, so it is served like the API.
app.MapOpenApi("/docs/{documentName}/openapi.json");

app.MapWondarrSpa();

app.Run();

/// <summary>
/// Entry point marker so integration tests can host the API with <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program
{
}

/// <summary>Startup log messages written before any service owns a logger category.</summary>
internal static partial class ProgramLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Renamed {LegacyDatabase} to {Database} (the project is now called Wondarr)")]
    public static partial void AdoptedLegacyDatabase(Microsoft.Extensions.Logging.ILogger logger, string legacyDatabase, string database);
}
