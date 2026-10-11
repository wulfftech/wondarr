using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Configuration;

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

    /// <summary>
    /// Callers on the local network may skip authentication (the *arr default): a private or loopback
    /// socket address, no proxy header, and a LAN-style <c>Host</c> (see <c>LocalCallerRules</c>).
    /// </summary>
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

    /// <summary>Optional URL base, for example <c>/wondarr</c>. Empty means the site root.</summary>
    public string UrlBase { get; set; } = string.Empty;

    /// <summary>Authentication method.</summary>
    public AuthenticationMethod Auth { get; set; } = AuthenticationMethod.Forms;

    /// <summary>Whether authentication is required for local addresses.</summary>
    public AuthenticationRequired AuthRequired { get; set; } = AuthenticationRequired.DisabledForLocalAddresses;

    /// <summary>API key used by *arr-style clients (<c>X-Api-Key</c>).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Extra host names that count as local for <see cref="AuthenticationRequired.DisabledForLocalAddresses"/>
    /// and first-login setup, beyond IP literals, <c>localhost</c>, single-label names and the
    /// <c>.local</c>, <c>.lan</c>, <c>.home.arpa</c> and <c>.internal</c> suffixes. An entry is an exact
    /// name or <c>*.suffix</c>. Empty by default.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// Origins, such as <c>https://dash.example.lan</c>, that may frame the web UI in addition to
    /// itself (CSP <c>frame-ancestors</c>). Empty by default.
    /// </summary>
    public List<string> FrameAncestors { get; set; } = [];
}

/// <summary>
/// Validates <see cref="ServerOptions"/>. Every failure message starts with the YAML key so
/// the user can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class ServerOptionsValidator : IValidateOptions<ServerOptions>
{
    private static readonly Regex UrlBasePattern = new("^(/[A-Za-z0-9._~-]+)+$", RegexOptions.Compiled);
    private static readonly Regex ApiKeyPattern = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex DnsNamePattern = new(
        @"^(?=.{1,253}$)[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$",
        RegexOptions.Compiled);

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
            failures.Add($"server.url_base: must be empty or a path such as /wondarr (was '{urlBase}')");
        }

        var apiKey = options.ApiKey ?? string.Empty;
        if (apiKey.Length > 0 && !ApiKeyPattern.IsMatch(apiKey))
        {
            // Never echo the value: it is a secret and this message ends up in logs.
            failures.Add("server.api_key: must be 32 lowercase hexadecimal characters");
        }

        foreach (var host in options.AllowedHosts ?? [])
        {
            var dnsName = host is not null && host.StartsWith("*.", StringComparison.Ordinal) ? host[2..] : host;

            if (string.IsNullOrEmpty(dnsName) || !DnsNamePattern.IsMatch(dnsName))
            {
                failures.Add($"server.allowed_hosts: '{host}' must be a DNS name such as nas.example.com, or *. followed by one");
            }
            else if (dnsName != host && !dnsName.Contains('.', StringComparison.Ordinal))
            {
                failures.Add($"server.allowed_hosts: '{host}' is too broad: a wildcard needs at least two labels after '*.', such as *.example.com, or it would match a whole top-level domain");
            }
        }

        foreach (var origin in options.FrameAncestors ?? [])
        {
            if (!IsOrigin(origin))
            {
                failures.Add($"server.frame_ancestors: '{origin}' must be an http or https origin with no path, such as https://dash.example.lan");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsOrigin(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.EndsWith('/') ||
            value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is ';' or ',' or '\'' or '"') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            uri.AbsolutePath == "/" &&
            uri.Query.Length == 0 &&
            uri.Fragment.Length == 0 &&
            uri.UserInfo.Length == 0;
    }
}

/// <summary>
/// Normalises <see cref="ServerOptions.UrlBase"/> to the form <c>/wondarr</c>: trimmed, with a
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
