using System.Xml;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// Reads a worksheet's comments (notes) from the comments part it refers to: each comment's cell, its text (its runs'
/// text, phonetic runs left out) and its author. Excel keeps a note for each thread of threaded comments there too, so
/// threaded comments are read through it.
/// </summary>
internal static class XlsxComments
{
    /// <summary>The most comments a sheet keeps.</summary>
    private const int MaxComments = 100_000;

    /// <summary>Reads the comments of a worksheet into it; a comments part that is damaged is left out.</summary>
    public static void Read(XlsxPackage package, string sheetPath, XlsxWorksheet sheet, Action? checkpoint)
    {
        List<(string Id, string Type, string Target)> relationships;
        try
        {
            relationships = package.Relationships(sheetPath);
        }
        catch (SpreadsheetFormatException)
        {
            return;
        }

        foreach (var (_, type, target) in relationships)
        {
            if (type != "comments")
            {
                continue;
            }

            var comments = new List<(int, int, string, string?)>();
            try
            {
                package.Parse(target, reader => Parse(reader, comments, checkpoint), false);
            }
            catch (SpreadsheetFormatException)
            {
                continue; // A damaged part: its comments are left out.
            }

            sheet.Comments.AddRange(comments.Take(MaxComments - sheet.Comments.Count));
        }
    }

    private static bool Parse(XmlReader reader, List<(int, int, string, string?)> comments, Action? checkpoint)
    {
        var authors = new List<string>();
        while (reader.Read() && comments.Count < MaxComments)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "author" when authors.Count < MaxComments:
                    authors.Add(XlsxWorkbook.ReadText(reader));
                    break;
                case "comment":
                {
                    if (comments.Count % 1024 == 0)
                    {
                        checkpoint?.Invoke();
                    }

                    var position = XlsxWorkbook.ParseReference(reader.GetAttribute("ref") ?? string.Empty);
                    var author = int.TryParse(reader.GetAttribute("authorId"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                        && id < authors.Count ? authors[id] : null;
                    var text = reader.IsEmptyElement ? string.Empty : Text(reader);
                    if (position is ( >= 0 and < XlsxWorkbook.MaxRows, >= 0 and < XlsxWorkbook.MaxColumns) && !string.IsNullOrWhiteSpace(text))
                    {
                        comments.Add((position.Value.Row, position.Value.Column, text.TrimEnd(), string.IsNullOrWhiteSpace(author) ? null : author));
                    }

                    break;
                }
            }
        }

        return true;
    }

    /// <summary>The text of a comment: its <c>t</c> elements (not those of phonetic runs), the reader left on its end.</summary>
    private static string Text(XmlReader reader)
    {
        var builder = new System.Text.StringBuilder();
        var depth = reader.Depth;
        var phonetic = -1;
        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == phonetic)
                {
                    phonetic = -1;
                }

                continue;
            }

            if (reader.LocalName == "rPh" && !reader.IsEmptyElement)
            {
                phonetic = reader.Depth;
            }
            else if (reader.LocalName == "t" && phonetic < 0 && builder.Length < ConditionalFormula.MaxLength * 4)
            {
                builder.Append(XlsxWorkbook.ReadText(reader));
            }
        }

        return XlsxWorkbook.WithoutLoneSurrogates(builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
