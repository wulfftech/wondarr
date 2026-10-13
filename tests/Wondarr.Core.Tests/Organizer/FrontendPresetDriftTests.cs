using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

/// <summary>
/// The Library settings page keeps its own copy of the layout presets (no endpoint exposes them), for
/// its "Reset to the preset" button. This fails when the copy drifts from <see cref="NamingTemplate.PresetTemplates"/>.
/// </summary>
public class FrontendPresetDriftTests
{
    [Fact]
    public void The_settings_page_offers_exactly_the_presets_the_backend_renders_with()
    {
        var page = File.ReadAllText(PagePath());

        var expected = new Dictionary<LibraryLayout, string>
        {
            [LibraryLayout.Flat] = "flat",
            [LibraryLayout.Artist] = "artist",
            [LibraryLayout.ArtistAlbum] = "artistAlbum",
            [LibraryLayout.Plexamp] = "plexamp",
        };

        foreach (var (layout, key) in expected)
        {
            page.Should().Contain(
                $"  {key}: '{NamingTemplate.PresetTemplates[layout]}',",
                $"the page's {key} preset must equal NamingTemplate.PresetTemplates[{layout}]");
        }
    }

    private static string PagePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "frontend", "src", "pages", "settings", "LibrarySettingsPage.tsx");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("frontend/src/pages/settings/LibrarySettingsPage.tsx was not found above the test binaries.");
    }
}
