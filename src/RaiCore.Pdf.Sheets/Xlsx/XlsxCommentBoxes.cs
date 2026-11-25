using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using RaiCore.Pdf.DrawingML;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets.Drawings;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// Reads the boxes of the comments shown on a worksheet, for printing them as displayed: the note shapes of its legacy
/// drawing part (VML: <c>v:shape</c> with <c>x:ClientData ObjectType="Note"</c> and <c>x:Visible</c>), each with its
/// anchor (<c>x:Anchor</c>: column, offset, row, offset of its two corners, offsets in pixels), fill and outline, and the
/// rich text of its comment from the comments part.
/// </summary>
internal static class XlsxCommentBoxes
{
    /// <summary>The most boxes a sheet keeps.</summary>
    private const int MaxBoxes = 10_000;

    /// <summary>The size of a pixel of a VML anchor's offsets, in points (at 96 pixels an inch).</summary>
    private const double Pixel = 0.75;

    /// <summary>The size of a comment's text where its runs do not give one (Excel's default).</summary>
    private const double TextSize = 9;

    /// <summary>The font of a comment's text where its runs do not give one (Excel's default).</summary>
    private const string TextFont = "Tahoma";

    /// <summary>A note shape: its cell, anchor, fill and outline.</summary>
    private sealed record Note(int Row, int Column, int[] Anchor, PdfColor? Fill, ShapeLine? Line);

    /// <summary>
    /// Reads the boxes of a worksheet's shown comments into <see cref="XlsxWorksheet.CommentBoxes"/>; damaged parts are
    /// left out.
    /// </summary>
    public static void Read(XlsxPackage package, XlsxWorkbook workbook, string sheetPath, XlsxWorksheet sheet, Action? checkpoint)
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

        var notes = new List<Note>();
        foreach (var (_, type, target) in relationships)
        {
            if (type == "vmlDrawing")
            {
                try
                {
                    package.Parse(target, reader => ReadNotes(reader, notes, workbook.Styles.Colors, checkpoint), false);
                }
                catch (SpreadsheetFormatException)
                {
                    // A damaged part: its boxes are left out.
                }
            }
        }

        if (notes.Count == 0)
        {
            return;
        }

        var texts = new Dictionary<(int Row, int Column), ShapeText>();
        var wanted = notes.Select(n => (n.Row, n.Column)).ToHashSet();
        foreach (var (_, type, target) in relationships)
        {
            if (type == "comments")
            {
                try
                {
                    package.Parse(target, reader => ReadTexts(reader, workbook, wanted, texts), false);
                }
                catch (SpreadsheetFormatException)
                {
                    // A damaged part: its comments have no text.
                }
            }
        }

        foreach (var note in notes)
        {
            var (a, item) = (note.Anchor, new ShapeItem(Geometry.Rectangle)
            {
                Fill = note.Fill,
                Line = note.Line,
                Text = texts.GetValueOrDefault((note.Row, note.Column)),
            });
            sheet.CommentBoxes.Add(new SheetDrawing(AnchorKind.TwoCell, new AnchorEdge(a[0], a[1] * Pixel), new AnchorEdge(a[2], a[3] * Pixel), item)
            {
                Right = new AnchorEdge(a[4], a[5] * Pixel),
                Bottom = new AnchorEdge(a[6], a[7] * Pixel),
            });
        }
    }

    /// <summary>The shown note shapes of a VML part.</summary>
    private static bool ReadNotes(XmlReader reader, List<Note> notes, XlsxColors colors, Action? checkpoint)
    {
        while (notes.Count < MaxBoxes && reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "shape")
            {
                continue;
            }

            if (notes.Count % 256 == 0)
            {
                checkpoint?.Invoke();
            }

            XElement shape;
            using (var subtree = reader.ReadSubtree())
            {
                shape = XElement.Load(subtree);
            }

            XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(c => c.Name.LocalName == name);
            int? Number(XElement? e) => int.TryParse(e?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
            var data = Child(shape, "ClientData");
            if ((string?)data?.Attribute("ObjectType") != "Note" || Child(data, "Visible") is null
                || Number(Child(data, "Row")) is not (>= 0 and < XlsxWorkbook.MaxRows) || Number(Child(data, "Column")) is not (>= 0 and < XlsxWorkbook.MaxColumns))
            {
                continue;
            }

            var anchor = (Child(data, "Anchor")?.Value ?? string.Empty).Split(',')
                .Select(p => int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, 1_048_576) : -1).ToArray();
            if (anchor.Length != 8 || anchor.Any(n => n < 0))
            {
                continue;
            }

            bool On(string name, bool fallback) => (string?)shape.Attribute(name) is { } v ? v is "t" or "true" or "1" : fallback;
            var fill = On("filled", true) ? Color((string?)shape.Attribute("fillcolor"), colors) ?? CommentBox.Fill : (PdfColor?)null;
            var line = On("stroked", true)
                ? new ShapeLine(Weight((string?)shape.Attribute("strokeweight")), Color((string?)shape.Attribute("strokecolor"), colors) ?? PdfColor.Black, [])
                : null;
            notes.Add(new Note(Number(Child(data, "Row"))!.Value, Number(Child(data, "Column"))!.Value, anchor, fill, line));
        }

        return true;
    }

    /// <summary>
    /// A VML color: <c>#rrggbb</c> or <c>#rgb</c>, optionally followed by a palette index in brackets
    /// (<c>#ffffe1 [80]</c>), which a system color (<c>infoBackground [80]</c>) only has; <c>null</c> for other colors.
    /// </summary>
    internal static PdfColor? Color(string? value, XlsxColors colors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var bracket = text.IndexOf('[', StringComparison.Ordinal);
        var name = (bracket >= 0 ? text[..bracket] : text).Trim();
        if (name.StartsWith('#'))
        {
            var hex = name[1..];
            if (hex.Length == 3)
            {
                hex = string.Concat(hex.Select(c => $"{c}{c}"));
            }

            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                return PdfColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            }
        }

        return name switch
        {
            "black" => PdfColor.Black,
            "white" => PdfColor.White,
            "infoBackground" => CommentBox.Fill,
            "infoText" or "windowText" => PdfColor.Black,
            _ => null,
        };
    }

    /// <summary>A VML stroke weight (<c>0.75pt</c>, <c>1px</c>, a number of EMUs), in points; 0.75 by default.</summary>
    internal static double Weight(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        var (number, scale) = text.EndsWith("pt", StringComparison.Ordinal) ? (text[..^2], 1.0)
            : text.EndsWith("px", StringComparison.Ordinal) ? (text[..^2], Pixel)
            : (text, 1 / 12700.0);
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0
            ? Math.Min(n * scale, 100)
            : Pixel;
    }

    /// <summary>The rich text of the comments of <paramref name="wanted"/> cells, as the text of their boxes.</summary>
    private static bool ReadTexts(XmlReader reader, XlsxWorkbook workbook, HashSet<(int, int)> wanted, Dictionary<(int, int), ShapeText> texts)
    {
        (int Row, int Column)? cell = null;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "comment")
            {
                cell = XlsxWorkbook.ParseReference(reader.GetAttribute("ref") ?? string.Empty) is { } at && wanted.Contains(at) ? at : null;
            }
            else if (reader.LocalName == "text" && cell is { } at && !texts.ContainsKey(at))
            {
                var text = workbook.ReadRuns(reader, out var runs);
                texts[at] = BoxText(runs ?? [new CellRun(text, null)]);
                cell = null;
            }
        }

        return true;
    }

    /// <summary>A comment's runs as a box's text: a paragraph for each line, in each run's font (Excel's comment font where it gives none).</summary>
    internal static ShapeText BoxText(IReadOnlyList<CellRun> runs)
    {
        var paragraphs = new List<ShapeParagraph>();
        var line = new List<ShapeRun>();
        foreach (var run in runs)
        {
            var font = run.Font;
            var parts = XlsxWorkbook.WithoutLoneSurrogates(run.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')).Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    paragraphs.Add(new ShapeParagraph(line, TextAlignment.Left));
                    line = [];
                }

                if (parts[i].Length > 0)
                {
                    line.Add(new ShapeRun(parts[i], font?.Size ?? TextSize, font?.Bold ?? false, font?.Italic ?? false,
                        font is { Underline: not CellUnderline.None }, font?.Color ?? PdfColor.Black, font?.Name ?? TextFont));
                }
            }
        }

        paragraphs.Add(new ShapeParagraph(line, TextAlignment.Left));
        return new ShapeText(paragraphs) { Wrap = true };
    }
}
