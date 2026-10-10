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
    /// <summary>Seconds a client is told to wait before trying again.</summary>
    public const int RetryAfterSeconds = 60;

    /// <summary>The title of the problem.</summary>
    public const string Title = "A metadata provider is unavailable";

    /// <summary>The header name carrying the providers that did not answer a partly answered search.</summary>
    public const string PartialHeader = "X-Wondarr-Partial";

    /// <inheritdoc />
    public override void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var providers = context.Exception switch
        {
            MetadataProviderException exception => (IReadOnlyList<string>)[exception.Provider],
            ProvidersUnavailableException exception => exception.Providers,
            _ => null,
        };

        if (providers is null)
        {
            return;
        }

        context.Result = Unavailable(context.HttpContext, Title, DetailFor(providers));
        context.ExceptionHandled = true;
    }

    /// <summary>The 503 problem: a title, a detail, and <c>Retry-After</c>.</summary>
    /// <param name="httpContext">The request being answered.</param>
    /// <param name="title">The problem title.</param>
    /// <param name="detail">The sentence the UI shows.</param>
    public static ObjectResult Unavailable(HttpContext httpContext, string title, string detail)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var problem = httpContext.RequestServices
            .GetRequiredService<ProblemDetailsFactory>()
            .CreateProblemDetails(
                httpContext,
                StatusCodes.Status503ServiceUnavailable,
                title,
                detail: detail);

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
        };
    }

    /// <summary>The value for <see cref="PartialHeader"/>.</summary>
    /// <param name="providers">The provider keys that did not answer.</param>
    public static string PartialValue(IEnumerable<string> providers) =>
        string.Join(',', providers);

    private static string DetailFor(IReadOnlyList<string> providers)
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
