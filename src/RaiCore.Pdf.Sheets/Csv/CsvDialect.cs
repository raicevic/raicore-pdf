namespace RaiCore.Pdf.Sheets.Csv;

/// <summary>Finds the field delimiter of CSV text.</summary>
internal static class CsvDialect
{
    /// <summary>The delimiters tried, in order of preference when they split the text equally well.</summary>
    public static ReadOnlySpan<char> Candidates => [',', ';', '\t', '|'];

    /// <summary>How much of the text the detection looks at.</summary>
    private const int SampleLength = 64 * 1024;

    /// <summary>How many records of the sample the detection looks at.</summary>
    private const int SampleRecords = 200;

    /// <summary>
    /// The <c>sep=</c> line Excel reads and writes as the first line of a file to name its delimiter (<c>sep=;</c>):
    /// the delimiter and the length of the line with its line break, or <c>null</c> and 0 if the text has none.
    /// </summary>
    public static (char? Delimiter, int Length) SepLine(string text)
    {
        if (text.Length < 5 || !text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) || text[4] is '\r' or '\n' or '"')
        {
            return (null, 0);
        }

        var delimiter = text[4];
        return text.Length == 5 ? (delimiter, 5)
            : text[5] == '\n' ? (delimiter, 6)
            : text[5] == '\r' ? (delimiter, text.Length > 6 && text[6] == '\n' ? 7 : 6)
            : (null, 0);
    }

    /// <summary>
    /// The delimiter that splits the first records of the text most consistently into more than one field: the one
    /// whose most common field count the most records share, then the one with more fields, then the first of
    /// <see cref="Candidates"/>. Text that no candidate splits is one column, delimited by commas.
    /// </summary>
    public static char Detect(string text)
    {
        var sample = text.Length <= SampleLength ? text : text[..SampleLength];
        var truncated = sample.Length < text.Length;
        var best = ',';
        (double Consistency, int Fields) bestScore = (0, 0);
        foreach (var candidate in Candidates)
        {
            var records = CsvParser.Parse(sample, candidate, maxRecords: SampleRecords + 1);
            if (records.Count > 0 && (truncated || records.Count > SampleRecords))
            {
                records.RemoveAt(records.Count - 1); // Cut off by the sample's end.
            }

            // Empty lines have one field with every delimiter: they say nothing.
            records.RemoveAll(r => r is [""]);
            if (records.Count == 0)
            {
                continue;
            }

            var (fields, count) = records.GroupBy(r => r.Length)
                .Select(g => (Fields: g.Key, Count: g.Count()))
                .MaxBy(g => (g.Count, g.Fields));
            if (fields < 2)
            {
                continue;
            }

            var score = ((double)count / records.Count, fields);
            if (score.CompareTo(bestScore) > 0)
            {
                (best, bestScore) = (candidate, score);
            }
        }

        return best;
    }
}
