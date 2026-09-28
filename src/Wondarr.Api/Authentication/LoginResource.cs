// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Authentication/LoginResource.cs, GPL-3.0.
// Adapted for Wondarr: a record, with nullable members because the form may omit fields.

namespace Wondarr.Api.Authentication;

/// <summary>The <c>POST /login</c> form body.</summary>
/// <param name="Username">The submitted username.</param>
/// <param name="Password">The submitted password.</param>
/// <param name="RememberMe"><c>on</c> when the "remember me" box was ticked.</param>
public sealed record LoginResource(string? Username, string? Password, string? RememberMe);
