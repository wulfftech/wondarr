using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Wondarr.Core.Tests.Songs;

/// <summary>Hand-seeded songs and services for the list-filter and editor suites, so they do not depend on the add path.</summary>
internal static class SongSeed
{
    /// <summary>A quality below the Standard profile's cutoff (29).</summary>
    public const long LowQuality = 23;

    /// <summary>A quality at or above the Standard profile's cutoff (29).</summary>
    public const long HighQuality = 36;

    /// <summary>Adds one song (and an artist credit), optionally with a file.</summary>
    public static async Task<Song> AddAsync(
        WondarrDbContext context,
        string title,
        string artist = "Daft Punk",
        bool monitored = true,
        long? fileQuality = null,
        long libraryId = SeedData.DefaultLibraryId,
        long profileId = SeedData.StandardProfileId,
        string[]? tags = null)
    {
        var existing = await context.Artists.FirstOrDefaultAsync(row => row.Name == artist);
        var artistRow = existing ?? new Artist { Name = artist, SortName = artist };
        if (existing is null)
        {
            context.Artists.Add(artistRow);
            await context.SaveChangesAsync();
        }

        var song = new Song
        {
            Title = title,
            ArtistCredit = artist,
            PrimaryArtistId = artistRow.Id,
            Monitored = monitored,
            QualityProfileId = profileId,
            LibraryId = libraryId,
            AddedBy = "api",
            Tags = [.. tags ?? []],
        };
        song.Artists.Add(new SongArtist { Artist = artistRow, Role = ArtistRole.Main, Position = 1 });
        song.AlbumContext = new AlbumContext
        {
            Kind = AlbumContextKind.PseudoSingles,
            AlbumTitle = "Singles",
            AlbumArtist = artist,
            AlbumKey = Guid.NewGuid().ToString("D"),
        };

        if (fileQuality is { } quality)
        {
            song.File = new SongFile
            {
                Path = $"/music/{Guid.NewGuid():N}.flac",
                Codec = "flac",
                Container = "flac",
                Size = 1,
                QualityId = quality,
                SourceType = "soulseek",
            };
        }

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        return song;
    }

    /// <summary>A second library to move songs to.</summary>
    public static async Task<Library> AddLibraryAsync(WondarrDbContext context, string name = "Second")
    {
        var library = new Library { Name = name, RootPath = "/music2" };
        context.Libraries.Add(library);
        await context.SaveChangesAsync();

        return library;
    }

    /// <summary>The real song service over a context, with every provider faked.</summary>
    public static SongService Service(WondarrDbContext context) =>
        new(
            context,
            Substitute.For<IIdentityResolver>(),
            new AlbumPolicyEngine(() => Guid.NewGuid()),
            Substitute.For<IMusicBrainzClient>(),
            Substitute.For<ICoverArtResolver>(),
            SearchOnAddOff.Commands,
            SearchOnAddOff.Options,
            NullLogger<SongService>.Instance);

    /// <summary>The editor service, with the given (substituted) command queue.</summary>
    public static SongEditorService Editor(WondarrDbContext context, ICommandQueue commands) =>
        new(context, Service(context), commands);
}
