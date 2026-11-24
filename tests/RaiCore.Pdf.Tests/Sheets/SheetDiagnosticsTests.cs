using System.Globalization;
using System.Text;
using System.Text.Json;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Sheets.Xls;
using static RaiCore.Pdf.Tests.Sheets.BiffBuilder;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>
/// The diagnostics of the Sheets converters: what workbooks have that is left out or simplified, one diagnostic per
/// kind and sheet with its count, in .xlsx (generated fixtures, hand-made packages) and .xls (the Apache POI corpus,
/// whose content is counted independently in expected.json, and hand-made streams), and layout problems.
/// </summary>
public class SheetDiagnosticsTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Compatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const string Drawing = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private const string DrawingMain = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static readonly Lazy<JsonElement> ExpectedJson = new(() =>
        JsonDocument.Parse(File.ReadAllText(Fixtures.Path("Sheets/Xls/expected.json"))).RootElement);

    public static TheoryData<string> XlsCorpus => [.. ExpectedJson.Value.EnumerateObject()
        .Where(p => p.Value.TryGetProperty("content", out _) && p.Value.GetProperty("biff").GetInt32() >= 50).Select(p => p.Name)];

    /// <summary>Converts with diagnostics, and returns them in the order they were reported.</summary>
    private static List<SheetDiagnostic> Diagnose(Func<SheetOptions, byte[]> convert)
    {
        var diagnostics = new List<SheetDiagnostic>();
        convert(new SheetOptions { Diagnostics = diagnostics.Add });
        return diagnostics;
    }

    private static List<SheetDiagnostic> Xlsx(byte[] xlsx, IReadOnlyList<string>? sheetNames = null) =>
        Diagnose(sheet => XlsxToPdf.ConvertToBytes(xlsx, new XlsxConverterOptions { Sheet = sheet, SheetNames = sheetNames }));

    private static List<SheetDiagnostic> Xls(byte[] xls, IReadOnlyList<int>? sheetIndexes = null) =>
        Diagnose(sheet => XlsToPdf.ConvertToBytes(xls, new XlsxConverterOptions { Sheet = sheet, SheetIndexes = sheetIndexes }));

    /// <summary>The count a message starts with (1 for the chart sheet's, which has none).</summary>
    private static int Count(SheetDiagnostic diagnostic) =>
        int.TryParse(diagnostic.Message.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 1;

    /// <summary>The diagnostics as (sheet, kind, count), layout problems left out.</summary>
    private static (string?, SheetDiagnosticKind, int)[] Content(IEnumerable<SheetDiagnostic> diagnostics) =>
        [.. diagnostics.Where(d => d.Kind != SheetDiagnosticKind.Layout).Select(d => (d.SheetName, d.Kind, Count(d)))];

    /// <summary>A workbook of one worksheet with the given content after its cells, and other parts it refers to.</summary>
    internal static byte[] Workbook(string sheetExtra, string sheetRelationships = "", string sheetData = "<row r=\"1\"><c r=\"A1\"><v>1</v></c></row>",
        string workbookSheets = "", string workbookRelationships = "", params (string Name, string Content)[] parts)
    {
        var all = new List<(string, string)>
        {
            ("_rels/.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Relationships}\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/>{workbookSheets}</sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/worksheet\" Target=\"worksheets/sheet1.xml\"/>{workbookRelationships}</Relationships>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Main}\" xmlns:r=\"{Relationships}\" xmlns:mc=\"{Compatibility}\"><sheetData>{sheetData}</sheetData>{sheetExtra}</worksheet>"),
            ("xl/worksheets/_rels/sheet1.xml.rels", $"<Relationships xmlns=\"{PackageRelationships}\">{sheetRelationships}</Relationships>"),
        };
        all.AddRange(parts);
        return XlsxToPdfTests.Package([.. all]);
    }

    internal static string Relationship(string id, string type, string target) =>
        $"<Relationship Id=\"{id}\" Type=\"{Relationships}/{type}\" Target=\"{target}\"/>";

    /// <summary>A drawing part with the given anchors.</summary>
    private static (string, string) DrawingPart(string anchors) =>
        ("xl/drawings/drawing1.xml", $"<xdr:wsDr xmlns:xdr=\"{Drawing}\" xmlns:a=\"{DrawingMain}\" xmlns:mc=\"{Compatibility}\">{anchors}</xdr:wsDr>");

    private static string Anchor(string content, string clientData = "<xdr:clientData/>") =>
        $"<xdr:twoCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>"
        + $"<xdr:to><xdr:col>3</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>5</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>{content}{clientData}</xdr:twoCellAnchor>";

    private static string Picture(bool hidden = false) =>
        $"<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"2\" name=\"Picture\"{(hidden ? " hidden=\"1\"" : string.Empty)}/><xdr:cNvPicPr/></xdr:nvPicPr><xdr:blipFill/><xdr:spPr/></xdr:pic>";

    private static string Shape(bool hidden = false) =>
        $"<xdr:sp><xdr:nvSpPr><xdr:cNvPr id=\"3\" name=\"Shape\"{(hidden ? " hidden=\"1\"" : string.Empty)}/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr/></xdr:sp>";

    private static string GraphicFrame(string uri) =>
        $"<xdr:graphicFrame><xdr:nvGraphicFramePr><xdr:cNvPr id=\"4\" name=\"Frame\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr><xdr:xfrm/>"
        + $"<a:graphic><a:graphicData uri=\"{uri}\"/></a:graphic></xdr:graphicFrame>";

    // ---- XLSX: the generated fixture ----

    [Fact]
    public void Xlsx_fixture_reports_each_kind_once_per_sheet_with_its_count()
    {
        var diagnostics = Xlsx(Fixtures.Read("Sheets/content.xlsx"));
        Assert.Equal([("Notes", SheetDiagnosticKind.Sparkline, 1)], Content(diagnostics));
        // Drawings, headers, footers, page breaks and the chart sheet's chart are printed.
        Assert.DoesNotContain(diagnostics, d => d.SheetName is "Plain" or "Drawings" or "Print" or "Chart1");
        Assert.Equal(["1 sparkline is left out."], diagnostics.Select(d => d.Message));
    }

    [Fact]
    public void Only_the_chosen_sheets_are_reported()
    {
        Assert.Single(Xlsx(Fixtures.Read("Sheets/content.xlsx"), ["Plain", "Notes"]).Select(d => d.SheetName).Distinct(), "Notes");

        // A chart sheet named by the options is converted: its chart is drawn.
        Assert.Empty(Content(Xlsx(Fixtures.Read("Sheets/content.xlsx"), ["Chart1"])));
    }

    [Theory]
    [InlineData("formats.xlsx")]
    [InlineData("wide.xlsx")]
    [InlineData("date1904.xlsx")]
    [InlineData("inline.xlsx")]
    [InlineData("multi.xlsx")]
    public void Workbooks_with_nothing_unsupported_report_nothing(string name) => Assert.Empty(Xlsx(Fixtures.Read("Sheets/" + name)));

    [Fact]
    public void A_comments_part_is_read_only_when_its_comments_are_printed()
    {
        // The comments part decompresses to more than the limit allows: read only when comments are printed (here, not by
        // the page setup), whether diagnostics are wanted or not; comments are not reported.
        var comments = string.Concat(Enumerable.Range(0, 2000).Select(i => $"<comment ref=\"A{i + 1}\" authorId=\"0\"><text><t>Comment {i}</t></text></comment>"));
        var big = Workbook(string.Empty, Relationship("rId1", "comments", "../comments1.xml"),
            parts: ("xl/comments1.xml", $"<comments xmlns=\"{Main}\"><authors><author>A</author></authors><commentList>{comments}</commentList></comments>"));
        var limits = new ResourceLimits { MaxDecompressedBytes = 64 * 1024 };
        Assert.NotEmpty(XlsxToPdf.ConvertToBytes(big, new XlsxConverterOptions { Sheet = new SheetOptions { Limits = limits } }));
        Assert.NotEmpty(XlsxToPdf.ConvertToBytes(big, new XlsxConverterOptions { Sheet = new SheetOptions { Limits = limits, Diagnostics = _ => { } } }));
        var exception = Assert.Throws<ResourceLimitExceededException>(() =>
            XlsxToPdf.ConvertToBytes(big, new XlsxConverterOptions { Comments = SheetComments.AtEnd, Sheet = new SheetOptions { Limits = limits } }));
        Assert.Equal(nameof(ResourceLimits.MaxDecompressedBytes), exception.LimitName);
        Assert.Empty(Content(Xlsx(big)));
    }

    [Fact]
    public void Drawing_parts_are_read_to_draw_them_within_the_limits_with_or_without_a_callback()
    {
        var big = Workbook("<drawing r:id=\"rId1\"/>", Relationship("rId1", "drawing", "../drawings/drawing1.xml"),
            parts: DrawingPart(string.Concat(Enumerable.Repeat(Anchor(Picture()), 2000))));
        var limits = new ResourceLimits { MaxDecompressedBytes = 64 * 1024 };
        foreach (var diagnostics in new Action<SheetDiagnostic>?[] { null, _ => { } })
        {
            var exception = Assert.Throws<ResourceLimitExceededException>(() =>
                XlsxToPdf.ConvertToBytes(big, new XlsxConverterOptions { Sheet = new SheetOptions { Limits = limits, Diagnostics = diagnostics } }));
            Assert.Equal(nameof(ResourceLimits.MaxDecompressedBytes), exception.LimitName);
        }

        // Pictures without an image part cannot be drawn.
        Assert.Equal([("Sheet1", SheetDiagnosticKind.Picture, 2000)], Content(Xlsx(big)));
    }

    // ---- XLSX: hand-made packages ----

    [Fact]
    public void Drawing_objects_that_cannot_be_drawn_are_counted_by_kind_in_groups_and_alternate_content()
    {
        var drawing = DrawingPart(
            Anchor(Picture())
            + Anchor(Shape())
            + Anchor("<xdr:cxnSp><xdr:nvCxnSpPr><xdr:cNvPr id=\"5\" name=\"Connector\"/></xdr:nvCxnSpPr></xdr:cxnSp>")
            + Anchor("<xdr:grpSp><xdr:nvGrpSpPr><xdr:cNvPr id=\"6\" name=\"Group\"/></xdr:nvGrpSpPr>" + Picture() + Shape() + Shape() + "</xdr:grpSp>")
            + Anchor(GraphicFrame("http://schemas.openxmlformats.org/drawingml/2006/chart"))
            + Anchor(GraphicFrame("http://schemas.openxmlformats.org/drawingml/2006/diagram")) // SmartArt
            + $"<mc:AlternateContent><mc:Choice Requires=\"cx1\">{Anchor(GraphicFrame("http://schemas.microsoft.com/office/drawing/2014/chartex"))}</mc:Choice>"
            + $"<mc:Fallback>{Anchor(Shape())}</mc:Fallback></mc:AlternateContent>"
            + "<xdr:absoluteAnchor><xdr:pos x=\"0\" y=\"0\"/><xdr:ext cx=\"1\" cy=\"1\"/>" + Picture() + "<xdr:clientData/></xdr:absoluteAnchor>"
            + "<xdr:oneCellAnchor><xdr:from><xdr:col>0</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>0</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>"
            + "<xdr:ext cx=\"1\" cy=\"1\"/><xdr:contentPart r:id=\"rId9\" xmlns:r=\"" + Relationships + "\"/><xdr:clientData/></xdr:oneCellAnchor>"

            // Left out: hidden objects (Excel's stand-ins for controls and embedded objects), and objects that do not print.
            + Anchor(Shape(hidden: true))
            + Anchor(Picture(), "<xdr:clientData fPrintsWithSheet=\"0\"/>")
            + "<xdr:twoCellAnchor/>");
        var diagnostics = Xlsx(Workbook("<drawing r:id=\"rId1\"/>", Relationship("rId1", "drawing", "../drawings/drawing1.xml"), parts: drawing));

        // Drawn: the shapes, the connector and the group (its shapes). Not: the pictures without an image part, the
        // chart without a chart part, the SmartArt, the newer chart type of the choice (not its fallback) and the ink.
        Assert.Equal(
        [
            ("Sheet1", SheetDiagnosticKind.Chart, 2),
            ("Sheet1", SheetDiagnosticKind.Picture, 3),
            ("Sheet1", SheetDiagnosticKind.Shape, 2),
        ], Content(diagnostics));
    }

    [Fact]
    public void Comments_pivot_tables_controls_and_embedded_objects_are_counted()
    {
        const string threaded = "http://schemas.microsoft.com/office/spreadsheetml/2018/threadedcomments";
        var xlsx = Workbook(
            "<oleObjects><mc:AlternateContent><mc:Choice Requires=\"x14\"><oleObject progId=\"Word.Document.12\" shapeId=\"1025\" r:id=\"rId6\"/></mc:Choice>"
            + "<mc:Fallback><oleObject progId=\"Word.Document.12\" shapeId=\"1025\" r:id=\"rId6\"/></mc:Fallback></mc:AlternateContent>"
            + "<oleObject progId=\"Package\" shapeId=\"1026\" r:id=\"rId7\"/></oleObjects>",
            Relationship("rId1", "comments", "../comments1.xml")
            + $"<Relationship Id=\"rId2\" Type=\"http://schemas.microsoft.com/office/2017/10/relationships/threadedComment\" Target=\"../threadedComments/threadedComment1.xml\"/>"
            + Relationship("rId3", "pivotTable", "../pivotTables/pivotTable1.xml")
            + Relationship("rId4", "ctrlProp", "../ctrlProps/ctrlProp1.xml")
            + Relationship("rId5", "control", "../activeX/activeX1.xml")
            + "<Relationship Id=\"rId8\" Type=\"" + Relationships + "/oleObject\" Target=\"file:///c:/linked.docx\" TargetMode=\"External\"/>",
            parts:
            [
                ("xl/comments1.xml", $"<comments xmlns=\"{Main}\"><authors><author>A</author></authors><commentList>"
                    + "<comment ref=\"A1\" authorId=\"0\"><text><t>One</t></text></comment><comment ref=\"B1\" authorId=\"0\"><text><t>Two</t></text></comment></commentList></comments>"),
                ("xl/threadedComments/threadedComment1.xml", $"<ThreadedComments xmlns=\"{threaded}\"><threadedComment ref=\"A1\" id=\"{{1}}\"><text>One</text></threadedComment>"
                    + $"<threadedComment ref=\"A1\" id=\"{{2}}\" parentId=\"{{1}}\"><text>Reply</text></threadedComment><threadedComment ref=\"C1\" id=\"{{3}}\"><text>Three</text></threadedComment></ThreadedComments>"),
            ]);
        Assert.Equal(
        [
            ("Sheet1", SheetDiagnosticKind.Shape, 1),
            ("Sheet1", SheetDiagnosticKind.OleObject, 3),
            ("Sheet1", SheetDiagnosticKind.PivotTable, 1), // Comments are printed (or left out by choice).
        ], Content(Xlsx(xlsx)));

        // Threaded comments without notes beside them.
        var threads = Workbook(string.Empty,
            $"<Relationship Id=\"rId2\" Type=\"http://schemas.microsoft.com/office/2017/10/relationships/threadedComment\" Target=\"../threadedComments/threadedComment1.xml\"/>",
            parts: ("xl/threadedComments/threadedComment1.xml", $"<ThreadedComments xmlns=\"{threaded}\"><threadedComment ref=\"A1\" id=\"{{1}}\"/><threadedComment ref=\"B1\" id=\"{{2}}\"/>"
                + "<threadedComment ref=\"B1\" id=\"{3}\" parentId=\"{2}\"/><threadedComment ref=\"C1\" id=\"{4}\"/></ThreadedComments>"));
        Assert.Empty(Content(Xlsx(threads)));
    }

    [Fact]
    public void Sheet_settings_are_counted_and_empty_or_automatic_ones_are_not()
    {
        var xlsx = Workbook(
            "<conditionalFormatting sqref=\"A1\"><cfRule type=\"expression\" priority=\"1\"><formula>INDIRECT(\"A1\")</formula></cfRule></conditionalFormatting>"
            + "<headerFooter><oddHeader>&amp;CTitle</oddHeader><oddFooter></oddFooter><evenHeader> </evenHeader><firstFooter>Page &amp;P</firstFooter></headerFooter>"
            + "<rowBreaks count=\"3\"><brk id=\"5\" man=\"1\"/><brk id=\"9\"/><brk id=\"12\" man=\"true\"/></rowBreaks><colBreaks><brk id=\"2\" man=\"0\"/></colBreaks>"
            + "<extLst><ext uri=\"{78C0D931-6437-407d-A8EE-F0AAD7539E65}\" xmlns:x14=\"http://schemas.microsoft.com/office/spreadsheetml/2009/9/main\">"
            + "<x14:conditionalFormattings><x14:conditionalFormatting><x14:cfRule type=\"iconSet\" id=\"{X}\"/></x14:conditionalFormatting></x14:conditionalFormattings></ext></extLst>");
        Assert.Equal(
        [
            ("Sheet1", SheetDiagnosticKind.ConditionalFormatting, 2), // The formula, and the icon set, which has no rule in the main list.
        ], Content(Xlsx(xlsx))); // Headers, footers and page breaks are printed.
    }

    [Fact]
    public void Cells_with_unsupported_formatting_are_counted_when_they_show_text()
    {
        const string styles = "<cellXfs count=\"5\"><xf/><xf><alignment textRotation=\"90\"/></xf><xf><alignment textRotation=\"255\" shrinkToFit=\"1\"/></xf>"
            + "<xf><alignment textRotation=\"0\" shrinkToFit=\"0\"/></xf><xf><alignment shrinkToFit=\"true\"/></xf></cellXfs>";
        var xlsx = XlsxToPdfTests.Workbook(
            "<row r=\"1\"><c r=\"A1\" s=\"1\"><v>1</v></c><c r=\"B1\" s=\"2\" t=\"s\"><v>0</v></c><c r=\"C1\" s=\"3\" t=\"s\"><v>1</v></c><c r=\"D1\" s=\"4\"><v>2</v></c>"
            + "<c r=\"E1\" s=\"1\"/><c r=\"F1\" t=\"inlineStr\"><is><r><rPr><i/></rPr><t>inline</t></r></is></c><c r=\"G1\" t=\"inlineStr\"><is><r><t>plain runs</t></r></is></c></row>",
            styles: styles,
            sharedStrings: "<si><r><rPr><b/></rPr><t>bold</t></r><r><t> plain</t></r></si><si><t>plain</t></si><si><r><rPr><b/></rPr><t>unused</t></r></si>");
        // Rich text, rotated and stacked text and shrink to fit are all shown.
        Assert.Empty(Content(Xlsx(xlsx)));
    }

    [Fact]
    public void Chart_sheets_without_a_drawing_are_reported_when_they_would_be_converted()
    {
        var xlsx = Workbook(string.Empty,
            workbookSheets: "<sheet name=\"Chart\" sheetId=\"2\" r:id=\"rId2\"/><sheet name=\"Hidden chart\" sheetId=\"3\" r:id=\"rId3\" state=\"hidden\"/>",
            workbookRelationships: Relationship("rId2", "chartsheet", "chartsheets/sheet1.xml") + Relationship("rId3", "chartsheet", "chartsheets/sheet2.xml"));
        Assert.Equal([("Chart", SheetDiagnosticKind.ChartSheet, 1)], Content(Xlsx(xlsx)));
        Assert.Equal([("Hidden chart", SheetDiagnosticKind.ChartSheet, 1)], Content(Xlsx(xlsx, ["hidden CHART"])));
    }

    [Fact]
    public void Damaged_parts_count_nothing_and_do_not_fail_the_conversion()
    {
        var xlsx = Workbook(string.Empty,
            Relationship("rId1", "drawing", "../drawings/drawing1.xml") + Relationship("rId2", "comments", "../missing.xml"),
            parts: ("xl/drawings/drawing1.xml", "<xdr:wsDr xmlns:xdr=\"" + Drawing + "\">" + Anchor(Picture()) + "<unclosed>"));
        Assert.Empty(Xlsx(xlsx));

        var badRelationships = Workbook(string.Empty, parts: ("xl/worksheets/_rels/sheet1.xml.rels", "<Relationships"));
        Assert.Empty(Xlsx(badRelationships));
    }

    // ---- XLS: the corpus, counted independently ----

    [Theory]
    [MemberData(nameof(XlsCorpus))]
    public void Xls_corpus_reports_the_content_its_records_have(string name)
    {
        var data = Fixtures.Read(Path.Combine("Sheets", "Xls", name));
        var expected = ExpectedJson.Value.GetProperty(name).GetProperty("content");
        var count = XlsConverter.Open(data, ResourceLimits.Default, CultureInfo.InvariantCulture, null).Workbook.Sheets.Count;
        var diagnostics = Xls(data, [.. Enumerable.Range(0, count)]);
        var actual = Content(diagnostics).Select(d => $"{d.Item1}: {d.Item2} {d.Item3}").Order(StringComparer.Ordinal);
        var wanted = expected.EnumerateObject()
            .SelectMany(sheet => sheet.Value.EnumerateObject().Select(kind => $"{sheet.Name}: {kind.Name} {kind.Value.GetInt32()}"))
            .Order(StringComparer.Ordinal);
        Assert.Equal(wanted, actual);
    }

    [Fact]
    public void Xls_corpus_spot_checks()
    {
        // By default, the visible sheets: not the hidden ones with a footer and with rich text. The picture and the text
        // boxes are drawn; the embedded chart (a BIFF chart, not DrawingML) is not.
        Assert.Empty(Content(Xls(Fixtures.Read("Sheets/Xls/46250.xls")))); // Its comment is printed as the page setup asks (not).
        Assert.Equal([("Sheet2", SheetDiagnosticKind.Chart, 1)], Content(Xls(Fixtures.Read("Sheets/Xls/WithChart.xls"))));
        Assert.Equal([("Graph2", SheetDiagnosticKind.ChartSheet, 1)], Content(Xls(Fixtures.Read("Sheets/Xls/44010-SingleChart.xls"))));
        Assert.Empty(Xls(Fixtures.Read("Sheets/Xls/SampleSS.xls")));
    }

    // ---- XLS: hand-made streams ----

    /// <summary>An OBJ record of BIFF8: its common data (type, flags) and, for an embedded object, its formula subrecord.</summary>
    private static byte[] Obj(int type, bool print = true, bool embedded = false) => Join(
        U16(0x15), U16(18), U16(type), U16(1), U16(print ? 0x0011 : 0x0001), new byte[12],
        embedded ? Join(U16(0x0009), U16(4), new byte[4]) : [],
        U16(0), U16(0));

    [Fact]
    public void Xls_records_are_counted_by_kind()
    {
        var xls = SingleSheet(s => s
            .Record(0x0203, Cell(0, 0), F64(1))
            .Record(0x005D, Obj(0x08)) // picture
            .Record(0x005D, Obj(0x08, embedded: true)) // embedded object
            .Record(0x005D, Obj(0x02)) // rectangle
            .Record(0x005D, Obj(0x0B)) // check box
            .Record(0x005D, Obj(0x05)) // chart
            .Record(0x005D, Obj(0x00)) // group: not counted
            .Record(0x005D, Obj(0x19)) // a comment's shape: its NOTE counts
            .Record(0x005D, Obj(0x14, print: false)) // a filter's drop-down arrow: does not print
            .Record(0x005D, U16(0x15), U16(18)) // cut short
            .Record(0x005D, Obj(0x08)[..3])
            .Record(0x001C, Cell(0, 0), U16(1), Text("Author"))
            .Record(0x01B0, new byte[12]).Record(0x01B1, new byte[6]).Record(0x01B1, new byte[6]).Record(0x087A, new byte[12])
            .Record(0x0014, Text("&CTitle")) // HEADER
            .Record(0x0015, U16(0), U8(0)) // FOOTER, empty
            .Record(0x0015) // FOOTER, none
            .Record(0x089C, new byte[28], U16(3), U16(0), U16(5), U16(0), U16(2), Encoding.ASCII.GetBytes("FirstEvenX")) // HEADERFOOTER: even footer, first footer
            .Record(0x001B, U16(2), new byte[12]) // HORIZONTALPAGEBREAKS
            .Record(0x001A, U16(1), new byte[6]) // VERTICALPAGEBREAKS
            .Record(0x00B0, new byte[44])); // SXVIEW
        Assert.Equal(
        [
            ("Sheet1", SheetDiagnosticKind.Chart, 1),
            ("Sheet1", SheetDiagnosticKind.Picture, 1),
            ("Sheet1", SheetDiagnosticKind.Shape, 2),
            ("Sheet1", SheetDiagnosticKind.OleObject, 1),
            ("Sheet1", SheetDiagnosticKind.ConditionalFormatting, 3),
            ("Sheet1", SheetDiagnosticKind.PivotTable, 1),
        ], Content(Xls(xls))); // Headers, footers and page breaks are printed.
    }

    [Fact]
    public void Xls_records_of_embedded_charts_are_not_counted()
    {
        var xls = SingleSheet(s => s
            .Record(0x0203, Cell(0, 0), F64(1))
            .Record(0x005D, Obj(0x05))
            .Bof(0x20).Record(0x005D, Obj(0x06)).Record(0x0014, Text("chart header")).Eof());
        Assert.Equal([("Sheet1", SheetDiagnosticKind.Chart, 1)], Content(Xls(xls)));
    }

    [Fact]
    public void Xls_cells_with_unsupported_formatting_are_counted_when_they_show_text()
    {
        byte[] Sst(params (string Text, bool Rich)[] strings) => Join([U32(strings.Length), U32(strings.Length),
            .. strings.Select(s => s.Rich ? Join(U16(s.Text.Length), U8(0x08), U16(1), Encoding.ASCII.GetBytes(s.Text), U16(0), U16(1)) : Text(s.Text))]);
        var xls = SingleSheet(
            s => s
                .Record(0x00FD, Cell(0, 0), U32(0)) // rich shared string
                .Record(0x00FD, Cell(0, 1), U32(1)) // plain shared string
                .Record(0x00D6, Cell(0, 2), Text("rich"), U16(1), U16(0), U16(1)) // RSTRING
                .Record(0x0203, Cell(1, 0, 1), F64(1)) // rotated
                .Record(0x0203, Cell(1, 1, 2), F64(2)) // stacked and shrunk
                .Record(0x0201, Cell(1, 2, 1)) // blank: nothing to rotate
                .Record(0x0203, Cell(1, 3, 3), F64(3)), // shrunk
            g => g
                .Font("Arial")
                .Xf().Record(0x00E0, U16(0), U16(0), U16(1), U8(0x20), U8(90), U8(0), U8(0), U32(0), U32(0), U16(64 | (65 << 7)))
                .Record(0x00E0, U16(0), U16(0), U16(1), U8(0x20), U8(255), U8(0x10), U8(0), U32(0), U32(0), U16(64 | (65 << 7)))
                .Record(0x00E0, U16(0), U16(0), U16(1), U8(0x20), U8(0), U8(0x10), U8(0), U32(0), U32(0), U16(64 | (65 << 7)))
                .Record(0x00FC, Sst(("bold", true), ("plain", false))));
        Assert.Empty(Content(Xls(xls))); // Rotated and stacked text, shrink to fit and rich text are shown.
    }

    [Fact]
    public void Biff5_records_are_counted()
    {
        var latin = Encoding.Latin1;
        var sheet = new BiffBuilder().Bof(0x10, 0x0500)
            .Record(0x0203, Cell(0, 0, 1), F64(1)) // rotated
            .Record(0x001C, Cell(0, 0), U16(5), latin.GetBytes("Note ")) // NOTE, continued
            .Record(0x001C, U16(0xFFFF), U16(0), U16(4), latin.GetBytes("more"))
            .Record(0x0014, Text5("&CTitle", latin, 1)) // HEADER
            .Record(0x0015, U8(0)) // FOOTER, empty
            .Record(0x005D, U32(1), U16(0x08), U16(1), new byte[30]) // OBJ: a picture
            .Record(0x005D, U32(1)) // cut short
            .Eof().ToArray();
        byte[] Globals(int offset) => new BiffBuilder().Bof(0x0005, 0x0500)
            .Record(0x0031, U16(200), U16(0), U16(0x7FFF), U16(400), U16(0), U8(0), U8(0), U8(0), U8(0), Text5("Arial", latin, 1))
            .Record(0x00E0, U16(0), U16(0), U16(1), U8(0x20), U8(0), U32(64 | (65 << 7)), U32(0))
            .Record(0x00E0, U16(0), U16(0), U16(1), U8(0x20), U8(0x02), U32(64 | (65 << 7)), U32(0))
            .Record(0x0085, U32(offset), U8(0), U8(0), Text5("Sheet", latin, 1))
            .Eof().ToArray();
        Assert.Equal(
        [
            ("Sheet", SheetDiagnosticKind.Picture, 1), // Not the note, nor the rotated text: they are printed.
        ], Content(Xls(Join(Globals(Globals(0).Length), sheet))));
    }

    [Fact]
    public void Xls_chart_sheets_are_reported_when_they_would_be_converted()
    {
        var xls = BiffBuilder.Workbook(null,
            new BiffSheet("Data", s => s.Record(0x0203, Cell(0, 0), F64(1))),
            new BiffSheet("Chart", s => s.Record(0x005D, Obj(0x06)), Type: 2),
            new BiffSheet("Hidden chart", s => { }, Hidden: 1, Type: 2));
        Assert.Equal([("Chart", SheetDiagnosticKind.ChartSheet, 1)], Content(Xls(xls)));
        Assert.Equal([("Hidden chart", SheetDiagnosticKind.ChartSheet, 1)], Content(Xls(xls, [2])));
    }

    // ---- Layout problems and CSV ----

    [Fact]
    public void Text_too_big_for_the_page_is_a_layout_problem_with_its_pages()
    {
        // A sheet that sizes its columns is printed in bands of columns and its text wraps, so only text too big for the
        // page itself does not fit: a 409 pt font on a small page is wider and taller than the page.
        var xlsx = XlsxToPdfTests.Workbook("<row r=\"1\"><c r=\"A1\" s=\"1\" t=\"inlineStr\"><is><t>Huge</t></is></c></row><row r=\"2\"><c r=\"A2\"><v>2</v></c></row>",
            styles: "<fonts count=\"2\"><font><sz val=\"11\"/></font><font><sz val=\"409\"/></font></fonts><cellXfs count=\"2\"><xf/><xf fontId=\"1\"/></cellXfs>");
        var page = new PageSetup(new PdfPageSize(200, 200), Thickness.All(10));
        var diagnostics = Diagnose(sheet => XlsxToPdf.ConvertToBytes(xlsx, new XlsxConverterOptions
        {
            Sheet = new SheetOptions { Page = page, Diagnostics = sheet.Diagnostics },
        }));
        var layout = Assert.Single(diagnostics);
        Assert.Equal(new SheetDiagnostic("Sheet1", SheetDiagnosticKind.Layout,
            "Content too wide for the page or its cell is drawn past the edge on page 1. Content taller than a page is cut off on page 2."), layout);

        // At its own size, the same sheet fits.
        Assert.Empty(Xlsx(XlsxToPdfTests.Workbook("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Huge</t></is></c></row>")));
    }

    [Fact]
    public void Layout_problems_are_numbered_by_the_pages_of_the_whole_document()
    {
        // The second sheet starts on page 2, after the first sheet's page; the order is each sheet's content, then its layout.
        var xlsx = Workbook(
            "<drawing r:id=\"rId1\"/>",
            Relationship("rId1", "drawing", "../drawings/drawing1.xml"),
            workbookSheets: "<sheet name=\"Huge\" sheetId=\"2\" r:id=\"rId2\"/>",
            workbookRelationships: Relationship("rId2", "worksheet", "worksheets/sheet2.xml") + Relationship("rId3", "styles", "styles.xml"),
            parts:
            [
                DrawingPart(Anchor(Picture())),
                ("xl/worksheets/sheet2.xml", $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"A1\" s=\"1\" t=\"inlineStr\"><is><t>Huge</t></is></c></row></sheetData>"
                    + "<headerFooter><oddHeader>Title</oddHeader></headerFooter></worksheet>"),
                ("xl/styles.xml", $"<styleSheet xmlns=\"{Main}\"><fonts count=\"2\"><font><sz val=\"11\"/></font><font><sz val=\"409\"/></font></fonts><cellXfs count=\"2\"><xf/><xf fontId=\"1\"/></cellXfs></styleSheet>"),
            ]);
        var page = new PageSetup(new PdfPageSize(200, 200), Thickness.All(10));
        var diagnostics = Diagnose(sheet => XlsxToPdf.ConvertToBytes(xlsx, new XlsxConverterOptions { Sheet = new SheetOptions { Page = page, Diagnostics = sheet.Diagnostics } }));
        Assert.Equal(
        [
            new SheetDiagnostic("Sheet1", SheetDiagnosticKind.Picture, "1 picture is left out."),
            new SheetDiagnostic("Huge", SheetDiagnosticKind.Layout,
                "Content too wide for the page or its cell is drawn past the edge on page 2. Content taller than a page is cut off on page 3."),
        ], diagnostics);
    }

    [Fact]
    public void Csv_reports_layout_problems_without_a_sheet_name()
    {
        var page = new PageSetup(new PdfPageSize(200, 200), Thickness.All(10));
        var diagnostics = Diagnose(sheet => CsvToPdf.ConvertToBytes(Encoding.ASCII.GetBytes("8\n"),
            new CsvConverterOptions { HasHeaderRow = false, Sheet = new SheetOptions { Page = page, FontSize = 300, Diagnostics = sheet.Diagnostics } }));
        var layout = Assert.Single(diagnostics);
        Assert.Null(layout.SheetName);
        Assert.Equal(SheetDiagnosticKind.Layout, layout.Kind);
        Assert.StartsWith("Content too wide for the page or its cell is drawn past the edge on page 1.", layout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_with_text_that_fits_reports_nothing()
    {
        Assert.Empty(Diagnose(sheet => CsvToPdf.ConvertToBytes(Encoding.ASCII.GetBytes("a,b\n1,2\n"), new CsvConverterOptions { Sheet = sheet })));

        // A number wider than the page is broken rather than drawn past its edge.
        Assert.Empty(Diagnose(sheet => CsvToPdf.ConvertToBytes(Encoding.ASCII.GetBytes("Id,Number\n1," + new string('9', 400) + "\n"), new CsvConverterOptions { Sheet = sheet })));
    }

    [Fact]
    public void Layout_messages_list_the_pages()
    {
        Assert.Equal("page 3", LayoutProblems.Pages([3]));
        Assert.Equal("pages 2 and 5", LayoutProblems.Pages([5, 2]));
        Assert.Equal("pages 1, 2 and 3", LayoutProblems.Pages([1, 2, 3]));
        Assert.Equal("pages 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 and 2 more", LayoutProblems.Pages([.. Enumerable.Range(1, 12)]));
        var problems = new LayoutProblems();
        problems.Add(LayoutDiagnosticKind.Clipped, 4);
        problems.Add(LayoutDiagnosticKind.ScaledToFit, 2);
        problems.Add(LayoutDiagnosticKind.Overflow, 1);
        SheetDiagnostic? reported = null;
        problems.Report("S", d => reported = d);
        Assert.Equal("Content too wide for the page or its cell is drawn past the edge on page 1. Content taller than a page is cut off on page 4. "
            + "An image taller than a page is scaled down on page 2.", reported?.Message);
    }

    [Fact]
    public void Messages_name_one_or_many()
    {
        foreach (var kind in Enum.GetValues<SheetDiagnosticKind>().Where(k => k is not SheetDiagnosticKind.ChartSheet and not SheetDiagnosticKind.Layout))
        {
            Assert.Matches("^1 .* is ", UnsupportedContent.Message(kind, 1));
            Assert.Matches("^7 .* are ", UnsupportedContent.Message(kind, 7));
        }
    }
}
