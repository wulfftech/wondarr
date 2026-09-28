using System.Collections.Frozen;
using System.Text;

namespace Wondarr.Core.Metadata;

/// <summary>
/// Folds accented Latin letters to their base letters ("Björk" → "Bjork", "Sigur Rós" → "Sigur Ros",
/// "Straße" → "Strasse") for name comparisons.
/// </summary>
/// <remarks>
/// The app runs with <c>InvariantGlobalization</c>, where <see cref="string.Normalize(NormalizationForm)"/>
/// has no Unicode data and returns non-ASCII text unchanged, so the usual "decompose and drop the
/// combining marks" trick silently does nothing. An explicit table covers Latin-1 Supplement and Latin
/// Extended-A, which is what artist and title names in practice use; anything else passes through.
/// </remarks>
public static class TextFolding
{
    private static readonly FrozenDictionary<char, string> Folds = BuildFolds();

    /// <summary>Returns <paramref name="text"/> with accented Latin letters replaced by their base letters.</summary>
    public static string RemoveDiacritics(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder? builder = null;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character < 'À' || !Folds.TryGetValue(character, out var folded))
            {
                builder?.Append(character);
                continue;
            }

            builder ??= new StringBuilder(text.Length).Append(text, 0, index);
            builder.Append(folded);
        }

        return builder?.ToString() ?? text;
    }

    private static FrozenDictionary<char, string> BuildFolds()
    {
        // Each group: the base letter(s) and every accented letter that folds to it.
        (string Base, string Accented)[] groups =
        [
            ("A", "ÀÁÂÃÄÅĀĂĄ"), ("a", "àáâãäåāăą"),
            ("AE", "Æ"), ("ae", "æ"),
            ("C", "ÇĆĈĊČ"), ("c", "çćĉċč"),
            ("D", "ĎĐÐ"), ("d", "ďđð"),
            ("E", "ÈÉÊËĒĔĖĘĚ"), ("e", "èéêëēĕėęě"),
            ("G", "ĜĞĠĢ"), ("g", "ĝğġģ"),
            ("H", "ĤĦ"), ("h", "ĥħ"),
            ("I", "ÌÍÎÏĨĪĬĮİ"), ("i", "ìíîïĩīĭįı"),
            ("J", "Ĵ"), ("j", "ĵ"),
            ("K", "Ķ"), ("k", "ķĸ"),
            ("L", "ĹĻĽĿŁ"), ("l", "ĺļľŀł"),
            ("N", "ÑŃŅŇŊ"), ("n", "ñńņňŉŋ"),
            ("O", "ÒÓÔÕÖØŌŎŐ"), ("o", "òóôõöøōŏő"),
            ("OE", "Œ"), ("oe", "œ"),
            ("R", "ŔŖŘ"), ("r", "ŕŗř"),
            ("S", "ŚŜŞŠ"), ("s", "śŝşšſ"),
            ("ss", "ß"),
            ("T", "ŢŤŦ"), ("t", "ţťŧ"),
            ("TH", "Þ"), ("th", "þ"),
            ("U", "ÙÚÛÜŨŪŬŮŰŲ"), ("u", "ùúûüũūŭůűų"),
            ("W", "Ŵ"), ("w", "ŵ"),
            ("Y", "ÝŶŸ"), ("y", "ýÿŷ"),
            ("Z", "ŹŻŽ"), ("z", "źżž"),
        ];

        var folds = new Dictionary<char, string>();
        foreach (var (baseLetters, accented) in groups)
        {
            foreach (var character in accented)
            {
                folds[character] = baseLetters;
            }
        }

        return folds.ToFrozenDictionary();
    }
}
