using System.Text.Json;
using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.ImportLists.Csv;
using Xunit;

namespace Wondarr.Core.Tests.ImportLists;

/// <summary>
/// The CSV provider (DECISIONS build session 7 #8): Exportify by English names and, in another
/// language, by its URIs and column positions; any other CSV by usual names or the user's mapping.
/// </summary>
public sealed class CsvImportListProviderTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "importlists");

    [Fact]
    public void Reads_quoted_fields_line_breaks_and_a_byte_order_mark()
    {
        var rows = CsvText.Read("\uFEFFa,b,c\r\n\"x, y\",\"say \"\"hi\"\"\",\"two\nlines\"\r\n\r\n1,,3");

        rows.Should().HaveCount(3);
        rows[0].Should().Equal("a", "b", "c");
        rows[1].Should().Equal("x, y", "say \"hi\"", "two\nlines");
        rows[2].Should().Equal("1", string.Empty, "3");
    }

    [Fact]
    public void Guesses_a_semicolon_separator_from_the_header()
    {
        var rows = CsvText.Read("Song;Performer\nGet Lucky;Daft Punk");

        rows[1].Should().Equal("Get Lucky", "Daft Punk");
    }

    [Fact]
    public void Maps_an_english_exportify_export_by_name()
    {
        var preview = CsvImportListProvider.Preview(Fixture("exportify-en.csv"), Empty());

        preview.Problems.Should().BeEmpty();
        preview.Format.Should().Be(CsvImportListProvider.ExportifyFormat);
        preview.RowCount.Should().Be(3);

        var queen = preview.Sample[0];
        queen.ExternalId.Should().Be("spotify:track:4u7EnebtmKWzUH433cf5Qv");
        queen.Title.Should().Be("Bohemian Rhapsody - Remastered 2011");
        queen.Artist.Should().Be("Queen");
        queen.Album.Should().Be("A Night At The Opera (2011 Remaster)");
        queen.DurationMs.Should().Be(354_320);
        queen.Isrc.Should().Be("GBUM71029604");

        // Exportify joins several artists with commas; the first is the main one.
        preview.Sample[1].Artist.Should().Be("Daft Punk");

        // A title with a comma inside quotes stays whole; a row without an ISRC keeps its text.
        preview.Sample[2].Title.Should().Be("A Song, With a Comma");
        preview.Sample[2].Isrc.Should().BeNull();
    }

    [Fact]
    public void Maps_an_exportify_export_in_another_language_by_position()
    {
        var preview = CsvImportListProvider.Preview(Fixture("exportify-de.csv"), Empty());

        preview.Problems.Should().BeEmpty();
        preview.Format.Should().Be(CsvImportListProvider.ExportifyFormat);
        preview.Sample.Should().ContainSingle();
        preview.Sample[0].Title.Should().Be("Bohemian Rhapsody - Remastered 2011");
        preview.Sample[0].Artist.Should().Be("Queen");
        preview.Sample[0].DurationMs.Should().Be(354_320);
        preview.Sample[0].Isrc.Should().Be("GBUM71029604");
        preview.Sample[0].ExternalId.Should().Be("spotify:track:4u7EnebtmKWzUH433cf5Qv");
    }

    [Fact]
    public void Maps_a_generic_file_by_the_users_column_names_and_reads_lengths()
    {
        var mapping = Settings("""{"titleColumn":"Song","artistColumn":"Performer","durationColumn":"Length"}""");

        var preview = CsvImportListProvider.Preview(Fixture("generic-semicolon.csv"), mapping);

        preview.Problems.Should().BeEmpty();
        preview.Format.Should().Be(CsvImportListProvider.GenericFormat);
        preview.Sample.Select(entry => entry.DurationMs).Should().Equal(369_000, 355_000);
        preview.Sample[0].ExternalId.Should().Be("text:daft punk|get lucky");
    }

    [Fact]
    public void A_generic_file_with_usual_names_needs_no_mapping()
    {
        var preview = CsvImportListProvider.Preview("Title,Artist,ISRC\nGet Lucky,Daft Punk,usqx9-13-00108\n", Empty());

        preview.Problems.Should().BeEmpty();
        preview.Sample[0].Isrc.Should().Be("USQX91300108");
        preview.Sample[0].ExternalId.Should().Be("isrc:USQX91300108");
    }

    [Fact]
    public void A_file_whose_title_column_cannot_be_found_lists_its_columns()
    {
        var preview = CsvImportListProvider.Preview("Foo,Bar\n1,2\n", Empty());

        preview.Problems.Should().ContainSingle()
            .Which.Should().Contain("Foo, Bar");
    }

    [Fact]
    public void A_mapped_column_the_file_does_not_have_is_named()
    {
        var preview = CsvImportListProvider.Preview(
            Fixture("generic-semicolon.csv"),
            Settings("""{"titleColumn":"Song","isrcColumn":"Code"}"""));

        preview.Problems.Should().ContainSingle()
            .Which.Should().Contain("'Code'");
    }

    [Fact]
    public async Task Fetch_reads_the_stored_file_and_validate_wants_one()
    {
        var provider = new CsvImportListProvider();

        provider.Validate(Empty(), null).Should().ContainSingle();

        var list = new ImportList { Type = ImportList.CsvType, SourceText = Fixture("exportify-en.csv") };
        var fetched = await provider.FetchAsync(list, CancellationToken.None);

        fetched.Success.Should().BeTrue();
        fetched.Entries.Should().HaveCount(3);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FixtureDir, name));

    private static JsonElement Empty() => Settings("{}");

    private static JsonElement Settings(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}
