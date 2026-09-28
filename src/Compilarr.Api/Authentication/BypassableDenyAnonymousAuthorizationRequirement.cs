// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Authentication/BypassableDenyAnonymousAuthorizationRequirement.cs, GPL-3.0.
// Adapted for Compilarr: moved to the Compilarr.Api.Authentication namespace, otherwise unchanged.

using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace Compilarr.Api.Authentication;

/// <summary>
/// Denies anonymous callers, but lets <see cref="UiAuthorizationHandler"/> succeed the requirement
/// for local addresses when authentication is disabled for them.
/// </summary>
public sealed class BypassableDenyAnonymousAuthorizationRequirement : DenyAnonymousAuthorizationRequirement;
