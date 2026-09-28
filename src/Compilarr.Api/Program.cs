using Compilarr.Core;
using Compilarr.Sources.Slskd;
using Compilarr.Sources.Torznab;
using Compilarr.Sources.YouTube;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCompilarrCore();
builder.Services.AddCompilarrSlskd();
builder.Services.AddCompilarrYouTube();
builder.Services.AddCompilarrTorznab();

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