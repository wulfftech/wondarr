using Compilarr.Core;
using Compilarr.Core.Configuration;
using Compilarr.Sources.Slskd;
using Compilarr.Sources.Torznab;
using Compilarr.Sources.YouTube;

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

var serverOptions = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
var bindAddress = string.IsNullOrWhiteSpace(serverOptions.BindAddress) ? "*" : serverOptions.BindAddress;
builder.WebHost.UseUrls($"http://{bindAddress}:{serverOptions.Port}");

var app = builder.Build();

// Health probe used by the container images and the *arr-style clients.
app.MapGet("/ping", () => Results.Ok());

app.Run();

/// <summary>
/// Entry point marker so integration tests can host the API with <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program
{
}
