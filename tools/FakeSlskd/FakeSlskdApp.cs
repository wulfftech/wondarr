using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;

namespace FakeSlskd;

/// <summary>
/// The fake slskd itself: the slskd 0.26 API subset Wondarr uses, answered from a scenario file, on
/// the port and with the API keys of the rendered <c>slskd.yml</c>. A test tool — never part of the
/// image.
/// </summary>
public static class FakeSlskdApp
{
    /// <summary>The header slskd authenticates API calls with.</summary>
    public const string ApiKeyHeader = "X-API-Key";

    /// <summary>Builds the app. The caller starts it.</summary>
    /// <param name="options">Configuration and scenario.</param>
    /// <param name="state">The state shared with the AcoustID stub.</param>
    public static WebApplication Build(FakeSlskdOptions options, FakeSlskdState state)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);

        var builder = FakeSlskdHost.CreateBuilder(
            $"http://{options.Configuration.WebIpAddress}:{options.Configuration.WebPort}");

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(state);

        var app = builder.Build();

        app.Use(FakeSlskdHost.LogRequestsAsync);

        // Every /api/v0 request needs one of the configured keys, like slskd's own API-key handler.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v0"))
            {
                var provided = context.Request.Headers[ApiKeyHeader].ToString();

                if (!state.IsAuthorized(provided.Length == 0 ? null : provided))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                    return;
                }
            }

            await next(context).ConfigureAwait(false);
        });

        MapState(app, state);
        MapSearches(app, state);
        MapTransfers(app, state);

        app.MapGet("/fake/log", (HttpContext context) =>
            IsLoopback(context.Connection.RemoteIpAddress)
                ? Results.Json(state.GateLog())
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        // The upgrade gate makes a better file appear mid-run; loopback only, like the log.
        app.MapPost("/fake/scenario/files", (HttpContext context, ScenarioFile[] files) =>
            IsLoopback(context.Connection.RemoteIpAddress)
                ? Results.Json(new { files = state.AddScenarioFiles(files) })
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        return app;
    }

    private static void MapState(WebApplication app, FakeSlskdState state)
    {
        app.MapGet("/api/v0/application", () => Results.Json(state.ApplicationJson()));
        app.MapGet("/api/v0/server", () => Results.Json(state.ServerJson()));
        app.MapGet("/api/v0/shares", () => Results.Json(state.SharesJson()));

        // slskd answers a rescan with 204; the fake scans synchronously, so it is never "already scanning" (409).
        app.MapPut("/api/v0/shares", () =>
        {
            state.RescanShares();

            return Results.NoContent();
        });
    }

    private static void MapSearches(WebApplication app, FakeSlskdState state)
    {
        app.MapGet("/api/v0/searches", () => Results.Json(state.ListSearches()));

        app.MapPost("/api/v0/searches", (SearchRequestBody body) =>
        {
            if (string.IsNullOrWhiteSpace(body.SearchText))
            {
                return Results.BadRequest("searchText is required");
            }

            var search = state.StartSearch(body.Id ?? Guid.NewGuid(), body.SearchText);

            return search is null
                ? Results.Text(
                    "Only one concurrent operation is permitted. Wait until the previous request completes",
                    statusCode: StatusCodes.Status429TooManyRequests)
                : Results.Json(search);
        });

        app.MapGet("/api/v0/searches/{id:guid}", (Guid id) =>
            state.GetSearch(id) is { } search ? Results.Json(search) : Results.NotFound());

        app.MapGet("/api/v0/searches/{id:guid}/responses", (Guid id) =>
            state.GetResponses(id) is { } responses ? Results.Json(responses) : Results.NotFound());

        // Stopping a search ends it now, as slskd's own stop does.
        app.MapPut("/api/v0/searches/{id:guid}", (Guid id) =>
            state.CompleteSearch(id, "Cancelled") is { } search ? Results.Json(search) : Results.NotFound());

        app.MapDelete("/api/v0/searches/{id:guid}", (Guid id) =>
            state.DeleteSearch(id) ? Results.NoContent() : Results.NotFound());
    }

    private static void MapTransfers(WebApplication app, FakeSlskdState state)
    {
        app.MapPost("/api/v0/transfers/downloads/batches", (EnqueueBatchRequestBody body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Username))
            {
                return Results.BadRequest("username is required");
            }

            if (body.Files is null || body.Files.Count == 0)
            {
                return Results.BadRequest("at least one file is required");
            }

            var destination = body.Options?.Destination;

            if (!IsSafeDestination(destination))
            {
                return Results.BadRequest("destination must be relative and must not traverse upwards");
            }

            var response = state.EnqueueBatch(body, destination?.Trim('/', '\\') ?? string.Empty, body.SearchId);

            var status = response.Failures.Count switch
            {
                0 => StatusCodes.Status201Created,
                var failed when failed < body.Files.Count => StatusCodes.Status207MultiStatus,
                _ => StatusCodes.Status200OK,
            };

            return Results.Json(response, statusCode: status);
        });

        app.MapGet("/api/v0/transfers/downloads", (bool? includeRemoved) =>
            Results.Json(state.ListDownloads(includeRemoved ?? false)));

        app.MapGet("/api/v0/transfers/downloads/{username}/{id:guid}", (string username, Guid id) =>
            state.GetTransfer(username, id) is { } transfer ? Results.Json(transfer) : Results.NotFound());

        app.MapGet("/api/v0/transfers/downloads/{username}/{id:guid}/position", (string username, Guid id) =>
            state.GetPosition(username, id) is { } position ? Results.Json(position) : Results.NotFound());

        app.MapDelete("/api/v0/transfers/downloads/{username}/{id:guid}", (string username, Guid id, bool? remove) =>
            state.CancelTransfer(username, id, remove ?? false) ? Results.NoContent() : Results.NotFound());
    }

    private static bool IsSafeDestination(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return true;
        }

        return !Path.IsPathRooted(destination)
            && !destination.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment == "..");
    }

    private static bool IsLoopback(IPAddress? address) => address is null || IPAddress.IsLoopback(address);
}

/// <summary>The pieces every fake app needs: URLs, no default logging, and slskd's JSON conventions.</summary>
internal static class FakeSlskdHost
{
    /// <summary>Writes one slskd-style line per request, which is what the app's log watcher sees.</summary>
    /// <param name="context">The request being served.</param>
    /// <param name="next">The rest of the pipeline.</param>
    public static async Task LogRequestsAsync(HttpContext context, RequestDelegate next)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            FakeSlskdLog.Error($"{context.Request.Method} {context.Request.Path} failed: {exception.Message}");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            return;
        }

        var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var elapsedText = elapsed.ToString(CultureInfo.InvariantCulture);

        FakeSlskdLog.Info(
            $"{context.Request.Method} {context.Request.Path} responded {context.Response.StatusCode} in {elapsedText}ms");
    }

    /// <summary>Builds a minimal web host for <paramref name="url"/>.</summary>
    /// <param name="url">The URL to listen on.</param>
    public static WebApplicationBuilder CreateBuilder(string url)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "FakeSlskd",
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseUrls(url);

        // Everything this process prints is one of our own slskd-style lines; the default console
        // logger would interleave multi-line records with them.
        builder.Logging.ClearProviders();

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            // slskd omits attributes the peer did not send instead of writing null.
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        return builder;
    }
}
