using System.Text;

namespace RaiCore.Pdf.Sheets.Workbooks;

/// <summary>
/// Writes a worksheet as delimited text: its rows from the first to the last with a value, each with the fields of
/// the first to the last column with a value (so that every row has as many), quoted as RFC 4180 describes.
/// </summary>
internal static class CsvWriter
{
    /// <summary>Writes a worksheet; without one (a workbook of chart sheets only), no rows.</summary>
    public static void Write(Worksheet? sheet, Stream stream, CsvSaveOptions options)
    {
        var encoding = options.Encoding;
        if (options.ByteOrderMark)
        {
            stream.Write(ByteOrderMark(encoding));
        }

        if (sheet is null)
        {
            return;
        }

        var cells = sheet.Cells.GroupBy(c => c.Row).ToDictionary(g => g.Key, g => g.ToList());
        var lastRow = cells.Count == 0 ? 0 : cells.Keys.Max();
        var lastColumn = cells.Count == 0 ? 0 : cells.Values.Max(r => r.Max(c => c.Column));
        var columns = Enumerable.Range(1, lastColumn).Where(c => options.IncludeHiddenColumns || !sheet.Column(c).IsHidden).ToArray();
        var position = new Dictionary<int, int>();
        for (var i = 0; i < columns.Length; i++)
        {
            position[columns[i]] = i;
        }

        var guard = sheet.Workbook.Package.Guard;
        guard.Start();
        var fields = new string[columns.Length];
        var line = new StringBuilder();
        for (var row = 1; row <= lastRow; row++)
        {
            if (!options.IncludeHiddenRows && sheet.Row(row).IsHidden)
            {
                continue;
            }

            Array.Fill(fields, string.Empty);
            foreach (var cell in cells.GetValueOrDefault(row) ?? [])
            {
                if (position.TryGetValue(cell.Column, out var index))
                {
                    fields[index] = options.FormattedValues ? cell.GetDisplayText(options.Culture) : cell.RawText();
                }
            }

            line.Clear();
            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    line.Append(options.Separator);
                }

                Append(line, fields[i], options);
            }

            line.Append(options.LineEnding);
            stream.Write(encoding.GetBytes(line.ToString()));
            if ((row & 1023) == 0)
            {
                guard.Checkpoint();
            }
        }
    }

    /// <summary>Appends a field, in double quotes (with quotes inside doubled) when the options ask for them.</summary>
    private static void Append(StringBuilder line, string field, CsvSaveOptions options)
    {
        var quote = field.Length > 0 && (options.Quoting == CsvQuoting.Always
            || field.Contains(options.Separator, StringComparison.Ordinal) || field.AsSpan().IndexOfAny('"', '\r', '\n') >= 0
            || char.IsWhiteSpace(field[0]) || char.IsWhiteSpace(field[^1]));
        if (!quote)
        {
            line.Append(field);
            return;
        }

        line.Append('"').Append(field.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }

    /// <summary>The byte order mark of an encoding: of UTF-8, UTF-16 and UTF-32 even when the encoding object has none.</summary>
    private static byte[] ByteOrderMark(Encoding encoding) => encoding.CodePage switch
    {
        65001 => [0xEF, 0xBB, 0xBF],
        1200 => [0xFF, 0xFE],
        1201 => [0xFE, 0xFF],
        12000 => [0xFF, 0xFE, 0x00, 0x00],
        12001 => [0x00, 0x00, 0xFE, 0xFF],
        _ => encoding.GetPreamble(),
    };
}
