namespace Wondarr.Api.Authentication;

/// <summary>The <c>POST /login/setup</c> form body: the first login, typed twice.</summary>
/// <param name="Username">The username to create, 1 to 64 characters.</param>
/// <param name="Password">The password to create, at least 8 characters.</param>
/// <param name="PasswordAgain">The password again; it must match <paramref name="Password"/>.</param>
/// <param name="RememberMe"><c>on</c> when the "remember me" box was ticked.</param>
public sealed record SetupResource(string? Username, string? Password, string? PasswordAgain, string? RememberMe);
