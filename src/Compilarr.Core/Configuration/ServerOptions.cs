using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Compilarr.Core.Configuration;

/// <summary>How users authenticate against the web UI and API.</summary>
public enum AuthenticationMethod
{
    /// <summary>No authentication; only sensible on a trusted network.</summary>
    None,

    /// <summary>Username and password stored by the app.</summary>
    Forms,

    /// <summary>Delegated to a reverse proxy that sets the authenticated user header.</summary>
    External,
}

/// <summary>Whether authentication is required for every caller.</summary>
public enum AuthenticationRequired
{
    /// <summary>Authentication is required from every address.</summary>
    Enabled,

    /// <summary>Loopback addresses may skip authentication (the *arr default).</summary>
    DisabledForLocalAddresses,
}

/// <summary>
/// The <c>server</c> section of <c>config.yml</c>.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>HTTP port; 1077 by owner decision.</summary>
    public int Port { get; set; } = 1077;

    /// <summary>Address to bind; <c>*</c> means all interfaces.</summary>
    public string BindAddress { get; set; } = "*";

    /// <summary>Optional URL base, for example <c>/compilarr</c>. Empty means the site root.</summary>
    public string UrlBase { get; set; } = string.Empty;

    /// <summary>Authentication method.</summary>
    public AuthenticationMethod Auth { get; set; } = AuthenticationMethod.Forms;

    /// <summary>Whether authentication is required for local addresses.</summary>
    public AuthenticationRequired AuthRequired { get; set; } = AuthenticationRequired.DisabledForLocalAddresses;

    /// <summary>API key used by *arr-style clients (<c>X-Api-Key</c>).</summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// Validates <see cref="ServerOptions"/>. Every failure message starts with the YAML key so
/// the user can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class ServerOptionsValidator : IValidateOptions<ServerOptions>
{
    private static readonly Regex UrlBasePattern = new("^(/[A-Za-z0-9._~-]+)+$", RegexOptions.Compiled);
    private static readonly Regex ApiKeyPattern = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

    public ValidateOptionsResult Validate(string? name, ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.Port is < 1 or > 65535)
        {
            failures.Add($"server.port: must be between 1 and 65535 (was {options.Port})");
        }

        var urlBase = options.UrlBase ?? string.Empty;
        if (urlBase.Length > 0 && !UrlBasePattern.IsMatch(urlBase))
        {
            failures.Add($"server.url_base: must be empty or a path such as /compilarr (was '{urlBase}')");
        }

        var apiKey = options.ApiKey ?? string.Empty;
        if (apiKey.Length > 0 && !ApiKeyPattern.IsMatch(apiKey))
        {
            failures.Add($"server.api_key: must be 32 lowercase hexadecimal characters (was '{apiKey}')");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Normalises <see cref="ServerOptions.UrlBase"/> to the form <c>/compilarr</c>: trimmed, with a
/// leading slash and no trailing slash. A bare <c>/</c> becomes the empty string.
/// </summary>
public sealed class ServerOptionsPostConfigure : IPostConfigureOptions<ServerOptions>
{
    public void PostConfigure(string? name, ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var urlBase = (options.UrlBase ?? string.Empty).Trim();

        if (urlBase.Length > 0)
        {
            if (!urlBase.StartsWith('/'))
            {
                urlBase = "/" + urlBase;
            }

            urlBase = urlBase.TrimEnd('/');
        }

        options.UrlBase = urlBase;
    }
}