namespace FakeSlskd;

/// <summary>
/// The AcoustID stand-in: the app's own verification fingerprints the audio the fake generated, then
/// asks <c>api.acoustid.org/v2/lookup</c> what it is. CI cannot reach that host, so the fake answers
/// for the fingerprints it generated itself, from the scenario's <c>identity</c> blocks.
/// </summary>
public static class AcoustIdStubApp
{
    /// <summary>The lookup path, which is what <c>APP__ACOUSTID__BASE_URL=http://127.0.0.1:5031/v2/</c> resolves to.</summary>
    public const string LookupPath = "/v2/lookup";

    /// <summary>Error code AcoustID uses for an unusable API key.</summary>
    public const int InvalidApiKeyCode = 4;

    /// <summary>Error code AcoustID uses for a malformed lookup.</summary>
    public const int InvalidFingerprintCode = 3;

    /// <summary>Builds the stub. The caller starts it.</summary>
    /// <param name="options">Configuration and scenario.</param>
    /// <param name="state">The state shared with the fake slskd.</param>
    public static WebApplication Build(FakeSlskdOptions options, FakeSlskdState state)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);

        var builder = FakeSlskdHost.CreateBuilder($"http://127.0.0.1:{options.AcoustIdPort}");

        // AcoustID clients post the form gzip-compressed.
        builder.Services.AddRequestDecompression();
        builder.Services.AddSingleton(state);

        var app = builder.Build();

        app.Use(FakeSlskdHost.LogRequestsAsync);
        app.UseRequestDecompression();

        app.MapPost(LookupPath, async (HttpContext context) =>
        {
            if (!context.Request.HasFormContentType)
            {
                return Error(InvalidFingerprintCode, "invalid fingerprint");
            }

            var form = await context.Request.ReadFormAsync().ConfigureAwait(false);

            return Lookup(state, form["client"].ToString(), form["fingerprint"].ToString());
        });

        app.MapGet(LookupPath, (string? client, string? fingerprint) => Lookup(state, client, fingerprint));

        return app;
    }

    private static IResult Lookup(FakeSlskdState state, string? client, string? fingerprint)
    {
        if (string.IsNullOrEmpty(client))
        {
            return Error(InvalidApiKeyCode, "invalid API key");
        }

        if (string.IsNullOrEmpty(fingerprint))
        {
            return Error(InvalidFingerprintCode, "invalid fingerprint");
        }

        var identity = state.FindIdentity(fingerprint);

        if (identity is null)
        {
            return Results.Json(new AcoustIdLookupResource("ok", []));
        }

        var result = new AcoustIdResultResource(
            Deterministic.Uuid(fingerprint),
            0.96,
            [
                new AcoustIdRecordingResource(
                    identity.RecordingId,
                    identity.Title,
                    identity.DurationSeconds,
                    identity.Artists.Select(artist => new AcoustIdArtistResource(artist.Id, artist.Name)).ToArray()),
            ]);

        return Results.Json(new AcoustIdLookupResource("ok", [result]));
    }

    private static IResult Error(int code, string message) =>
        Results.Json(
            new AcoustIdErrorResource(new AcoustIdErrorDetailResource(code, message)),
            statusCode: StatusCodes.Status400BadRequest);
}

/// <summary>A successful lookup.</summary>
/// <param name="Status">Always <c>ok</c>.</param>
/// <param name="Results">The matches, best first; empty when the fingerprint is unknown.</param>
public sealed record AcoustIdLookupResource(string Status, IReadOnlyList<AcoustIdResultResource> Results);

/// <summary>One AcoustID match.</summary>
/// <param name="Id">The AcoustID's own id, derived from the fingerprint so it is stable.</param>
/// <param name="Score">How confident the match is, out of 1.</param>
/// <param name="Recordings">The recordings linked to that AcoustID.</param>
public sealed record AcoustIdResultResource(Guid Id, double Score, IReadOnlyList<AcoustIdRecordingResource> Recordings);

/// <summary>One recording behind an AcoustID.</summary>
/// <param name="Id">The MusicBrainz recording id.</param>
/// <param name="Title">The recording's title.</param>
/// <param name="Duration">The recording's length in seconds.</param>
/// <param name="Artists">The recording's artists.</param>
public sealed record AcoustIdRecordingResource(
    string Id,
    string Title,
    double Duration,
    IReadOnlyList<AcoustIdArtistResource> Artists);

/// <summary>One artist of a recording.</summary>
/// <param name="Id">The MusicBrainz artist id.</param>
/// <param name="Name">The artist's name.</param>
public sealed record AcoustIdArtistResource(string Id, string Name);

/// <summary>A failed lookup, in AcoustID's error shape.</summary>
/// <param name="Error">What went wrong.</param>
public sealed record AcoustIdErrorResource(AcoustIdErrorDetailResource Error)
{
    /// <summary>Always <c>error</c>.</summary>
    public string Status => "error";
}

/// <summary>The body of an AcoustID error.</summary>
/// <param name="Code">AcoustID's error code.</param>
/// <param name="Message">What the code means.</param>
public sealed record AcoustIdErrorDetailResource(int Code, string Message);
