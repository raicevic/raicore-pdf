using System.Text;

namespace RaiCore.Pdf.Sheets.Csv;

/// <summary>
/// Splits CSV text into records of fields (RFC 4180), leniently, as spreadsheet programs read it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Records end at CRLF, LF or a lone CR; a line break at the end of the text ends the last record (it does not
/// start an empty one). Empty lines elsewhere are records with one empty field.</item>
/// <item>A field that starts with a double quote is quoted: it runs to the next quote that is not doubled, and may hold
/// delimiters, line breaks and doubled quotes (<c>""</c>, one quote). Spaces and tabs between the closing quote and
/// the delimiter are left out.</item>
/// <item>A quoted field that is malformed, because its closing quote is followed by anything else or because it is
/// never closed, is read as an unquoted field instead (its quotes are ordinary characters, and it ends at the next
/// delimiter or line break), so that one stray quote cannot swallow the records after it. Quotes inside unquoted
/// fields are ordinary characters too.</item>
/// </list>
/// The text is read in linear time: a quoted field that opens inside the stretch a malformed one covered meets a
/// doubled quote at once (every quote in that stretch was doubled), so it is short.
/// </remarks>
internal static class CsvParser
{
    /// <summary>How many records go by between calls of the checkpoint.</summary>
    private const int CheckpointInterval = 1024;

    /// <summary>Splits <paramref name="text"/> into records.</summary>
    /// <param name="text">The text.</param>
    /// <param name="delimiter">The field delimiter.</param>
    /// <param name="maxCells">
    /// The most cells (records times the most fields any record has) allowed: more throw the
    /// <see cref="ResourceLimits.MaxTableCells"/> exception, before they take up memory.
    /// </param>
    /// <param name="maxRecords">Stop after this many records (to sample the text).</param>
    /// <param name="checkpoint">Called regularly; throws to stop (time limit, cancellation).</param>
    public static List<string[]> Parse(string text, char delimiter, long maxCells = long.MaxValue, int maxRecords = int.MaxValue, Action? checkpoint = null)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var columns = 0;
        var pos = 0;
        while (pos < text.Length && records.Count < maxRecords)
        {
            fields.Clear();
            while (true)
            {
                field.Clear();
                pos = ReadField(text, pos, delimiter, field);
                fields.Add(field.ToString());
                if ((long)(records.Count + 1) * Math.Max(columns, fields.Count) > maxCells)
                {
                    throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxTableCells), maxCells,
                        $"The CSV data has more than the limit of {maxCells} cells (rows times columns).");
                }

                if (pos < text.Length && text[pos] == delimiter)
                {
                    pos++;
                    continue;
                }

                // The end of the record: a line break (CRLF, LF or CR) or the end of the text.
                if (pos < text.Length)
                {
                    pos += text[pos] == '\r' && pos + 1 < text.Length && text[pos + 1] == '\n' ? 2 : 1;
                }

                break;
            }

            records.Add([.. fields]);
            columns = Math.Max(columns, fields.Count);
            if (records.Count % CheckpointInterval == 0)
            {
                checkpoint?.Invoke();
            }
        }

        return records;
    }

    /// <summary>Reads the field at <paramref name="pos"/> into <paramref name="field"/>; returns the position after it.</summary>
    private static int ReadField(string text, int pos, char delimiter, StringBuilder field)
    {
        if (pos >= text.Length || text[pos] != '"')
        {
            return ReadUnquoted(text, pos, delimiter, field);
        }

        var open = pos;
        var segment = pos + 1;
        for (pos++; pos < text.Length; pos++)
        {
            if (text[pos] != '"')
            {
                continue;
            }

            if (pos + 1 < text.Length && text[pos + 1] == '"')
            {
                // A doubled quote: one quote.
                field.Append(text, segment, pos + 1 - segment);
                segment = ++pos + 1;
                continue;
            }

            // Closed: well-formed if the delimiter or the record's end follows (after spaces or tabs).
            var end = pos + 1;
            while (end < text.Length && text[end] is ' ' or '\t' && text[end] != delimiter)
            {
                end++;
            }

            if (end == text.Length || text[end] == delimiter || text[end] is '\r' or '\n')
            {
                field.Append(text, segment, pos - segment);
                return end;
            }

            break;
        }

        // Malformed (something follows the closing quote, or it is never closed): an unquoted field after all.
        field.Clear();
        return ReadUnquoted(text, open, delimiter, field);
    }

    private static int ReadUnquoted(string text, int pos, char delimiter, StringBuilder field)
    {
        var start = pos;
        while (pos < text.Length && text[pos] != delimiter && text[pos] != '\n' && text[pos] != '\r')
        {
            pos++;
        }

        field.Append(text, start, pos - start);
        return pos;
    }
}
