// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Api.V1/Health/HealthResource.cs, GPL-3.0.
// Adapted for Compilarr: a record without the REST id, and WikiUrl is a string that is null for now.

using Compilarr.Core.HealthCheck;

namespace Compilarr.Api.Health;

/// <summary>One health check result, as the UI and dashboards see it.</summary>
/// <param name="Source">The name of the check that produced the result.</param>
/// <param name="Type">The result, serialised camelCase (<c>ok</c>, <c>notice</c>, <c>warning</c>, <c>error</c>).</param>
/// <param name="Message">A sentence for the user.</param>
/// <param name="WikiUrl">The wiki page for this result, or <see langword="null"/>.</param>
public sealed record HealthResource(
    string Source,
    HealthCheckResult Type,
    string Message,
    string? WikiUrl);