using System.ComponentModel.DataAnnotations;
using Wondarr.Core.Metadata;

namespace Wondarr.Api.Metadata;

/// <summary>
/// Which of the optional metadata keys are set. The keys themselves are never returned; a settings
/// page that could read one back would put it in every response, log and browser cache on the way.
/// </summary>
/// <param name="AcoustIdKeySet">Whether an AcoustID client key is stored.</param>
/// <param name="AcoustIdLocked">Whether the environment sets the AcoustID key (<c>APP__ACOUSTID__CLIENT_KEY</c>), so it cannot be changed here.</param>
/// <param name="LastFmKeySet">Whether a Last.fm API key is stored.</param>
/// <param name="LastFmLocked">Whether the environment sets the Last.fm key (<c>APP__LASTFM__API_KEY</c>), so it cannot be changed here.</param>
public sealed record MetadataSettingsResource(
    bool AcoustIdKeySet,
    bool AcoustIdLocked,
    bool LastFmKeySet,
    bool LastFmLocked)
{
    /// <summary>Maps the service's settings onto the resource.</summary>
    /// <param name="settings">The settings to map.</param>
    public static MetadataSettingsResource From(MetadataSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new MetadataSettingsResource(
            settings.AcoustIdKeySet,
            settings.AcoustIdLocked,
            settings.LastFmKeySet,
            settings.LastFmLocked);
    }
}

/// <summary>A change to the keys. An absent field is left alone; an empty string removes the key.</summary>
/// <param name="AcoustIdClientKey">The new AcoustID client key.</param>
/// <param name="LastFmApiKey">The new Last.fm API key.</param>
public sealed record MetadataSettingsUpdateResource(string? AcoustIdClientKey = null, string? LastFmApiKey = null)
{
    /// <summary>Maps the request body onto the service's update.</summary>
    public MetadataSettingsUpdate ToUpdate() => new(AcoustIdClientKey, LastFmApiKey);
}

/// <summary>Asks a metadata service whether it accepts a key.</summary>
/// <param name="Service"><c>lastfm</c> or <c>acoustid</c>.</param>
/// <param name="Key">The key to try; absent to try the stored one.</param>
public sealed record MetadataKeyTestRequest([Required] string Service, string? Key = null);

/// <summary>What a key test found.</summary>
/// <param name="Ok">Whether the service accepted the key.</param>
/// <param name="Message">A sentence to show; never the key.</param>
public sealed record MetadataKeyTestResource(bool Ok, string Message);
