using Wondarr.Core.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Wondarr.Api.Middleware;

/// <summary>
/// Turns "a metadata provider did not answer" into a 503 problem with a plain detail, instead of the
/// unhandled-exception 500 the user would otherwise see for what is somebody else's outage. Apply it
/// to a controller that reaches MusicBrainz or Deezer.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class MetadataUnavailableFilterAttribute : ExceptionFilterAttribute
{
    /// <summary>Seconds a client is told to wait when the provider did not say.</summary>
    public const int DefaultRetryAfterSeconds = 60;

    /// <summary>The longest <c>Retry-After</c> sent, however long the provider asked for.</summary>
    public const int MaxRetryAfterSeconds = 300;

    /// <summary>The title of the problem.</summary>
    public const string Title = "A metadata provider is unavailable";

    /// <summary>The header name carrying the providers that did not answer a partly answered search.</summary>
    public const string PartialHeader = "X-Wondarr-Partial";

    /// <inheritdoc />
    public override void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var http = context.HttpContext;

        switch (context.Exception)
        {
            // The provider refused the request (a banned User-Agent, a bad key): waiting will not help,
            // so this is a bad gateway, not a busy one.
            case MetadataProviderException { IsUnavailable: false } rejected:
                context.Result = Problem(
                    http,
                    StatusCodes.Status502BadGateway,
                    "A metadata provider rejected the request",
                    $"{NameOf(rejected.Provider)} rejected the request ({(int)rejected.StatusCode}).");
                context.ExceptionHandled = true;
                break;

            case MetadataProviderException busy:
                context.Result = Unavailable(http, Title, DetailFor([busy.Provider]), busy.RetryAfter);
                context.ExceptionHandled = true;
                break;

            case ProvidersUnavailableException none:
                context.Result = Unavailable(http, Title, DetailFor(none.Providers), none.RetryAfter);
                context.ExceptionHandled = true;
                break;

            // No connection, or the provider's own timeout; a caller who gave up is not a provider outage.
            case HttpRequestException:
            case TaskCanceledException when !http.RequestAborted.IsCancellationRequested:
                context.Result = Unavailable(http, Title, DetailFor([]), null);
                context.ExceptionHandled = true;
                break;
        }
    }

    /// <summary>The 503 problem: a title, a detail, and <c>Retry-After</c>.</summary>
    /// <param name="httpContext">The request being answered.</param>
    /// <param name="title">The problem title.</param>
    /// <param name="detail">The sentence the UI shows.</param>
    /// <param name="retryAfter">How long the provider asked to be left alone; a minute when unknown.</param>
    public static ObjectResult Unavailable(HttpContext httpContext, string title, string detail, TimeSpan? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var seconds = retryAfter is { } wait
            ? (int)Math.Clamp(Math.Ceiling(wait.TotalSeconds), 1, MaxRetryAfterSeconds)
            : DefaultRetryAfterSeconds;

        httpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return Problem(httpContext, StatusCodes.Status503ServiceUnavailable, title, detail);
    }

    private static ObjectResult Problem(HttpContext httpContext, int status, string title, string detail)
    {
        var problem = httpContext.RequestServices
            .GetRequiredService<ProblemDetailsFactory>()
            .CreateProblemDetails(httpContext, status, title, detail: detail);

        return new ObjectResult(problem) { StatusCode = status };
    }

    /// <summary>The value for <see cref="PartialHeader"/>.</summary>
    /// <param name="providers">The provider keys that did not answer.</param>
    public static string PartialValue(IEnumerable<string> providers) =>
        string.Join(',', providers);

    /// <summary>The sentence for a 503: which providers did not answer.</summary>
    /// <param name="providers">The provider keys that did not answer.</param>
    public static string DetailFor(IReadOnlyList<string> providers)
    {
        var names = providers.Select(NameOf).Distinct(StringComparer.Ordinal).ToList();

        return names.Count switch
        {
            0 => "A metadata provider did not answer; try again in a minute.",
            1 => $"{names[0]} did not answer; try again in a minute.",
            _ => $"{string.Join(" and ", names)} did not answer; try again in a minute.",
        };
    }

    private static string NameOf(string provider) => provider switch
    {
        ProviderKeys.MusicBrainz => "MusicBrainz",
        ProviderKeys.Deezer => "Deezer",
        "coverartarchive" => "The Cover Art Archive",
        "acoustid" => "AcoustID",
        "itunes" => "iTunes",
        "lastfm" => "Last.fm",
        _ => provider,
    };
}
