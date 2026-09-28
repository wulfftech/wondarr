using System.Text.Encodings.Web;

namespace Compilarr.Api.Authentication;

/// <summary>
/// The server-rendered login form behind <c>GET /login</c>. Deliberately self-contained: inline
/// CSS, no scripts, no build step, so it works before the SPA is served (P0-08).
/// </summary>
internal static class LoginPage
{
    private const string FormActionToken = "__FORM_ACTION__";
    private const string ErrorToken = "__ERROR__";
    private const string HintToken = "__HINT__";

    private const string Template = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Compilarr</title>
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
        <h1>Compilarr</h1>
        __ERROR__
        <form method="post" action="__FORM_ACTION__">
        <label for="username">Username</label>
        <input id="username" name="username" type="text" autocomplete="username" autofocus>
        <label for="password">Password</label>
        <input id="password" name="password" type="password" autocomplete="current-password">
        <label class="remember"><input type="checkbox" name="rememberMe"> Remember me</label>
        <button type="submit">Log in</button>
        </form>
        __HINT__
        </main>
        </body>
        </html>
        """;

    /// <summary>Renders the page for the given URL base and state.</summary>
    public static string Render(string urlBase, bool loginFailed, bool configured)
    {
        var action = HtmlEncoder.Default.Encode($"{urlBase}/login");

        var error = loginFailed
            ? """<p class="error">Incorrect username or password.</p>"""
            : string.Empty;

        var hint = configured
            ? string.Empty
            : """<p class="hint">No credentials are configured yet: set a username and password through the API, or in the app's settings.</p>""";

        return Template
            .Replace(FormActionToken, action, StringComparison.Ordinal)
            .Replace(ErrorToken, error, StringComparison.Ordinal)
            .Replace(HintToken, hint, StringComparison.Ordinal);
    }
}
