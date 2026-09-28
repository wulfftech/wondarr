namespace Wondarr.Api.Tests;

/// <summary>Builds the settings dictionaries the tests hand to <see cref="WondarrAppFactory"/>.</summary>
internal static class TestConfiguration
{
    /// <summary>Creates a settings dictionary from configuration keys and values.</summary>
    public static IReadOnlyDictionary<string, string?> Of(params (string Key, string Value)[] settings) =>
        settings.ToDictionary(
            setting => setting.Key,
            setting => (string?)setting.Value,
            StringComparer.OrdinalIgnoreCase);
}
