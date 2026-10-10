using System.Reflection;

namespace Wondarr.Core.Updates;

/// <summary>
/// The version of the running build. A release image carries <c>0.1.0</c> or <c>0.1.0-rc.1</c>; a
/// <c>main</c> or <c>sha-*</c> image carries <c>0.0.0-develop.&lt;run&gt;</c> (an image built locally
/// <c>0.0.0-dev</c>), so any <c>0.0.0-*</c> version is a development build; anything else that is not a
/// Semantic Versioning version is a development build too, because it cannot be ordered against
/// releases.
/// </summary>
/// <param name="Text">The version as shown to the user, without the <c>+commit</c> suffix.</param>
/// <param name="Parsed">The parsed version, or <see langword="null"/> when <paramref name="Text"/> is not one.</param>
/// <param name="IsDevelopmentBuild">Whether this is a development build, which never reports an update.</param>
public sealed record RunningVersion(string Text, SemanticVersion? Parsed, bool IsDevelopmentBuild)
{
    /// <summary>Reads the version of <paramref name="assembly"/>'s <c>AssemblyInformationalVersion</c>.</summary>
    /// <param name="assembly">The assembly that carries the build's version.</param>
    public static RunningVersion FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();

        return Parse(informational);
    }

    /// <summary>Parses an informational version such as <c>0.1.0+abc1234</c>.</summary>
    /// <param name="informationalVersion">The version text, or <see langword="null"/> when there is none.</param>
    public static RunningVersion Parse(string? informationalVersion)
    {
        var text = informationalVersion?.Trim() ?? string.Empty;

        // The source revision after '+' is build noise here.
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            text = text[..plus];
        }

        if (text.Length == 0)
        {
            text = "0.0.0";
        }

        var parsed = SemanticVersion.TryParse(text);
        var development = parsed is null
            || (parsed is { Major: 0, Minor: 0, Patch: 0 } && parsed.PreReleaseIdentifiers.Count > 0);

        return new RunningVersion(text, parsed, development);
    }
}
