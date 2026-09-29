using System.Text;

namespace FakeSlskd;

/// <summary>
/// The text normalisation the scenario matcher uses: lower-case, Latin diacritics folded, every
/// non-alphanumeric turned into a separator. A scenario entry matches a search when all of the
/// search's tokens occur among the tokens of the entry's path.
/// </summary>
public static class SearchText
{
    private static readonly Dictionary<char, string> Foldings = new()
    {
        ['à'] = "a", ['á'] = "a", ['â'] = "a", ['ã'] = "a", ['ä'] = "a", ['å'] = "a", ['ā'] = "a", ['ă'] = "a", ['ą'] = "a",
        ['æ'] = "ae",
        ['ç'] = "c", ['ć'] = "c", ['č'] = "c", ['ĉ'] = "c",
        ['ď'] = "d", ['đ'] = "d",
        ['è'] = "e", ['é'] = "e", ['ê'] = "e", ['ë'] = "e", ['ē'] = "e", ['ę'] = "e", ['ě'] = "e",
        ['ğ'] = "g",
        ['ì'] = "i", ['í'] = "i", ['î'] = "i", ['ï'] = "i", ['ī'] = "i", ['ı'] = "i",
        ['ł'] = "l", ['ĺ'] = "l", ['ľ'] = "l",
        ['ñ'] = "n", ['ń'] = "n", ['ň'] = "n",
        ['ò'] = "o", ['ó'] = "o", ['ô'] = "o", ['õ'] = "o", ['ö'] = "o", ['ø'] = "o", ['ō'] = "o", ['ő'] = "o",
        ['ř'] = "r", ['ŕ'] = "r",
        ['ß'] = "ss", ['š'] = "s", ['ś'] = "s", ['ş'] = "s",
        ['ť'] = "t", ['ţ'] = "t",
        ['ù'] = "u", ['ú'] = "u", ['û'] = "u", ['ü'] = "u", ['ū'] = "u", ['ů'] = "u", ['ű'] = "u",
        ['ý'] = "y", ['ÿ'] = "y",
        ['ž'] = "z", ['ź'] = "z", ['ż'] = "z",
    };

    /// <summary>Folds <paramref name="value"/> to lower-case ASCII words separated by single spaces.</summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        var pendingSeparator = false;

        foreach (var raw in value.ToLowerInvariant())
        {
            var text = Foldings.TryGetValue(raw, out var folded) ? folded : raw.ToString();

            foreach (var c in text)
            {
                if (char.IsAsciiLetterOrDigit(c))
                {
                    if (pendingSeparator && builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    pendingSeparator = false;
                    builder.Append(c);
                }
                else
                {
                    pendingSeparator = true;
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>Normalises <paramref name="value"/> and splits it into tokens.</summary>
    public static string[] Tokenize(string value)
    {
        var normalized = Normalize(value);

        return normalized.Length == 0
            ? []
            : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Whether every token of <paramref name="searchText"/> occurs among the tokens of
    /// <paramref name="path"/>. An empty search text matches nothing.
    /// </summary>
    public static bool Matches(string searchText, string path)
    {
        var wanted = Tokenize(searchText);

        if (wanted.Length == 0)
        {
            return false;
        }

        var offered = new HashSet<string>(Tokenize(path), StringComparer.Ordinal);

        return wanted.All(offered.Contains);
    }
}