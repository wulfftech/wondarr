using Compilarr.Api.Extensions;
using Compilarr.Api.Middleware;
using Compilarr.Core;
using Compilarr.Core.Configuration;
using Compilarr.Sources.Slskd;
using Compilarr.Sources.Torznab;
using Compilarr.Sources.YouTube;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var environment = Environment.GetEnvironmentVariables();
var paths = CompilarrPaths.Resolve(builder.Configuration, environment);

// First run writes config.yml (with a generated API key) before anything reads the settings.
ConfigFileInitializer.EnsureInitialized(paths, builder.Configuration);
builder.Configuration.AddCompilarrConfiguration(paths, environment);

builder.Services.AddCompilarrConfiguration(builder.Configuration, paths);
builder.Services.AddCompilarrPersistence($"Data Source={paths.DatabaseFile}");
builder.Services.AddCompilarrCore();
builder.Services.AddCompilarrApi(paths);
builder.Services.AddCompilarrSlskd();
builder.Services.AddCompilarrYouTube();
builder.Services.AddCompilarrTorznab();

var configured = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
var bindAddress = string.IsNullOrWhiteSpace(configured.BindAddress) ? "*" : configured.BindAddress;
builder.WebHost.UseUrls($"http://{bindAddress}:{configured.Port}");

var app = builder.Build();

// The *arr pipeline order (Lidarr's Startup.Configure): forwarded headers, URL base, routing,
// authentication, authorization, then the URL-base redirect and the endpoints.
var urlBase = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.UrlBase;

app.UseForwardedHeaders();
app.UsePathBase(new PathString(urlBase));
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<UrlBaseMiddleware>(urlBase);
app.MapControllers();

app.Run();

/// <summary>
/// Entry point marker so integration tests can host the API with <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program
{
}
