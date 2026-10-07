using System.Text;

namespace Wondarr.Core.ImportLists.Csv;

/// <summary>
/// A small RFC 4180 reader: quoted fields with doubled quotes and embedded separators or line breaks,
/// CRLF or LF line ends, a leading byte-order mark, and the separator guessed from the header line
/// (comma, semicolon or tab — spreadsheet exports in some locales use semicolons).
/// </summary>
public static class CsvText
{
    private static readonly char[] Separators = [',', ';', '\t'];

    /// <summary>Reads every row of a CSV text. Blank lines are dropped; rows keep their own field counts.</summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The rows, the header first.</returns>
    public static List<string[]> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var separator = GuessSeparator(text);
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldStarted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];

            if (quoted)
            {
                if (c == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                quoted = true;
                fieldStarted = true;
            }
            else if (c == separator)
            {
                fields.Add(field.ToString());
                field.Clear();
                fieldStarted = true;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                EndRow();
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }

        EndRow();

        return rows;

        void EndRow()
        {
            if (fieldStarted || field.Length > 0 || fields.Count > 0)
            {
                fields.Add(field.ToString());

                if (fields.Exists(value => value.Length > 0))
                {
                    rows.Add([.. fields]);
                }
            }

            fields.Clear();
            field.Clear();
            fieldStarted = false;
        }
    }

    /// <summary>The separator that occurs most often outside quotes on the first line; a comma when none does.</summary>
    private static char GuessSeparator(string text)
    {
        var counts = new int[Separators.Length];
        var quoted = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '\r' or '\n')
            {
                break;
            }
            else if (!quoted)
            {
                var at = Array.IndexOf(Separators, c);
                if (at >= 0)
                {
                    counts[at]++;
                }
            }
        }

        var best = 0;
        for (var index = 1; index < counts.Length; index++)
        {
            if (counts[index] > counts[best])
            {
                best = index;
            }
        }

        return Separators[best];
    }
}
