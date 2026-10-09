using System.Text.Encodings.Web;

namespace Wondarr.Api.Authentication;

/// <summary>
/// The server-rendered login form behind <c>GET /login</c>. Deliberately self-contained: inline
/// CSS, no scripts, no build step, so it works before the SPA is served (P0-08).
/// </summary>
internal static class LoginPage
{
    private const string FormActionToken = "__FORM_ACTION__";
    private const string IconToken = "__ICON__";
    private const string ErrorToken = "__ERROR__";
    private const string HeadingToken = "__HEADING__";
    private const string BodyToken = "__BODY__";
    private const string UsernameToken = "__USERNAME__";

    private const string Template = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Wondarr</title>
        <link rel="icon" href="__ICON__" sizes="48x48">
        <style>
        :root { color-scheme: dark; }
        body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
               background: #1b1b1f; color: #f2f2f4; font-family: system-ui, -apple-system, sans-serif; }
        main { width: min(22rem, 90vw); padding: 2rem; border: 1px solid #3a3a42; border-radius: 0.5rem; background: #232329; }
        h1 { margin: 0 0 1rem; font-size: 1.25rem; }
        label { display: block; margin: 0.75rem 0 0.25rem; font-size: 0.875rem; }
        input[type=text], input[type=password] { box-sizing: border-box; width: 100%; padding: 0.5rem;
               border: 1px solid #4a4a55; border-radius: 0.25rem; background: #1b1b1f; color: inherit; }
        .remember { display: flex; align-items: center; gap: 0.5rem; }
        button { width: 100%; margin-top: 1.25rem; padding: 0.6rem; border: 0; border-radius: 0.25rem;
                 background: #6d5efc; color: #fff; font-size: 1rem; cursor: pointer; }
        .error { margin: 0 0 1rem; color: #ff8a8a; }
        .hint { margin: 1rem 0 0; color: #b9b9c3; font-size: 0.875rem; }
        </style>
        </head>
        <body>
        <main>
        <h1>__HEADING__</h1>
        __ERROR__
        __BODY__
        </main>
        </body>
        </html>
        """;

    private const string SignInForm = """
        <form method="post" action="__FORM_ACTION__">
        <label for="username">Username</label>
        <input id="username" name="username" type="text" autocomplete="username" autofocus>
        <label for="password">Password</label>
        <input id="password" name="password" type="password" autocomplete="current-password">
        <label class="remember"><input type="checkbox" name="rememberMe"> Remember me</label>
        <button type="submit">Log in</button>
        </form>
        """;

    private const string CreateForm = """
        <form method="post" action="__FORM_ACTION__">
        <label for="username">Username</label>
        <input id="username" name="username" type="text" autocomplete="username" value="__USERNAME__" autofocus>
        <label for="password">Password</label>
        <input id="password" name="password" type="password" autocomplete="new-password">
        <label for="passwordAgain">Password again</label>
        <input id="passwordAgain" name="passwordAgain" type="password" autocomplete="new-password">
        <label class="remember"><input type="checkbox" name="rememberMe"> Remember me</label>
        <button type="submit">Create login</button>
        </form>
        <p class="hint">No login exists yet. Choose the username and password you will sign in with.</p>
        """;

    private const string RemoteHint =
        """<p class="hint">No login exists yet. Create it from a device on the same network as Wondarr, or under Settings → General.</p>""";

    /// <summary>What <c>GET /login</c> shows, depending on whether a login exists and who is asking.</summary>
    public enum Variant
    {
        /// <summary>The normal sign-in form.</summary>
        SignIn,

        /// <summary>No login exists and the caller is local: the create form.</summary>
        Create,

        /// <summary>No login exists and the caller is not local: an explanation and no form.</summary>
        CreateFromLocalNetwork,
    }

    /// <summary>Renders the page for the given URL base and state.</summary>
    /// <param name="urlBase">The URL base the app is served under.</param>
    /// <param name="loginFailed">Whether to show the "incorrect username or password" error.</param>
    /// <param name="variant">Which of the three pages to render.</param>
    /// <param name="returnUrl">An already validated local URL to land on after signing in.</param>
    /// <param name="error">An error to show in place of the login-failed one.</param>
    /// <param name="username">The username to keep filled in on the create form. A password is never echoed.</param>
    /// <param name="notice">A plain notice to show above the sign-in form.</param>
    public static string Render(
        string urlBase,
        bool loginFailed,
        Variant variant,
        string? returnUrl = null,
        string? error = null,
        string? username = null,
        string? notice = null)
    {
        // Carry returnUrl through the form so a retry still lands on the page first asked for;
        // POST /login and POST /login/setup re-validate it as a local URL before redirecting.
        var query = string.IsNullOrEmpty(returnUrl) ? string.Empty : $"?returnUrl={Uri.EscapeDataString(returnUrl)}";
        var encoder = HtmlEncoder.Default;

        // Named explicitly: a browser's own guess, /favicon.ico at the host root, misses under a URL base.
        var icon = encoder.Encode($"{urlBase}/favicon.ico");

        var heading = "Wondarr";
        string body;

        switch (variant)
        {
            case Variant.Create:
                heading = "Create your login";
                body = CreateForm
                    .Replace(UsernameToken, encoder.Encode(username ?? string.Empty), StringComparison.Ordinal)
                    .Replace(FormActionToken, encoder.Encode($"{urlBase}/login/setup{query}"), StringComparison.Ordinal);
                break;
            case Variant.CreateFromLocalNetwork:
                heading = "Create your login";
                body = RemoteHint;
                break;
            default:
                body = SignInForm.Replace(FormActionToken, encoder.Encode($"{urlBase}/login{query}"), StringComparison.Ordinal);

                if (!string.IsNullOrEmpty(notice))
                {
                    body = $"""<p class="hint">{encoder.Encode(notice)}</p>""" + body;
                }

                break;
        }

        string errorHtml;

        if (!string.IsNullOrEmpty(error))
        {
            errorHtml = $"""<p class="error">{encoder.Encode(error)}</p>""";
        }
        else if (loginFailed)
        {
            errorHtml = """<p class="error">Incorrect username or password.</p>""";
        }
        else
        {
            errorHtml = string.Empty;
        }

        // The body goes in last so nothing a user typed is ever scanned for a token.
        return Template
            .Replace(IconToken, icon, StringComparison.Ordinal)
            .Replace(HeadingToken, heading, StringComparison.Ordinal)
            .Replace(ErrorToken, errorHtml, StringComparison.Ordinal)
            .Replace(BodyToken, body, StringComparison.Ordinal);
    }
}
