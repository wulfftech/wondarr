using Wondarr.Core.Jobs;
using Wondarr.Core.Searching;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Wondarr.Core.Tests.Songs;

/// <summary>
/// What a <see cref="Wondarr.Core.Songs.SongService"/> gets in suites that are not about search on add:
/// a queue nobody reads and settings with <c>search.search_on_add</c> off, so adding songs queues nothing.
/// </summary>
internal static class SearchOnAddOff
{
    /// <summary>A fresh substitute queue.</summary>
    public static ICommandQueue Commands => Substitute.For<ICommandQueue>();

    /// <summary>Search settings with search on add turned off.</summary>
    public static IOptionsMonitor<SearchOptions> Options
    {
        get
        {
            var options = Substitute.For<IOptionsMonitor<SearchOptions>>();
            options.CurrentValue.Returns(new SearchOptions { SearchOnAdd = false });

            return options;
        }
    }
}
