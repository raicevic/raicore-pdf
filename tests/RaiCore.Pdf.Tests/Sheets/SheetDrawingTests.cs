using System.Globalization;
using System.Text;
using System.Text.Json;
using RaiCore.Pdf.DrawingML;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Sheets.Drawings;
using RaiCore.Pdf.Sheets.Xls;
using RaiCore.Pdf.Sheets.Xlsx;
using RaiCore.Pdf.Tests.Fuzzing;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>
/// The drawings of worksheets (pictures, shapes, text boxes, charts, groups): the anchor maths (on laid-out rows and
/// columns, hidden ones, print areas, scale, page breaks and bands of columns), the readers of both formats (hand-made
/// parts and records, and the Apache POI corpus against an independent parse in expected.json) and the PDF they make.
/// </summary>
public class SheetDrawingTests
{
    private const string Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const double Emu = 12700;

    private static readonly Lazy<JsonElement> ExpectedJson = new(() =>
        JsonDocument.Parse(File.ReadAllText(Fixtures.Path("Sheets/Drawings/expected.json"))).RootElement);

    public static TheoryData<string> XlsxCorpus => [.. ExpectedJson.Value.EnumerateObject().Select(p => p.Name).Where(n => n.EndsWith(".xlsx", StringComparison.Ordinal))];

    public static TheoryData<string> XlsCorpus => [.. ExpectedJson.Value.EnumerateObject().Select(p => p.Name).Where(n => n.EndsWith(".xls", StringComparison.Ordinal))];

    // ---- Anchor maths ----

    [Fact]
    public void An_edge_is_at_its_rows_start_plus_its_offset_at_the_scale_and_no_further_than_its_end()
    {
        var axis = new SheetAxis([10, 20, 30]);
        Assert.Equal(3, axis.Count);
        Assert.Equal(30, axis.Start(2));
        Assert.Equal(60, axis.Start(9)); // Past the end: the end.
        Assert.Equal(20, axis.Size(1));
        Assert.Equal(0, axis.Size(3));
        Assert.Equal(0, axis.Size(-1));
        Assert.Equal(10 + 5, axis.Position(new AnchorEdge(1, 5), 1));
        Assert.Equal(10 + 2.5, axis.Position(new AnchorEdge(1, 5), 0.5));
        Assert.Equal(10 + 20, axis.Position(new AnchorEdge(1, 50), 1)); // No further than the row.
        Assert.Equal(30 + 15, axis.Position(new AnchorEdge(2, 0, 0.5), 1)); // A fraction of the row.
        Assert.Equal(30 + 15 + 3, axis.Position(new AnchorEdge(2, 3, 0.5), 1));
        Assert.Equal(60 + 50, axis.Position(new AnchorEdge(3, 50), 1)); // At the end it runs on.
        Assert.Equal(-7, axis.Position(new AnchorEdge(0, -7), 1)); // Before the first printed row.
        Assert.Equal(0, axis.Position(new AnchorEdge(-4, 0), 1));
        Assert.Equal(10, axis.Position(new AnchorEdge(1, double.NaN), 1));
        Assert.Equal(4, new SheetAxis([double.NaN, -5, 4]).Start(3));
    }

    [Fact]
    public void A_distance_is_located_in_its_row_or_column()
    {
        var axis = new SheetAxis([10, 0, 20]);
        Assert.Equal(new AnchorEdge(0, 0), axis.Locate(0));
        Assert.Equal(new AnchorEdge(0, 9), axis.Locate(9));
        Assert.Equal(new AnchorEdge(2, 0), axis.Locate(10)); // The empty row's end: the next row.
        Assert.Equal(new AnchorEdge(2, 15), axis.Locate(25));
        Assert.Equal(new AnchorEdge(3, 70), axis.Locate(100)); // Past the end.
        Assert.Equal(new AnchorEdge(0, 0), axis.Locate(-5));
        Assert.Equal(new AnchorEdge(0, 0), axis.Locate(double.PositiveInfinity));
    }

    [Fact]
    public void Two_cell_anchors_stretch_one_cell_anchors_keep_their_size_and_absolute_anchors_are_located_at_100_percent()
    {
        var columns = new SheetAxis([50, 60, 70]); // As laid out, at 50%.
        var rows = new SheetAxis([10, 12, 30]);
        var nominalColumns = new SheetAxis([100, 120, 140]);
        var nominalRows = new SheetAxis([20, 20, 20]);
        var item = new ShapeItem(Geometry.Rectangle);
        Box Of(SheetDrawing d) => new(DrawingLayout.Box(d, columns, rows, 0.5, nominalColumns, nominalRows));

        var twoCell = new SheetDrawing(AnchorKind.TwoCell, new AnchorEdge(0, 20), new AnchorEdge(1, 4), item)
        {
            Right = new AnchorEdge(2, 10),
            Bottom = new AnchorEdge(2, 0, 0.5),
        };
        Assert.Equal(new Box(10, 12, 110 + 5, 22 + 15), Of(twoCell));

        // The end before the start: nothing wide or high.
        Assert.Equal(new Box(10, 12, 10, 12), Of(twoCell with { Right = new AnchorEdge(0, 0), Bottom = new AnchorEdge(0, 0) }));

        var oneCell = new SheetDrawing(AnchorKind.OneCell, new AnchorEdge(1, 10), new AnchorEdge(2, 4), item) { Width = 100, Height = 40 };
        Assert.Equal(new Box(55, 24, 105, 44), Of(oneCell));

        // 130 points across at 100% is 30 into the second column; 45 down is 5 into the third row.
        var absolute = new SheetDrawing(AnchorKind.Absolute, new AnchorEdge(0, 130), new AnchorEdge(0, 45), item) { Width = 20, Height = 10 };
        Assert.Equal(new Box(50 + 15, 22 + 2.5, 75, 29.5), Of(absolute));
        Assert.Equal(new Box(55, 24, 55, 24), Of(oneCell with { Width = double.NaN, Height = -3 }));
    }

    private readonly record struct Box(double Left, double Top, double Right, double Bottom)
    {
        public Box((double, double, double, double) box)
            : this(box.Item1, box.Item2, box.Item3, box.Item4)
        {
        }
    }

    [Fact]
    public void Rows_take_their_laid_out_heights_and_others_their_nominal_ones()
    {
        // Rows 2 to 4 of the sheet are the table's 0 to 2; row 3 is split over two pages; the header is repeated.
        PlacedRow[] placed =
        [
            new(0, 0, 100, 15, false), new(1, 0, 115, 40, false), new(1, 1, 50, 10, false), new(0, 1, 30, 15, true), new(2, 1, 60, 20, false),
        ];
        var heights = DrawingLayout.RowHeights(placed, [2, 3, 4], 6, row => 100 + row);
        Assert.Equal([100, 101, 15, 50, 20, 105], heights);
    }

    [Fact]
    public void Runs_of_rows_are_joined_on_each_page_and_split_where_a_row_breaks()
    {
        PlacedRow[] placed =
        [
            new(0, 0, 100, 15, false), new(1, 0, 115, 40, false), new(1, 1, 50, 10, false), new(0, 1, 35, 15, true), new(2, 1, 60, 20, false),
            new(3, 2, 80, 5, false), // Not right below the previous row on its page.
            new(5, 2, 85, 5, false), // Right below it on the page, but not on the row axis (row 4 is not in the table).
        ];
        var rows = new SheetAxis(DrawingLayout.RowHeights(placed, [0, 1, 2, 3, 4, 5], 6, _ => 7));
        var segments = DrawingLayout.Segments(placed, [0, 1, 2, 3, 4, 5], rows);
        Assert.Equal(
        [
            new RowSegment(0, 100, 0, 55),
            new RowSegment(1, 50, 55, 85), // The rest of row 1 and row 2, after the repeated header.
            new RowSegment(2, 80, 85, 90),
            new RowSegment(2, 85, 97, 102),
        ], segments);
        Assert.Equal(30, segments[1].Height);
        Assert.Equal([0, 0, 1, 1, 2, 3, 4], new[] { -5.0, 55, 55.5, 85, 86, 91, 103 }.Select(y => DrawingLayout.FirstReaching(segments, y)));
    }

    [Fact]
    public void Anchors_move_from_the_worksheets_rows_to_the_printed_ones()
    {
        int[] printed = [2, 3, 5, 6]; // Row 4 is hidden; rows 0, 1 are outside the print area, and 7 on.
        double Size(int row) => 10 + row;
        Assert.Equal(new AnchorEdge(1, 4), SheetDrawings.Map(new AnchorEdge(3, 4), printed, Size, 15));
        Assert.Equal(new AnchorEdge(2, 0), SheetDrawings.Map(new AnchorEdge(4, 4, 0.5), printed, Size, 15)); // Hidden: the next one's start.
        Assert.Equal(new AnchorEdge(0, 4 - 10 - 11), SheetDrawings.Map(new AnchorEdge(0, 4), printed, Size, 15));
        Assert.Equal(new AnchorEdge(0, 5.5 - 11), SheetDrawings.Map(new AnchorEdge(1, 0, 0.5), printed, Size, 15));
        Assert.Equal(new AnchorEdge(4, 17 + 18 + 1), SheetDrawings.Map(new AnchorEdge(9, 1), printed, Size, 15));
        Assert.Equal(new AnchorEdge(0, -(10_000 * 15.0)), SheetDrawings.Map(new AnchorEdge(0, 0), [10_000], Size, 15)); // Long runs: estimated.
    }

    [Fact]
    public void Drawings_extend_what_a_sheet_without_a_print_area_prints()
    {
        var worksheet = new XlsxWorksheet();
        worksheet.Add(0, 0, new SheetCell("x", CellKind.Text), 100);
        worksheet.Rows[3] = new XlsxRow(100, false);
        worksheet.Columns.Add(new XlsxColumns(1, 1, null, true));
        var item = new ShapeItem(Geometry.Rectangle);
        worksheet.Drawings.Add(new SheetDrawing(AnchorKind.TwoCell, new AnchorEdge(0, 0), new AnchorEdge(0, 0), item)
        {
            Right = new AnchorEdge(4, 0), // Ends at the start of column 4: column 3 is its last.
            Bottom = new AnchorEdge(6, 1),
        });
        Assert.Equal((6, 3), SheetDrawings.UsedRange(worksheet));

        // From 5 points into row 2: 15 points, then row 3 (100), then 15 each; from column 0: 8.71 characters (45.75
        // points), the hidden one, 45.75 more, then 8.5 into column 3.
        worksheet.Drawings.Clear();
        worksheet.Drawings.Add(new SheetDrawing(AnchorKind.OneCell, new AnchorEdge(0, 0), new AnchorEdge(2, 5), item) { Width = 100, Height = 140 });
        Assert.Equal((5, 3), SheetDrawings.UsedRange(worksheet));
        worksheet.Drawings.Add(new SheetDrawing(AnchorKind.Absolute, new AnchorEdge(0, 0), new AnchorEdge(0, 300), item) { Width = 1, Height = 1 });
        Assert.Equal((14, 3), SheetDrawings.UsedRange(worksheet)); // 300 points: rows 0 to 2 (45), row 3 (100), then 155 / 15 more.
        worksheet.Drawings.Add(new SheetDrawing(AnchorKind.Absolute, new AnchorEdge(0, 1e9), new AnchorEdge(0, 1e9), item) { Width = 1e9, Height = 1e9 });
        Assert.Equal((XlsxWorkbook.MaxRows - 1, XlsxWorkbook.MaxColumns - 1), SheetDrawings.UsedRange(worksheet));
    }

    // ---- XLSX: hand-made drawing parts ----

    private static string DrawingPart(string anchors) =>
        $"<xdr:wsDr xmlns:xdr=\"{Xdr}\" xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:mc=\"{Mc}\">{anchors}</xdr:wsDr>";

    private static string TwoCell(string content, int col1 = 1, int row1 = 1, int col2 = 3, int row2 = 5, long colOff = 0, long rowOff = 0, string clientData = "<xdr:clientData/>") =>
        $"<xdr:twoCellAnchor><xdr:from><xdr:col>{col1}</xdr:col><xdr:colOff>{colOff}</xdr:colOff><xdr:row>{row1}</xdr:row><xdr:rowOff>{rowOff}</xdr:rowOff></xdr:from>"
        + $"<xdr:to><xdr:col>{col2}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{row2}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>{content}{clientData}</xdr:twoCellAnchor>";

    private static string OneCell(string content, int col, int row, long cx, long cy) =>
        $"<xdr:oneCellAnchor><xdr:from><xdr:col>{col}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{row}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>"
        + $"<xdr:ext cx=\"{cx}\" cy=\"{cy}\"/>{content}<xdr:clientData/></xdr:oneCellAnchor>";

    private static string Absolute(string content, long x, long y, long cx, long cy) =>
        $"<xdr:absoluteAnchor><xdr:pos x=\"{x}\" y=\"{y}\"/><xdr:ext cx=\"{cx}\" cy=\"{cy}\"/>{content}<xdr:clientData/></xdr:absoluteAnchor>";

    private static string Picture(string extra = "", string spPr = "", string id = "rId1", bool hidden = false) =>
        $"<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"2\" name=\"Picture 1\"{(hidden ? " hidden=\"1\"" : string.Empty)}/><xdr:cNvPicPr/></xdr:nvPicPr>"
        + $"<xdr:blipFill><a:blip r:embed=\"{id}\"/>{extra}<a:stretch/></xdr:blipFill><xdr:spPr>{spPr}</xdr:spPr></xdr:pic>";

    private static string Shape(string spPr = "<a:prstGeom prst=\"rect\"/>", string style = "", string text = "") =>
        $"<xdr:sp><xdr:nvSpPr><xdr:cNvPr id=\"3\" name=\"Shape 1\"/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr>{spPr}</xdr:spPr>{style}{text}</xdr:sp>";

    private const string Chart = $"<xdr:graphicFrame><xdr:nvGraphicFramePr><xdr:cNvPr id=\"4\" name=\"Chart 1\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>"
        + $"<xdr:xfrm/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rId2\"/></a:graphicData></a:graphic></xdr:graphicFrame>";

    private static byte[] Workbook(string anchors, string? sheetData = null) =>
        sheetData is null ? FuzzTargets.DrawingInputs.Xlsx(Encoding.UTF8.GetBytes(DrawingPart(anchors))) : FuzzTargets.DrawingInputs.Xlsx(Encoding.UTF8.GetBytes(DrawingPart(anchors)), sheetData);

    /// <summary>The drawings of the workbook's sheet as read (in its own rows and columns), and what was not drawn.</summary>
    private static (List<SheetDrawing> Drawings, UnsupportedContent Unsupported) ReadSource(byte[] xlsx, string sheet = "xl/worksheets/sheet1.xml", ResourceLimits? limits = null)
    {
        limits ??= ResourceLimits.Default;
        using var package = XlsxPackage.Open(xlsx, limits);
        var workbook = XlsxWorkbook.Read(package, CultureInfo.InvariantCulture);
        var unsupported = new UnsupportedContent();
        return (new XlsxDrawing(package, workbook, limits, unsupported, []).Read(sheet), unsupported);
    }

    [Fact]
    public void Pictures_are_read_with_their_anchor_crop_transform_and_border()
    {
        var (drawings, unsupported) = ReadSource(Workbook(TwoCell(
            Picture("<a:srcRect l=\"10000\" t=\"20000\" r=\"5000\" b=\"-10000\"/>",
                "<a:xfrm rot=\"5400000\" flipH=\"1\"/><a:prstGeom prst=\"ellipse\"/><a:ln w=\"25400\"><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill><a:prstDash val=\"dash\"/></a:ln>"),
            col1: 2, row1: 3, colOff: 12700, rowOff: 25400)));
        var drawing = Assert.Single(drawings);
        Assert.Equal(AnchorKind.TwoCell, drawing.Kind);
        Assert.Equal(new AnchorEdge(2, 1), drawing.Left);
        Assert.Equal(new AnchorEdge(3, 2), drawing.Top);
        Assert.Equal(new AnchorEdge(3, 0), drawing.Right);
        Assert.Equal(new AnchorEdge(5, 0), drawing.Bottom);
        var picture = Assert.IsType<PictureItem>(drawing.Item);
        Assert.Equal((0.1, 0.2, 0.05, -0.1), picture.Crop);
        Assert.Equal(90, picture.Rotation);
        Assert.True(picture.FlipH);
        Assert.False(picture.FlipV);
        Assert.Equal("Picture 1", picture.Name);
        Assert.Equal("ellipse", Assert.IsType<PresetGeometry>(picture.Geometry).Name);
        Assert.Equal((2.0, PdfColor.FromRgb(255, 0, 0)), (picture.Line!.Width, picture.Line.Color));
        Assert.Equal([4, 3], picture.Line.Dash);
        Assert.Equal(0, unsupported[SheetDiagnosticKind.Picture]);

        // A rectangle is the picture's box (no clip of its own).
        Assert.Null(Assert.IsType<PictureItem>(Assert.Single(ReadSource(Workbook(TwoCell(Picture(spPr: "<a:prstGeom prst=\"rect\"/>")))).Drawings).Item).Geometry);
    }

    [Fact]
    public void Pictures_that_cannot_be_drawn_and_objects_without_a_drawing_are_counted()
    {
        var (drawings, unsupported) = ReadSource(Workbook(
            TwoCell(Picture(id: "rId9")) // No such relationship.
            + TwoCell(Picture(id: "rId2")) // The chart part, not a picture.
            + TwoCell("<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"5\" name=\"Linked\"/></xdr:nvPicPr><xdr:blipFill><a:blip r:link=\"rId1\"/></xdr:blipFill></xdr:pic>")
            + TwoCell(Picture(hidden: true)) // Hidden: not counted.
            + TwoCell(Picture(), clientData: "<xdr:clientData fPrintsWithSheet=\"false\"/>") // Does not print: not counted.
            + TwoCell("<xdr:contentPart r:id=\"rId1\"/>")
            + TwoCell("<xdr:graphicFrame><xdr:nvGraphicFramePr><xdr:cNvPr id=\"6\" name=\"Diagram\"/></xdr:nvGraphicFramePr><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/></a:graphic></xdr:graphicFrame>")
            + TwoCell("<xdr:graphicFrame><xdr:nvGraphicFramePr><xdr:cNvPr id=\"7\" name=\"Chart\"/></xdr:nvGraphicFramePr><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/drawing/2014/chartex\"/></a:graphic></xdr:graphicFrame>")
            + TwoCell("<xdr:graphicFrame><xdr:nvGraphicFramePr><xdr:cNvPr id=\"8\" name=\"Chart\"/></xdr:nvGraphicFramePr><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart xmlns:c=\"x\" r:id=\"rId1\"/></a:graphicData></a:graphic></xdr:graphicFrame>")
            + "<xdr:twoCellAnchor><xdr:from/><xdr:clientData/></xdr:twoCellAnchor><xdr:other/>"));
        Assert.Empty(drawings);
        Assert.Equal(3, unsupported[SheetDiagnosticKind.Picture]);
        Assert.Equal(2, unsupported[SheetDiagnosticKind.Shape]);
        Assert.Equal(2, unsupported[SheetDiagnosticKind.Chart]);
    }

    [Fact]
    public void Shapes_are_read_with_their_geometry_fill_outline_and_text()
    {
        var text = "<xdr:txBody><a:bodyPr lIns=\"0\" tIns=\"12700\" anchor=\"ctr\" wrap=\"none\"/><a:p><a:pPr algn=\"ctr\"/>"
            + "<a:r><a:rPr sz=\"1400\" b=\"1\" u=\"sng\"><a:solidFill><a:schemeClr val=\"accent2\"/></a:solidFill><a:latin typeface=\"Arial\"/></a:rPr><a:t>Big</a:t></a:r>"
            + "<a:br/><a:fld type=\"slidenum\"><a:t>7</a:t></a:fld></a:p><a:p><a:endParaRPr sz=\"900\"/></a:p></xdr:txBody>";
        var (drawings, _) = ReadSource(Workbook(
            OneCell(Shape("<a:xfrm flipV=\"1\"/><a:prstGeom prst=\"roundRect\"><a:avLst><a:gd name=\"adj\" fmla=\"val 50000\"/></a:avLst></a:prstGeom>"
                + "<a:solidFill><a:srgbClr val=\"00FF00\"><a:alpha val=\"50000\"/></a:srgbClr></a:solidFill><a:ln><a:noFill/></a:ln>", text: text), 1, 2, 127000, 254000)
            + Absolute(Shape("<a:custGeom><a:pathLst><a:path w=\"10\" h=\"10\"><a:moveTo><a:pt x=\"0\" y=\"0\"/></a:moveTo><a:lnTo><a:pt x=\"10\" y=\"10\"/></a:lnTo></a:path></a:pathLst></a:custGeom>"
                + "<a:gradFill><a:gsLst><a:gs pos=\"0\"><a:srgbClr val=\"000000\"/></a:gs><a:gs pos=\"100000\"><a:srgbClr val=\"FFFFFF\"/></a:gs></a:gsLst></a:gradFill>"
                + "<a:ln w=\"12700\"/>",
                "<xdr:style><a:lnRef idx=\"2\"><a:schemeClr val=\"tx1\"/></a:lnRef><a:fillRef idx=\"1\"><a:srgbClr val=\"0000FF\"/></a:fillRef><a:fontRef idx=\"minor\"><a:srgbClr val=\"333333\"/></a:fontRef></xdr:style>"),
                12700 * 30, 12700 * 40, 12700 * 50, 12700 * 60)
            + TwoCell(Shape(string.Empty, "<xdr:style><a:lnRef idx=\"3\"><a:srgbClr val=\"112233\"/></a:lnRef><a:fillRef idx=\"1\"><a:srgbClr val=\"0000FF\"/></a:fillRef></xdr:style>"))
            + TwoCell(Shape("<a:pattFill><a:fgClr><a:srgbClr val=\"FF0000\"/></a:fgClr></a:pattFill><a:ln w=\"0\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill></a:ln>"))));
        Assert.Equal(4, drawings.Count);

        Assert.Equal(AnchorKind.OneCell, drawings[0].Kind);
        Assert.Equal((10, 20), (drawings[0].Width, drawings[0].Height));
        var rounded = Assert.IsType<ShapeItem>(drawings[0].Item);
        Assert.Equal("roundRect", Assert.IsType<PresetGeometry>(rounded.Geometry).Name);
        Assert.True(rounded.FlipV);
        Assert.Equal(PdfColor.FromRgba(0, 255, 0, 0.5), rounded.Fill);
        Assert.Null(rounded.Line);
        var body = rounded.Text!;
        Assert.Equal((0, 1, 7.2, 3.6), body.Insets);
        Assert.Equal(VerticalAlignment.Middle, body.Anchor);
        Assert.False(body.Wrap);
        Assert.Equal(2, body.Paragraphs.Count);
        Assert.Equal(TextAlignment.Center, body.Paragraphs[0].Alignment);
        Assert.Equal(["Big", "\n", "7"], body.Paragraphs[0].Runs.Select(r => r.Text));
        Assert.Equal(new ShapeRun("Big", 14, true, false, true, PdfColor.FromRgb(0xED, 0x7D, 0x31), "Arial"), body.Paragraphs[0].Runs[0]);
        Assert.Equal(PdfColor.Black, body.Paragraphs[0].Runs[2].Color);
        Assert.Equal(9, Assert.Single(body.Paragraphs[1].Runs).Size);

        Assert.Equal(AnchorKind.Absolute, drawings[1].Kind);
        Assert.Equal((new AnchorEdge(0, 30), new AnchorEdge(0, 40), 50.0, 60.0), (drawings[1].Left, drawings[1].Top, drawings[1].Width, drawings[1].Height));
        var free = Assert.IsType<ShapeItem>(drawings[1].Item);
        Assert.IsType<CustomGeometry>(free.Geometry);
        Assert.Equal(new PdfColor(0.5, 0.5, 0.5), free.Fill); // A gradient, in its average color.
        Assert.Equal((1.0, PdfColor.Black), (free.Line!.Width, free.Line.Color)); // Its own width, the style's color.

        // Without fill and outline of their own: the style's, the line in the theme's thick width.
        var styled = Assert.IsType<ShapeItem>(drawings[2].Item);
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), styled.Fill);
        Assert.Equal(3, styled.Line!.Width);
        Assert.Equal(PdfColor.FromRgb(0x11, 0x22, 0x33), styled.Line.Color);
        Assert.Equal(Geometry.Rectangle, styled.Geometry);

        var pattern = Assert.IsType<ShapeItem>(drawings[3].Item);
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), pattern.Fill);
        Assert.Equal(0, pattern.Line!.Width);
    }

    [Fact]
    public void Groups_charts_and_the_choices_of_alternate_content_are_read()
    {
        var group = "<xdr:grpSp><xdr:nvGrpSpPr><xdr:cNvPr id=\"9\" name=\"Group\"/></xdr:nvGrpSpPr><xdr:grpSpPr><a:xfrm rot=\"60000\">"
            + "<a:off x=\"0\" y=\"0\"/><a:ext cx=\"200\" cy=\"100\"/><a:chOff x=\"1000\" y=\"2000\"/><a:chExt cx=\"400\" cy=\"200\"/></a:xfrm></xdr:grpSpPr>"
            + Shape("<a:xfrm><a:off x=\"1100\" y=\"2050\"/><a:ext cx=\"100\" cy=\"50\"/></a:xfrm><a:prstGeom prst=\"ellipse\"/>")
            + "<mc:AlternateContent><mc:Choice Requires=\"a14\">" + Picture(spPr: "<a:xfrm><a:off x=\"1000\" y=\"2000\"/><a:ext cx=\"400\" cy=\"200\"/></a:xfrm>") + "</mc:Choice>"
            + "<mc:Fallback>" + Shape() + "</mc:Fallback></mc:AlternateContent>"
            + "<xdr:grpSp><xdr:nvGrpSpPr><xdr:cNvPr id=\"10\" name=\"Empty\"/></xdr:nvGrpSpPr></xdr:grpSp>"
            + "</xdr:grpSp>";
        var (drawings, unsupported) = ReadSource(Workbook(
            "<mc:AlternateContent><mc:Choice Requires=\"x\">" + TwoCell(group) + "</mc:Choice><mc:Fallback>" + TwoCell(Shape()) + "</mc:Fallback></mc:AlternateContent>"
            + TwoCell(Chart)));
        Assert.Equal(2, drawings.Count);
        var read = Assert.IsType<GroupItem>(drawings[0].Item);
        Assert.Equal(1, read.Rotation);
        Assert.Equal(new PdfRectangle(1000 / Emu, 2000 / Emu, 400 / Emu, 200 / Emu), read.ChildBounds);
        Assert.Equal(2, read.Children.Count); // The empty group draws nothing.
        Assert.IsType<ShapeItem>(read.Children[0].Item);
        Assert.Equal(new PdfRectangle(1100 / Emu, 2050 / Emu, 100 / Emu, 50 / Emu), read.Children[0].Box);
        Assert.IsType<PictureItem>(read.Children[1].Item);
        var chart = Assert.IsType<ChartItem>(drawings[1].Item);
        Assert.NotEmpty(chart.Chart.PlotArea.Groups);
        Assert.Equal(0, unsupported[SheetDiagnosticKind.Shape] + unsupported[SheetDiagnosticKind.Chart] + unsupported[SheetDiagnosticKind.Picture]);
    }

    [Fact]
    public void Drawings_are_read_within_the_limits()
    {
        var nested = string.Concat(Enumerable.Repeat("<xdr:grpSp>", 40)) + Shape() + string.Concat(Enumerable.Repeat("</xdr:grpSp>", 40));
        var exception = Assert.Throws<ResourceLimitExceededException>(() => ReadSource(Workbook(TwoCell(nested)), limits: new ResourceLimits { MaxNestingDepth = 20 }));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), exception.LimitName);
        exception = Assert.Throws<ResourceLimitExceededException>(() =>
            ReadSource(Workbook(string.Concat(Enumerable.Repeat(TwoCell(Shape()), 30))), limits: new ResourceLimits { MaxElements = 20 }));
        Assert.Equal(nameof(ResourceLimits.MaxElements), exception.LimitName);

        // A damaged drawing part draws nothing; the cells are still converted.
        var damaged = FuzzTargets.DrawingInputs.Xlsx("<xdr:wsDr><broken"u8.ToArray());
        Assert.Empty(ReadSource(damaged).Drawings);
        Assert.NotEmpty(XlsxToPdf.ConvertToBytes(damaged));
    }

    [Fact]
    public void Pictures_over_the_pixel_limit_are_skipped_and_reported()
    {
        var png = File.ReadAllBytes(Fixtures.Path("PngSuite/basn2c08.png")); // 32 by 32 pixels.
        var xlsx = Repack(Workbook(TwoCell(Picture())), "xl/media/image1.png", png);
        var diagnostics = new List<SheetDiagnostic>();
        var pdf = XlsxToPdf.ConvertToBytes(xlsx, new XlsxConverterOptions
        {
            Sheet = new SheetOptions { Limits = new ResourceLimits { MaxImagePixels = 1000 }, Diagnostics = diagnostics.Add },
        });
        Assert.Empty(Images(pdf, 0));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal((SheetDiagnosticKind.Picture, "1 picture is left out."), (diagnostic.Kind, diagnostic.Message));

        // Within the limit it is drawn.
        Assert.Single(Images(XlsxToPdf.ConvertToBytes(xlsx), 0));
    }

    // ---- XLSX: where drawings go in the PDF ----

    /// <summary>The images on a page with their bounds, top-down (y from the page's top).</summary>
    private static List<(double Left, double Top, double Right, double Bottom)> Images(byte[] pdf, int page)
    {
        var reader = PdfReader.Open(pdf);
        var height = reader.Pages[page].Height;
        return [.. new ImageExtractor(reader).ExtractPage(page).Images
            .Select(i => (i.Bounds.X, height - i.Bounds.Y - i.Bounds.Height, i.Bounds.X + i.Bounds.Width, height - i.Bounds.Y))];
    }

    private static int PageCount(byte[] pdf) => PdfReader.Open(pdf).Pages.Count;

    private static void Near(double expected, double actual) => Assert.Equal(expected, actual, 0.01);

    [Fact]
    public void Two_cell_anchors_meet_where_their_cells_meet_and_offsets_and_sizes_are_in_points()
    {
        var xlsx = Workbook(
            TwoCell(Picture(), 1, 1, 3, 4)
            + TwoCell(Picture(), 3, 4, 5, 6)
            + TwoCell(Picture(), 1, 1, 3, 4, colOff: 12700 * 5, rowOff: 12700 * 3)
            + OneCell(Picture(), 3, 4, 12700 * 40, 12700 * 20)
            + Absolute(Picture(), 0, 0, 12700 * 30, 12700 * 10),
            "<row r=\"1\"><c r=\"A1\"><v>1</v></c></row><row r=\"3\" ht=\"30\" customHeight=\"1\"/><row r=\"9\"><c r=\"H9\"><v>1</v></c></row>");
        var images = Images(XlsxToPdf.ConvertToBytes(xlsx), 0);
        Assert.Equal(5, images.Count);
        var (first, second, offset, oneCell, absolute) = (images[0], images[1], images[2], images[3], images[4]);
        Near(first.Right, second.Left);
        Near(first.Bottom, second.Top);
        Near(first.Left + 5, offset.Left);
        Near(first.Top + 3, offset.Top);
        Near(first.Right, offset.Right); // The end does not move.
        Near(second.Left, oneCell.Left);
        Near(second.Top, oneCell.Top);
        Near(40, oneCell.Right - oneCell.Left);
        Near(20, oneCell.Bottom - oneCell.Top);
        var margin = new SheetOptions().Page.Margins.Left.Points;
        Near(margin, absolute.Left);
        Near(margin, absolute.Top);
        Near(30, absolute.Right - absolute.Left);

        // Row 3 is 30 points high: the first picture spans rows 2 to 4.
        Assert.True(first.Bottom - first.Top > 30 + 15);
    }

    [Fact]
    public void Hidden_rows_and_columns_take_no_room_and_the_print_scale_applies_to_sizes()
    {
        string Sheet(bool hidden) => $"<row r=\"1\"><c r=\"A1\"><v>1</v></c></row><row r=\"3\" hidden=\"{(hidden ? 1 : 0)}\"/><row r=\"9\"><c r=\"H9\"><v>1</v></c></row>";
        var anchors = TwoCell(Picture(), 1, 1, 3, 5) + TwoCell(Picture(), 1, 2, 3, 3, rowOff: 12700 * 4) + OneCell(Picture(), 3, 4, 12700 * 40, 12700 * 20);
        var shown = Images(XlsxToPdf.ConvertToBytes(Workbook(anchors, Sheet(false))), 0);
        var hidden = Images(XlsxToPdf.ConvertToBytes(Workbook(anchors, Sheet(true))), 0);
        var row = shown[0].Bottom - shown[0].Top - (hidden[0].Bottom - hidden[0].Top);
        Assert.True(row > 10, $"{row}");

        // A picture in the hidden row only is as high as nothing, at the start of the next row.
        Assert.Equal(2, hidden.Count(i => i.Bottom - i.Top > 1));
        Near(((shown[0].Bottom - shown[0].Top) / 4) - 4, shown[1].Bottom - shown[1].Top); // Row 3 of 4 equal rows, from 4 points in.

        // At half the scale, a size of its own is half as big.
        var scaled = FuzzTargets.DrawingInputs.Xlsx(Encoding.UTF8.GetBytes(DrawingPart(anchors)), Sheet(false));
        scaled = Repack(scaled, "xl/worksheets/sheet1.xml", Encoding.UTF8.GetBytes(
            $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"{R}\"><sheetData>{Sheet(false)}</sheetData><pageSetup scale=\"50\"/><drawing r:id=\"rId1\"/></worksheet>"));
        var half = Images(XlsxToPdf.ConvertToBytes(scaled), 0);
        Near(20, half[2].Right - half[2].Left);
        Near(10, half[2].Bottom - half[2].Top);
    }

    [Fact]
    public void A_drawing_across_a_page_break_is_split_over_the_pages()
    {
        // 80 rows of 15 points: the picture over rows 40 to 70 crosses the first page's end.
        var rows = string.Concat(Enumerable.Range(1, 80).Select(r => $"<row r=\"{r}\"><c r=\"A{r}\"><v>{r}</v></c></row>"));
        var pdf = XlsxToPdf.ConvertToBytes(Workbook(TwoCell(Picture(), 1, 40, 3, 70), rows));
        Assert.True(PageCount(pdf) >= 2);
        var first = Assert.Single(Images(pdf, 0));
        var second = Assert.Single(Images(pdf, 1));

        // The same picture, placed so that its part on each page is the part over that page's rows.
        Near(first.Right - first.Left, second.Right - second.Left);
        Near(first.Bottom - first.Top, second.Bottom - second.Top);
        Assert.True(second.Top < new SheetOptions().Page.Margins.Top.Points);
    }

    [Fact]
    public void A_drawing_across_bands_of_columns_is_split_over_them()
    {
        var columns = "<cols><col min=\"1\" max=\"30\" width=\"20\" customWidth=\"1\"/></cols>";
        var xlsx = Workbook(TwoCell(Picture(), 3, 2, 8, 6), "<row r=\"1\"><c r=\"A1\"><v>1</v></c></row>");
        xlsx = Repack(xlsx, "xl/worksheets/sheet1.xml", Encoding.UTF8.GetBytes(
            $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"{R}\">{columns}<sheetData><row r=\"1\"><c r=\"L1\"><v>1</v></c></row></sheetData><drawing r:id=\"rId1\"/></worksheet>"));
        var pdf = XlsxToPdf.ConvertToBytes(xlsx);
        var pages = Enumerable.Range(0, PageCount(pdf)).Where(p => Images(pdf, p).Count > 0).ToList();
        Assert.Equal(2, pages.Count); // Twelve columns of 20 characters make bands of a few columns: the picture is on two.
        Assert.Equal(pages[0] + 1, pages[1]);
        var left = Assert.Single(Images(pdf, pages[0]));
        var right = Assert.Single(Images(pdf, pages[1]));
        Near(left.Right - left.Left, right.Right - right.Left);
        Near(left.Top, right.Top);
        Assert.True(right.Left < new SheetOptions().Page.Margins.Left.Points, "The second band shows the picture's right part.");
    }

    [Fact]
    public void Right_to_left_sheets_mirror_their_drawings()
    {
        string Sheet(bool rightToLeft) => $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"{R}\">"
            + $"<sheetViews><sheetView rightToLeft=\"{(rightToLeft ? 1 : 0)}\"/></sheetViews><cols><col min=\"1\" max=\"4\" width=\"12\" customWidth=\"1\"/></cols>"
            + "<sheetData><row r=\"1\"><c r=\"D1\"><v>1</v></c></row></sheetData><drawing r:id=\"rId1\"/></worksheet>";
        var xlsx = Workbook(TwoCell(Picture(), 0, 1, 1, 3) + TwoCell(Picture(), 1, 2, 3, 4, colOff: 12700 * 6));
        var ltr = Images(XlsxToPdf.ConvertToBytes(Repack(xlsx, "xl/worksheets/sheet1.xml", Encoding.UTF8.GetBytes(Sheet(false)))), 0);
        var rtl = Images(XlsxToPdf.ConvertToBytes(Repack(xlsx, "xl/worksheets/sheet1.xml", Encoding.UTF8.GetBytes(Sheet(true)))), 0);

        // The first column is on the right: the picture in it ends at the table's right edge; the other is as far
        // from that edge as it is from the left one left to right, and as wide.
        Near(ltr[0].Right - ltr[0].Left, rtl[0].Right - rtl[0].Left);
        Near(rtl[0].Right - (ltr[1].Left - ltr[0].Left), rtl[1].Right);
        Near(ltr[1].Right - ltr[1].Left, rtl[1].Right - rtl[1].Left);
        Near(ltr[1].Top, rtl[1].Top);
        Assert.True(rtl[0].Left > ltr[0].Left);
    }

    [Fact]
    public void Shapes_text_charts_and_groups_are_drawn_over_the_cells()
    {
        var text = "<xdr:txBody><a:bodyPr/><a:p><a:r><a:t>Hello box</a:t></a:r></a:p></xdr:txBody>";
        var pdf = XlsxToPdf.ConvertToBytes(Workbook(
            TwoCell(Shape("<a:prstGeom prst=\"ellipse\"/><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill>", text: text), 1, 1, 5, 6)
            + TwoCell(Chart, 1, 7, 8, 20)
            + TwoCell("<xdr:grpSp><xdr:nvGrpSpPr><xdr:cNvPr id=\"9\" name=\"Group\"/></xdr:nvGrpSpPr><xdr:grpSpPr/>" // Without coordinates: its children's.
                + Picture(spPr: "<a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"100\" cy=\"100\"/></a:xfrm>") + "</xdr:grpSp>", 6, 1, 8, 6)));
        var extracted = new TextExtractor(PdfReader.Open(pdf)).ExtractPages().First().Text;
        Assert.Contains("Hello box", extracted, StringComparison.Ordinal);
        Assert.Single(Images(pdf, 0)); // The group's picture.
        var content = Encoding.Latin1.GetString(PdfReader.Open(pdf).Pages[0].GetContent());
        Assert.Contains("1 0 0 rg", content, StringComparison.Ordinal); // The ellipse's fill.
    }

    // ---- XLS: hand-made drawings ----

    private static byte[] Header(int version, int instance, int type, int length) => FuzzTargets.DrawingInputs.Header(version, instance, type, length);

    private static byte[] Container(int type, params byte[][] children)
    {
        var body = children.SelectMany(c => c).ToArray();
        return [.. Header(0xF, 0, type, body.Length), .. body];
    }

    private static byte[] Atom(int type, int instance, byte[] body) => [.. Header(0, instance, type, body.Length), .. body];

    private static byte[] U16(int value) => [(byte)value, (byte)(value >> 8)];

    private static byte[] I32(int value) => BitConverter.GetBytes(value);

    /// <summary>A shape: FSP (type, flags), its properties, a client anchor and client data (and text box).</summary>
    private static byte[] XlsShape(int type, (int Id, int Value)[] properties, int[] anchor, bool text = false, int flags = 0xA00, byte[]? child = null) => Container(0xF004,
        Atom(0xF00A, type, [.. I32(1024), .. I32(flags)]),
        Atom(0xF00B, properties.Length, [.. properties.SelectMany(p => U16(p.Id).Concat(I32(p.Value)))]),
        child ?? Atom(0xF010, 0, [.. U16(0), .. anchor.SelectMany(U16)]),
        Atom(0xF011, 0, []),
        text ? Atom(0xF00D, 0, []) : []);

    private static byte[] XlsDrawingData(params byte[][] shapes) => Container(0xF002,
        Atom(0xF008, 1, new byte[8]),
        Container(0xF003, [Container(0xF004, Atom(0xF009, 0, new byte[16]), Atom(0xF00A, 0, [.. I32(1024), .. I32(5)])), .. shapes]));

    private static List<SheetDrawing> ReadXls(byte[] xls, UnsupportedContent? unsupported = null, ResourceLimits? limits = null)
    {
        limits ??= ResourceLimits.Default;
        var workbook = XlsConverter.Open(xls, limits, CultureInfo.InvariantCulture, null).Workbook;
        var sheet = XlsWorksheet.Read(workbook, workbook.SheetOffsets[0], limits.MaxTableCells, null, limits);
        foreach (SheetDiagnosticKind kind in Enum.GetValues<SheetDiagnosticKind>())
        {
            unsupported?.Add(kind, sheet.Unsupported[kind]);
        }

        return sheet.Drawings;
    }

    [Fact]
    public void Xls_pictures_and_shapes_are_read_with_their_anchors_in_fractions_of_cells()
    {
        var data = XlsDrawingData(
            XlsShape(75, [(0x0104, 1), (0x0100, 0x4000), (0x0102, 0x8000), (0x01FF, 0x00080008), (0x01C0, 0x0000FF), (0x01CB, 25400)], [1, 512, 2, 128, 3, 0, 5, 256]),
            XlsShape(75, [(0x0104, 2), (0x0004, 90 << 16)], [0, 0, 0, 0, 1, 0, 1, 0], flags: 0xA40),
            XlsShape(3, [(0x0181, 0x01000003), (0x01C0, 0x08000040), (0x01BF, 0x00100010)], [4, 0, 4, 0, 6, 1024, 8, 0]),
            XlsShape(202, [(0x01BF, 0x00100000), (0x01FF, 0x00080000)], [0, 0, 6, 0, 2, 0, 8, 0], text: true));
        var drawings = ReadXls(FuzzTargets.DrawingInputs.Xls(data, [0x08, 0x08, 0x03, 0x06]));
        Assert.Equal(4, drawings.Count);
        Assert.All(drawings, d => Assert.Equal(AnchorKind.TwoCell, d.Kind));
        Assert.Equal((new AnchorEdge(1, 0, 0.5), new AnchorEdge(2, 0, 0.5), new AnchorEdge(3, 0, 0), new AnchorEdge(5, 0, 1)),
            (drawings[0].Left, drawings[0].Top, drawings[0].Right, drawings[0].Bottom));
        var png = Assert.IsType<PictureItem>(drawings[0].Item);
        Assert.Equal((0.5, 0.25, 0, 0), png.Crop);
        Assert.Equal((2.0, PdfColor.FromRgb(255, 0, 0)), (png.Line!.Width, png.Line.Color));
        Assert.Equal(1, png.Image.PixelWidth);
        var dib = Assert.IsType<PictureItem>(drawings[1].Item);
        Assert.Equal((2, 2), (dib.Image.PixelWidth, dib.Image.PixelHeight));
        Assert.Equal(90, dib.Rotation);
        Assert.True(dib.FlipH);
        Assert.Null(dib.Line);

        var oval = Assert.IsType<ShapeItem>(drawings[2].Item);
        Assert.Equal("ellipse", Assert.IsType<PresetGeometry>(oval.Geometry).Name);
        Assert.Equal(PdfColor.FromRgb(0, 255, 0), oval.Fill); // Palette index 3 of the default palette.
        Assert.Equal(PdfColor.Black, oval.Line!.Color); // 64: the system's foreground.

        var box = Assert.IsType<ShapeItem>(drawings[3].Item);
        Assert.Null(box.Fill);
        Assert.Null(box.Line);
        var paragraph = Assert.Single(box.Text!.Paragraphs);
        Assert.Equal("Fuzz", Assert.Single(paragraph.Runs).Text);
        Assert.Equal(TextAlignment.Center, paragraph.Alignment);
        Assert.Equal(VerticalAlignment.Middle, box.Text.Anchor);
    }

    [Fact]
    public void Xls_groups_comments_and_shapes_that_cannot_be_drawn()
    {
        var group = Container(0xF003,
            Container(0xF004, Atom(0xF009, 0, [.. I32(0), .. I32(0), .. I32(100), .. I32(50)]), Atom(0xF00A, 0, [.. I32(1030), .. I32(0x201)]),
                Atom(0xF010, 0, [.. U16(0), .. new[] { 1, 0, 1, 0, 4, 0, 4, 0 }.SelectMany(U16)]), Atom(0xF011, 0, [])),
            XlsShape(1, [], [], child: Atom(0xF00F, 0, [.. I32(0), .. I32(0), .. I32(50), .. I32(50)])),
            XlsShape(3, [], [], child: Atom(0xF00F, 0, [.. I32(50), .. I32(0), .. I32(100), .. I32(50)])));
        var data = XlsDrawingData(
            XlsShape(202, [], [0, 0, 0, 0, 2, 0, 2, 0], text: true), // A comment's shape.
            group,
            XlsShape(1, [(0x0145, 0)], [0, 0, 0, 0, 1, 0, 1, 0]), // A free form (a rectangle with vertices of its own).
            XlsShape(75, [(0x0104, 7)], [0, 0, 0, 0, 1, 0, 1, 0]), // No such picture.
            XlsShape(1, [], [0, 0, 0, 0, 1, 0, 1, 0]), // A check box.
            XlsShape(1, [(0x03BF, 0x00020002)], [0, 0, 0, 0, 1, 0, 1, 0])); // Hidden.
        var unsupported = new UnsupportedContent();
        var drawings = ReadXls(FuzzTargets.DrawingInputs.Xls(data, [0x19, 0x00, 0x02, 0x03, 0x1E, 0x08, 0x0B, 0x02, 0x05]), unsupported);
        var read = Assert.IsType<GroupItem>(Assert.Single(drawings).Item);
        Assert.Equal(new PdfRectangle(0, 0, 100, 50), read.ChildBounds);
        Assert.Equal([new PdfRectangle(0, 0, 50, 50), new PdfRectangle(50, 0, 50, 50)], read.Children.Select(c => c.Box));
        Assert.Equal(2, unsupported[SheetDiagnosticKind.Shape]); // The free form and the check box.
        Assert.Equal(1, unsupported[SheetDiagnosticKind.Picture]);
        Assert.Equal(1, unsupported[SheetDiagnosticKind.Chart]); // An OBJ without a shape, counted by its kind.
    }

    [Fact]
    public void Xls_objects_before_a_shapes_client_data_are_not_its_object()
    {
        // Two pictures' OBJ records come before the drawing data: they are not the rectangle's, which is drawn.
        var unsupported = new UnsupportedContent();
        var drawings = ReadXls(FuzzTargets.DrawingInputs.Xls(XlsDrawingData(XlsShape(1, [], [0, 0, 0, 0, 2, 0, 2, 0])), [0x02], [0x08, 0x08]), unsupported);
        Assert.IsType<ShapeItem>(Assert.Single(drawings).Item);
        Assert.Equal(2, unsupported[SheetDiagnosticKind.Picture]);
        Assert.Equal(0, unsupported[SheetDiagnosticKind.Shape]);
    }

    [Fact]
    public void A_cropped_picture_is_scaled_so_that_what_is_left_fills_its_box()
    {
        var plain = Assert.Single(Images(XlsxToPdf.ConvertToBytes(Workbook(TwoCell(Picture()))), 0));
        var cropped = Assert.Single(Images(XlsxToPdf.ConvertToBytes(Workbook(TwoCell(Picture("<a:srcRect t=\"50000\" r=\"25000\"/>")))), 0));
        Near(plain.Left, cropped.Left);
        Near(plain.Bottom, cropped.Bottom);
        Near(2 * (plain.Bottom - plain.Top), cropped.Bottom - cropped.Top); // Its top half is above the box (and clipped).
        Near((plain.Right - plain.Left) * 4 / 3, cropped.Right - cropped.Left);
    }

    [Fact]
    public void Xls_drawings_are_read_within_the_limits_and_damaged_ones_are_cut()
    {
        byte[] deep = XlsShape(1, [], [0, 0, 0, 0, 1, 0, 1, 0]);
        for (var i = 0; i < 30; i++)
        {
            deep = Container(0xF003, deep);
        }

        var exception = Assert.Throws<ResourceLimitExceededException>(() =>
            ReadXls(FuzzTargets.DrawingInputs.Xls(Container(0xF002, deep), [0x02]), limits: new ResourceLimits { MaxNestingDepth = 10 }));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), exception.LimitName);

        // Records that claim more than there is are cut; garbage draws nothing.
        var data = XlsDrawingData(XlsShape(1, [(0x0181, 0xFF)], [0, 0, 0, 0, 1, 0, 1, 0]));
        foreach (var cut in new[] { 3, 10, 30, 60, data.Length - 5 })
        {
            ReadXls(FuzzTargets.DrawingInputs.Xls(data[..cut], [0x02]));
        }

        Assert.Empty(ReadXls(FuzzTargets.DrawingInputs.Xls([.. Enumerable.Repeat((byte)0xFF, 64)], [0x02])));
    }

    [Fact]
    public void A_dib_gets_the_file_header_that_says_where_its_pixels_start()
    {
        // Width, height, planes, bits, compression, image size, resolution, colors used, colors important; colors; pixels.
        byte[] Dib(int bits, int compression, int used, int more) =>
            [.. I32(40), .. I32(2), .. I32(2), .. U16(1), .. U16(bits), .. I32(compression), .. new byte[12], .. I32(used), .. I32(0), .. new byte[more]];
        var file = XlsDrawing.WithFileHeader(Dib(8, 0, 3, 12 + 8));
        Assert.Equal("BM"u8.ToArray(), file[..2]);
        Assert.Equal(file.Length, BitConverter.ToInt32(file, 2));
        Assert.Equal(14 + 40 + (4 * 3), BitConverter.ToInt32(file, 10)); // Three colors used.
        Assert.Equal(14 + 40 + (4 * 256), BitConverter.ToInt32(XlsDrawing.WithFileHeader(Dib(8, 0, 0, 1024 + 8)), 10));
        Assert.Equal(14 + 40 + 12, BitConverter.ToInt32(XlsDrawing.WithFileHeader(Dib(16, 3, 0, 12 + 8)), 10)); // Bit fields: three masks.
        Assert.Equal(14 + 40, BitConverter.ToInt32(XlsDrawing.WithFileHeader(Dib(24, 0, 0, 16)), 10));
    }

    // ---- The Apache POI corpus, against expected.json ----

    [Theory]
    [MemberData(nameof(XlsxCorpus))]
    public void Xlsx_corpus_anchors_match_the_independent_parse(string name)
    {
        var data = File.ReadAllBytes(Path.Combine(Fixtures.Path("Sheets/Drawings"), name));
        foreach (var sheet in ExpectedJson.Value.GetProperty(name).GetProperty("sheets").EnumerateObject())
        {
            var expected = sheet.Value.EnumerateArray().Where(a => a.GetProperty("drawn").GetBoolean()).ToList();
            var (drawings, unsupported) = ReadSource(data, sheet.Name);
            Assert.Equal(expected.Count, drawings.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                var anchor = expected[i];
                var drawing = drawings[i];
                Assert.Equal(anchor.GetProperty("anchor").GetString(), drawing.Kind switch
                {
                    AnchorKind.TwoCell => "twoCellAnchor",
                    AnchorKind.OneCell => "oneCellAnchor",
                    _ => "absoluteAnchor",
                });
                Assert.Equal(anchor.GetProperty("object").GetString(), drawing.Item switch
                {
                    PictureItem => "pic",
                    ChartItem => "graphicFrame",
                    GroupItem => "grpSp",
                    _ => drawing.Item is ShapeItem shape && shape.Geometry.IsLine && anchor.GetProperty("object").GetString() == "cxnSp" ? "cxnSp" : "sp",
                });
                if (drawing.Kind == AnchorKind.Absolute)
                {
                    var pos = anchor.GetProperty("pos");
                    Assert.Equal((pos[0].GetDouble() / Emu, pos[1].GetDouble() / Emu), (drawing.Left.Points, drawing.Top.Points));
                }
                else
                {
                    var from = anchor.GetProperty("from");
                    Assert.Equal(new AnchorEdge(from[0].GetInt32(), from[1].GetDouble() / Emu), drawing.Left);
                    Assert.Equal(new AnchorEdge(from[2].GetInt32(), from[3].GetDouble() / Emu), drawing.Top);
                }

                if (drawing.Kind == AnchorKind.TwoCell)
                {
                    var to = anchor.GetProperty("to");
                    Assert.Equal(new AnchorEdge(to[0].GetInt32(), to[1].GetDouble() / Emu), drawing.Right);
                    Assert.Equal(new AnchorEdge(to[2].GetInt32(), to[3].GetDouble() / Emu), drawing.Bottom);
                }
                else
                {
                    var ext = anchor.GetProperty("ext");
                    Assert.Equal((ext[0].GetDouble() / Emu, ext[1].GetDouble() / Emu), (drawing.Width, drawing.Height));
                }
            }

            Assert.Equal(0, unsupported[SheetDiagnosticKind.Picture] + unsupported[SheetDiagnosticKind.Chart]);
        }

        // Each picture openpyxl finds is an image in the PDF (pictures in formats openpyxl cannot open are not counted).
        var openpyxl = ExpectedJson.Value.GetProperty(name).GetProperty("openpyxl");
        var pdf = XlsxToPdf.ConvertToBytes(data, new XlsxConverterOptions { SheetIndexes = null });
        var images = Enumerable.Range(0, PageCount(pdf)).Sum(p => Images(pdf, p).Count);
        Assert.True(images >= openpyxl.GetProperty("images").GetInt32(), $"{images} images");
    }

    [Theory]
    [MemberData(nameof(XlsCorpus))]
    public void Xls_corpus_anchors_match_the_independent_parse(string name)
    {
        var data = File.ReadAllBytes(Path.Combine(Fixtures.Path("Sheets/Drawings"), name));
        var limits = ResourceLimits.Default;
        var workbook = XlsConverter.Open(data, limits, CultureInfo.InvariantCulture, null).Workbook;
        foreach (var sheet in ExpectedJson.Value.GetProperty(name).GetProperty("sheets").EnumerateObject())
        {
            var index = workbook.Sheets.FindIndex(s => s.Name == sheet.Name);
            var drawings = XlsWorksheet.Read(workbook, workbook.SheetOffsets[index], limits.MaxTableCells, null, limits).Drawings;
            var expected = sheet.Value.EnumerateArray().Where(a => a.GetProperty("drawn").GetBoolean()).ToList();
            Assert.Equal(expected.Count, drawings.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                var a = expected[i].GetProperty("anchor").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                var d = drawings[i];
                Assert.Equal(new AnchorEdge(a[0], 0, Math.Min(a[1], 1024) / 1024.0), d.Left);
                Assert.Equal(new AnchorEdge(a[2], 0, Math.Min(a[3], 256) / 256.0), d.Top);
                Assert.Equal(new AnchorEdge(a[4], 0, Math.Min(a[5], 1024) / 1024.0), d.Right);
                Assert.Equal(new AnchorEdge(a[6], 0, Math.Min(a[7], 256) / 256.0), d.Bottom);
                Assert.Equal(expected[i].GetProperty("object").GetInt32() == 8, d.Item is PictureItem);
            }
        }
    }

    [Fact]
    public void Corpus_pictures_are_drawn_where_their_cells_are()
    {
        // The picture and the shape have the same anchor: they cover the same box.
        var pdf = XlsxToPdf.ConvertToBytes(File.ReadAllBytes(Fixtures.Path("Sheets/Drawings/picture-and-shape-same-size.xlsx")));
        var picture = Assert.Single(Images(pdf, 0));
        Assert.True(picture.Right - picture.Left > 100 && picture.Bottom - picture.Top > 40);

        // XLS: the four pictures (a JPEG, a PNG, a WMF and an EMF), in their cells; the bitmaps are images.
        var xls = XlsToPdf.ConvertToBytes(File.ReadAllBytes(Fixtures.Path("Sheets/Drawings/SimpleWithImages.xls")));
        Assert.Equal(4, XlsConverter.Read(File.ReadAllBytes(Fixtures.Path("Sheets/Drawings/SimpleWithImages.xls")))[0].Formatting!.Drawings.Count);
        Assert.NotEmpty(Images(xls, 0));
    }

    /// <summary>A package with one part replaced (or added).</summary>
    private static byte[] Repack(byte[] package, string path, byte[] content)
    {
        using var input = new System.IO.Compression.ZipArchive(new MemoryStream(package));
        using var output = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries.Where(e => e.FullName != path))
            {
                using var source = entry.Open();
                using var target = zip.CreateEntry(entry.FullName).Open();
                source.CopyTo(target);
            }

            using var added = zip.CreateEntry(path).Open();
            added.Write(content);
        }

        return output.ToArray();
    }
}
