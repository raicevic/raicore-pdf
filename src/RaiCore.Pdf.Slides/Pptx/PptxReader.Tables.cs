using System.Xml;
using System.Xml.Linq;
using RaiCore.Pdf.Slides.Model;

namespace RaiCore.Pdf.Slides.Pptx;

/// <summary>Tables (<c>a:tbl</c>): grid, rows, cells with their table style parts, own fills, borders and text.</summary>
internal sealed partial class PptxReader
{
    /// <summary>The id of PowerPoint's default table style, Medium Style 2 - Accent 1.</summary>
    private const string DefaultTableStyleId = "{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}";

    /// <summary>
    /// Medium Style 2 - Accent 1, built into PowerPoint (files that use it need not define it, and files made by other
    /// tools often do not): accent fills, white borders, a bold white header row.
    /// </summary>
    private const string DefaultTableStyle = """
        <tblStyle styleId="{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}">
          <wholeTbl>
            <tcTxStyle><fontRef idx="minor"/><schemeClr val="dk1"/></tcTxStyle>
            <tcStyle>
              <tcBdr>
                <left><ln w="12700"><solidFill><schemeClr val="lt1"/></solidFill></ln></left>
                <right><ln w="12700"><solidFill><schemeClr val="lt1"/></solidFill></ln></right>
                <top><ln w="12700"><solidFill><schemeClr val="lt1"/></solidFill></ln></top>
                <bottom><ln w="12700"><solidFill><schemeClr val="lt1"/></solidFill></ln></bottom>
                <insideH><ln w="12700"><solidFill><schemeClr val="lt1"/></solidFill></ln></insideH>
                <insideV><ln w="12700"><solidFill><schemeClr val="lt1"/></solidFill></ln></insideV>
              </tcBdr>
              <fill><solidFill><schemeClr val="accent1"><tint val="20000"/></schemeClr></solidFill></fill>
            </tcStyle>
          </wholeTbl>
          <band1H><tcStyle><fill><solidFill><schemeClr val="accent1"><tint val="40000"/></schemeClr></solidFill></fill></tcStyle></band1H>
          <band1V><tcStyle><fill><solidFill><schemeClr val="accent1"><tint val="40000"/></schemeClr></solidFill></fill></tcStyle></band1V>
          <lastCol>
            <tcTxStyle b="on"><fontRef idx="minor"/><schemeClr val="lt1"/></tcTxStyle>
            <tcStyle><fill><solidFill><schemeClr val="accent1"/></solidFill></fill></tcStyle>
          </lastCol>
          <firstCol>
            <tcTxStyle b="on"><fontRef idx="minor"/><schemeClr val="lt1"/></tcTxStyle>
            <tcStyle><fill><solidFill><schemeClr val="accent1"/></solidFill></fill></tcStyle>
          </firstCol>
          <lastRow>
            <tcTxStyle b="on"><fontRef idx="minor"/><schemeClr val="lt1"/></tcTxStyle>
            <tcStyle>
              <tcBdr><top><ln w="38100"><solidFill><schemeClr val="lt1"/></solidFill></ln></top></tcBdr>
              <fill><solidFill><schemeClr val="accent1"/></solidFill></fill>
            </tcStyle>
          </lastRow>
          <firstRow>
            <tcTxStyle b="on"><fontRef idx="minor"/><schemeClr val="lt1"/></tcTxStyle>
            <tcStyle>
              <tcBdr><bottom><ln w="38100"><solidFill><schemeClr val="lt1"/></solidFill></ln></bottom></tcBdr>
              <fill><solidFill><schemeClr val="accent1"/></solidFill></fill>
            </tcStyle>
          </firstRow>
        </tblStyle>
        """;

    private static readonly Lazy<XElement> DefaultStyle = new(() =>
    {
        using var reader = XmlReader.Create(new StringReader(DefaultTableStyle), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XElement.Load(reader);
    });

    /// <summary>Reads a table in a frame's box.</summary>
    private TableShape Table(XElement tbl, ShapeBox box, XElement? cNvPr, Sheet sheet)
    {
        var table = new TableShape { Box = box, Name = cNvPr.Attr("name"), AltText = cNvPr.Attr("descr") };
        foreach (var column in tbl.El("tblGrid").Els("gridCol"))
        {
            table.Columns.Add(Math.Max(column.Points("w") ?? 0, 0));
        }

        var rows = tbl.Els("tr").ToList();
        if ((long)rows.Count * Math.Max(table.Columns.Count, 1) > _limits.MaxTableCells)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxTableCells), _limits.MaxTableCells,
                $"A table has more cells ({rows.Count} rows by {table.Columns.Count} columns) than ResourceLimits.MaxTableCells allows ({_limits.MaxTableCells}).");
        }

        var tblPr = tbl.El("tblPr");
        var style = TableStyle(tblPr);
        var (firstRow, lastRow, firstCol, lastCol, bandRow, bandCol) =
            (tblPr.Bool("firstRow") == true, tblPr.Bool("lastRow") == true, tblPr.Bool("firstCol") == true, tblPr.Bool("lastCol") == true,
             tblPr.Bool("bandRow") == true, tblPr.Bool("bandCol") == true);
        var columnCount = table.Columns.Count;
        for (var r = 0; r < rows.Count; r++)
        {
            _checkpoint?.Invoke();
            var row = new TableRow { Height = Math.Max(rows[r].Points("h") ?? 0, 0) };
            var cells = rows[r].Els("tc").Take(Math.Max(columnCount, 1)).ToList();
            for (var c = 0; c < cells.Count; c++)
            {
                // The style's parts that apply to the cell, in the order they override each other.
                var parts = new List<(XElement Part, bool RowPart, bool ColumnPart)>();
                void Add(string name, bool rowPart = false, bool columnPart = false)
                {
                    if (style.El(name) is { } part)
                    {
                        parts.Add((part, rowPart, columnPart));
                    }
                }

                var isFirstRow = firstRow && r == 0;
                var isLastRow = lastRow && r == rows.Count - 1;
                Add("wholeTbl");
                if (bandCol)
                {
                    var k = c - (firstCol ? 1 : 0);
                    if (k >= 0 && !(lastCol && c == columnCount - 1))
                    {
                        Add(k % 2 == 0 ? "band1V" : "band2V");
                    }
                }

                if (bandRow && !isFirstRow && !isLastRow)
                {
                    var k = r - (firstRow ? 1 : 0);
                    Add(k % 2 == 0 ? "band1H" : "band2H");
                }

                if (firstCol && c == 0)
                {
                    Add("firstCol", columnPart: true);
                }

                if (lastCol && c == columnCount - 1)
                {
                    Add("lastCol", columnPart: true);
                }

                if (isFirstRow)
                {
                    Add("firstRow", rowPart: true);
                }

                if (isLastRow)
                {
                    Add("lastRow", rowPart: true);
                }

                row.Cells.Add(Cell(cells[c], parts, r, c, rows.Count, columnCount, sheet));
            }

            // Rows with fewer cells than columns get empty ones.
            while (row.Cells.Count < columnCount)
            {
                row.Cells.Add(new TableCell());
            }

            table.Rows.Add(row);
        }

        return table;
    }

    /// <summary>The table style of a table: its own, one of the presentation's, or the built-in default.</summary>
    private XElement? TableStyle(XElement? tblPr)
    {
        if (tblPr.El("tblStyle") is { } own)
        {
            return own;
        }

        var id = tblPr.El("tableStyleId")?.Value.Trim();
        if (id is null)
        {
            return null;
        }

        var defined = _tableStyles?.Elements().FirstOrDefault(s => s.Name.LocalName == "tblStyle" && string.Equals(s.Attr("styleId"), id, StringComparison.OrdinalIgnoreCase));
        return defined ?? (string.Equals(id, DefaultTableStyleId, StringComparison.OrdinalIgnoreCase) ? DefaultStyle.Value : null);
    }

    /// <summary>Reads a cell with the style parts that apply to it.</summary>
    private TableCell Cell(XElement tc, List<(XElement Part, bool RowPart, bool ColumnPart)> parts, int r, int c, int rowCount, int columnCount, Sheet sheet)
    {
        Fill? fill = null;
        var borders = new XElement?[4];
        var borderColors = new PdfColor?[4];
        var textLayer = new XElement("defRPr");
        foreach (var (part, rowPart, columnPart) in parts)
        {
            var tcStyle = part.El("tcStyle");
            if (tcStyle.El("fill") is { } partFill)
            {
                fill = FillOf(partFill, sheet, null, null).Fill;
            }
            else if (tcStyle.El("fillRef") is { } fillRef)
            {
                fill = StyleFill(fillRef, sheet);
            }

            // left, top, right, bottom: the outer edge on the table's sides, the inside lines elsewhere (a row or
            // column part's own edges across it).
            var tcBdr = tcStyle.El("tcBdr");
            string[] names =
            [
                c == 0 || columnPart ? "left" : "insideV",
                r == 0 || rowPart ? "top" : "insideH",
                c == columnCount - 1 || columnPart ? "right" : "insideV",
                r == rowCount - 1 || rowPart ? "bottom" : "insideH",
            ];
            for (var i = 0; i < 4; i++)
            {
                if (tcBdr.El(names[i]) is { } edge)
                {
                    if (edge.El("ln") is { } ln)
                    {
                        (borders[i], borderColors[i]) = (ln, null);
                    }
                    else if (edge.El("lnRef") is { } lnRef)
                    {
                        (borders[i], borderColors[i]) = (StyleLine(lnRef, sheet), DrawingColors.Child(lnRef, sheet.Colors, null));
                    }
                }
            }

            if (part.El("tcTxStyle") is { } text)
            {
                if (text.Attr("b") is { } bold)
                {
                    textLayer.SetAttributeValue("b", bold == "on" ? "1" : "0");
                }

                if (text.Attr("i") is { } italic)
                {
                    textLayer.SetAttributeValue("i", italic == "on" ? "1" : "0");
                }

                if (text.El("fontRef")?.Attr("idx") is "major" or "minor")
                {
                    textLayer.Elements().Where(e => e.Name.LocalName == "latin").Remove();
                    textLayer.Add(new XElement("latin", new XAttribute("typeface", text.El("fontRef")!.Attr("idx") == "major" ? "+mj-lt" : "+mn-lt")));
                }

                if ((DrawingColors.Child(text, sheet.Colors, null) ?? DrawingColors.Child(text.El("fontRef"), sheet.Colors, null)) is { } color)
                {
                    textLayer.Elements().Where(e => e.Name.LocalName == "solidFill").Remove();
                    textLayer.Add(SolidFill(color));
                }
            }
        }

        var tcPr = tc.El("tcPr");
        var own = FillOf(tcPr, sheet, null, null);
        if (own.Specified)
        {
            fill = own.Fill;
        }

        Outline? Border(int i, string name)
        {
            var layers = new List<XElement?> { borders[i] };
            if (tcPr.El(name) is { } line)
            {
                layers.Add(line);
            }

            return Line(layers, sheet, borderColors[i]);
        }

        var txBody = tc.El("txBody");
        var lists = new List<XElement?>
        {
            _defaultTextStyle,
            new XElement("lstStyle", Enumerable.Range(1, 9).Select(i => new XElement($"lvl{i}pPr", new XElement(textLayer)))),
            txBody.El("lstStyle"),
        };
        var frame = txBody is null ? new TextFrame() : TextFrame(txBody, lists, [txBody.El("bodyPr")], sheet);
        frame.LeftInset = tcPr.Points("marL") is { } left ? Math.Max(left, 0) : 7.2;
        frame.RightInset = tcPr.Points("marR") is { } right ? Math.Max(right, 0) : 7.2;
        frame.TopInset = tcPr.Points("marT") is { } top ? Math.Max(top, 0) : 3.6;
        frame.BottomInset = tcPr.Points("marB") is { } bottom ? Math.Max(bottom, 0) : 3.6;
        frame.Anchor = tcPr.Attr("anchor") switch { "ctr" => TextAnchor.Middle, "b" => TextAnchor.Bottom, _ => TextAnchor.Top };
        frame.Flow = tcPr.Attr("vert") switch { "vert" or "eaVert" => TextFlow.Vertical, "vert270" => TextFlow.Vertical270, _ => TextFlow.Horizontal };
        frame.Wrap = true;
        return new TableCell
        {
            Text = frame,
            Fill = fill,
            Left = Border(0, "lnL"),
            Top = Border(1, "lnT"),
            Right = Border(2, "lnR"),
            Bottom = Border(3, "lnB"),
            ColumnSpan = (int)Math.Clamp(tc.Number("gridSpan") ?? 1, 1, Math.Max(columnCount - c, 1)),
            RowSpan = (int)Math.Clamp(tc.Number("rowSpan") ?? 1, 1, Math.Max(rowCount - r, 1)),
            Merged = tc.Bool("hMerge") == true || tc.Bool("vMerge") == true,
        };
    }
}
