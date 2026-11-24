using RaiCore.Pdf.Xfa;
using static RaiCore.Pdf.Tests.Xfa.Xdp;

namespace RaiCore.Pdf.Tests.Xfa;

/// <summary>
/// Layout and pagination: positioned and flowed subforms, tables, growable objects, captions, rotation, page areas,
/// content areas, breaks, overflow leaders and trailers, and keep rules. Positions are checked in PDF coordinates (the
/// default content area starts at 36 points from the left and from the top of a 792 point high page).
/// </summary>
public class XfaLayoutTests
{
    private const double Left = 36;

    private const double Top = 792 - 36;

    /// <summary>A subform (breaks are subforms' properties) with one draw and a break or keep rule.</summary>
    private static string Section(string text, string rule) => $"""<subform layout="tb" w="7.5in">{rule}{Draw(text)}</subform>""";

    [Fact]
    public void Positioned_objects_are_placed_by_their_anchor()
    {
        var xdp = Form(
            Draw("TopLeft", "1in", "0.5in", "x=\"1in\" y=\"1in\"") + Draw("Centered", "2in", "1in", "x=\"4in\" y=\"4in\" anchorType=\"middleCenter\"")
            + Draw("BottomRight", "1in", "0.5in", "x=\"6in\" y=\"6in\" anchorType=\"bottomRight\""),
            layout: "position");

        var pdf = Convert(xdp);
        Assert.InRange(Find(pdf, 0, "TopLeft").X, Left + 72 - 1, Left + 72 + 2);
        Assert.InRange(Top - Find(pdf, 0, "TopLeft").Top, 72 - 1, 72 + 4);
        Assert.InRange(Find(pdf, 0, "Centered").X, Left + 216 - 1, Left + 216 + 2);
        Assert.InRange(Top - Find(pdf, 0, "Centered").Top, 252 - 1, 252 + 4);
        Assert.InRange(Find(pdf, 0, "BottomRight").X, Left + 360 - 1, Left + 360 + 2);
        Assert.InRange(Top - Find(pdf, 0, "BottomRight").Top, 396 - 1, 396 + 4);
    }

    [Fact]
    public void Top_to_bottom_flows_stack_their_objects()
    {
        var pdf = Convert(Form(Draw("First", h: "1in") + Draw("Second", h: "0.5in") + Draw("Third")));

        Assert.InRange(Top - Find(pdf, 0, "Second").Top, 72 - 1, 72 + 4);
        Assert.InRange(Top - Find(pdf, 0, "Third").Top, 108 - 1, 108 + 4);
    }

    [Fact]
    public void Left_to_right_flows_wrap_into_lines_and_right_to_left_ones_start_at_the_right()
    {
        var cells = string.Concat(Enumerable.Range(1, 4).Select(i => Draw($"Cell{i}", "3in")));
        var pdf = Convert(Form($"""<subform layout="lr-tb" w="7.5in">{cells}</subform><subform layout="rl-tb" w="7.5in">{Draw("Right", "3in")}</subform>"""));

        // Two 3 inch cells fit a 7.5 inch line.
        Assert.InRange(Find(pdf, 0, "Cell2").X, Left + 216 - 1, Left + 216 + 2);
        Assert.InRange(Find(pdf, 0, "Cell3").X, Left - 1, Left + 2);
        Assert.InRange(Top - Find(pdf, 0, "Cell3").Top, 21.6 - 1, 21.6 + 4);
        Assert.InRange(Find(pdf, 0, "Right").X, Left + 324 - 1, Left + 324 + 2);
    }

    [Fact]
    public void Tables_place_cells_in_their_columns_with_spans_and_automatic_widths()
    {
        var xdp = Form($"""
            <subform layout="table" columnWidths="1in -1 2in">
              <subform layout="row">{Draw("A1", "0.5in")}{Draw("Automatic", "1.5in")}{Draw("C1", "0.5in")}</subform>
              <subform layout="row">{Draw("Spanning", "0.5in", attributes: "colSpan=\"2\"")}{Draw("C2", "0.5in")}</subform>
            </subform>
            """);

        var pdf = Convert(xdp);
        Assert.InRange(Find(pdf, 0, "Automatic").X, Left + 72 - 1, Left + 72 + 2);
        Assert.InRange(Find(pdf, 0, "C1").X, Left + 180 - 1, Left + 180 + 2);
        Assert.InRange(Find(pdf, 0, "C2").X, Left + 180 - 1, Left + 180 + 2);
    }

    [Fact]
    public void Cells_are_as_tall_as_their_row()
    {
        var xdp = Form($"""
            <subform layout="table" columnWidths="2in 2in">
              <subform layout="row">{Draw("Short", "2in", "0.3in", content: "<para vAlign=\"bottom\"/>")}{Draw("Tall", "2in", "1in")}</subform>
            </subform>
            """);

        // The short cell is stretched to the row's inch and its text sits at its bottom.
        var pdf = Convert(xdp);
        Assert.InRange(Top - Find(pdf, 0, "Short").Bottom(), 60, 73);
    }

    [Fact]
    public void Growable_objects_fit_their_content_within_their_limits()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 120));
        var xdp = Form($"""
            <draw w="3in" minH="0.2in"><value><text>{text}</text></value><font typeface="Arial" size="10pt"/></draw>
            {Draw("After")}
            <draw minW="0.5in" maxW="1in" h="0.3in"><value><text>Narrow growable text that wraps</text></value></draw>
            """);

        var pdf = Convert(xdp);

        // 120 words at 3 inches take many lines: "After" is far below the content area's top.
        Assert.True(Top - Find(pdf, 0, "After").Top > 100);
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("top")]
    [InlineData("bottom")]
    public void Captions_are_placed_beside_the_value(string placement)
    {
        var xdp = Form(
            Field("Name", "4in", "2in", content: $"<caption placement=\"{placement}\" reserve=\"1in\"><value><text>Caption</text></value></caption>"),
            "<form1><Name>Value</Name></form1>");

        var pdf = Convert(xdp);
        var caption = Find(pdf, 0, "Caption");
        var value = Find(pdf, 0, "Value");
        switch (placement)
        {
            case "left":
                Assert.True(value.X >= caption.X + 72 - 1);
                break;
            case "right":
                Assert.True(caption.X >= value.X + (3 * 72) - 1);
                break;
            case "top":
                Assert.True(caption.Y > value.Y);
                break;
            default:
                Assert.True(caption.Y < value.Y);
                break;
        }
    }

    [Fact]
    public void A_hanging_indent_without_a_margin_starts_at_the_text_area()
    {
        var text = "1. " + string.Join(' ', Enumerable.Repeat("word", 40));
        var pdf = Convert(Form(Draw(text, "3in", "2in", content: "<para textIndent=\"-20pt\" marginLeft=\"0pt\"/>")));

        // The first line starts at the draw's left edge (not 20 points left of it), the others 20 points in.
        Assert.InRange(Find(pdf, 0, "1.").X, Left - 0.5, Left + 2);
    }

    [Fact]
    public void Rotated_objects_take_their_turned_extent_in_a_flow()
    {
        var pdf = Convert(Form(Draw("Turned", "2in", "0.5in", "rotate=\"90\"") + Draw("Below")));

        // Turned by 90 degrees, the draw is 2 inches tall.
        Assert.InRange(Top - Find(pdf, 0, "Below").Top, 144 - 1, 144 + 4);
    }

    [Fact]
    public void Breaks_go_to_the_named_page_area_or_the_next_content_area()
    {
        var xdp = Form("""
            <pageSet>
              <pageArea name="P1" id="P1"><contentArea x="0.5in" y="0.5in" w="3in" h="10in"/><contentArea x="4.5in" y="0.5in" w="3in" h="10in"/><medium stock="letter" short="8.5in" long="11in"/></pageArea>
              <pageArea name="P2" id="P2"><contentArea x="1in" y="1in" w="6.5in" h="9in"/><medium stock="letter" short="8.5in" long="11in" orientation="landscape"/></pageArea>
            </pageSet>
            """ + Draw("One") + Section("Two", "<breakBefore targetType=\"contentArea\"/>") + Section("Three", "<breakBefore targetType=\"pageArea\" target=\"#P2\"/>")
            + Section("Four", "<breakAfter targetType=\"pageArea\" target=\"P1\"/>") + Draw("Five"));

        var pdf = Convert(xdp);
        var pages = Pages(pdf);
        Assert.Equal(3, pages.Length);
        Assert.InRange(Find(pdf, 0, "Two").X, 324 - 1, 324 + 2);
        Assert.Contains("Three", pages[1], StringComparison.Ordinal);
        Assert.Contains("Four", pages[1], StringComparison.Ordinal);
        Assert.Contains("Five", pages[2], StringComparison.Ordinal);
        Assert.Equal(792, PdfReader.Open(pdf).Pages[1].Width, 1);
    }

    [Fact]
    public void Breaks_go_to_a_named_content_area_and_start_new_pages_when_asked()
    {
        var xdp = Form("""
            <pageSet><pageArea name="P1"><contentArea id="A" x="0.5in" y="0.5in" w="2in" h="10in"/><contentArea id="B" x="3in" y="0.5in" w="2in" h="10in"/><contentArea id="C" x="5.5in" y="0.5in" w="2in" h="10in"/><medium stock="letter" short="8.5in" long="11in"/></pageArea></pageSet>
            """ + Draw("One", "2in") + Section("Two", "<breakAfter targetType=\"contentArea\"/>")
            + Section("Next", "<breakBefore targetType=\"pageArea\"/>") + Section("Three", "<breakBefore targetType=\"contentArea\" target=\"#C\"/>")
            + Section("Last", "<breakAfter targetType=\"pageArea\"/>") + Section("Fresh", "<breakBefore targetType=\"pageArea\" startNew=\"1\"/>"));

        var pdf = Convert(xdp);
        var pages = Pages(pdf);

        // A page area break from an empty content area that is not the page's first still starts a new page.
        Assert.Contains("Next", pages[1], StringComparison.Ordinal);
        Assert.Contains("Three", pages[1], StringComparison.Ordinal);
        Assert.InRange(Find(pdf, 1, "Three").X, 396 - 1, 396 + 2);

        // A break that starts new pages does so even at the top of an empty page.
        Assert.Equal(4, pages.Length);
        Assert.Contains("Fresh", pages[3], StringComparison.Ordinal);
    }

    [Fact]
    public void A_break_at_the_start_of_a_page_does_not_leave_it_blank()
    {
        var xdp = Form("""
            <pageSet><pageArea name="A" id="A"><contentArea x="0.5in" y="0.5in" w="7.5in" h="10in"/></pageArea><pageArea name="B" id="B"><contentArea x="0.5in" y="0.5in" w="7.5in" h="10in"/></pageArea></pageSet>
            """ + Section("Start", "<break before=\"pageArea\" beforeTarget=\"#B\"/>"));

        Assert.Single(Pages(Convert(xdp)));
    }

    [Fact]
    public void Odd_and_even_page_breaks_add_a_blank_page_when_needed()
    {
        var xdp = Form(Draw("One") + Section("Odd", "<breakBefore targetType=\"pageOdd\"/>"));

        var pages = Pages(Convert(xdp));
        Assert.Equal(3, pages.Length);
        Assert.Contains("Odd", pages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Split_containers_repeat_their_leader_and_trailer()
    {
        var rows = string.Concat(Enumerable.Range(1, 30).Select(i => $"<Row><V>Row{i:00}</V></Row>"));
        var xdp = Form($"""
            <subform name="List" layout="tb" w="7.5in">
              <overflow leader="#Lead" trailer="Trail"/>
              <subform name="Row" w="7.5in" h="0.5in"><occur min="0" max="-1"/>{Field("V")}</subform>
            </subform>
            <proto>
              <subform id="Lead" w="7.5in" h="0.5in">{Draw("Continued")}</subform>
              <subform name="Trail" w="7.5in" h="0.5in">{Draw("Turn over")}</subform>
            </proto>
            """, $"<form1><List>{rows}</List></form1>");

        var pages = Pages(Convert(xdp));
        Assert.Equal(2, pages.Length);

        // Page 1: 19 rows and the trailer (10 inches); page 2 starts with the leader.
        Assert.Contains("Row19", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Row20", pages[0], StringComparison.Ordinal);
        Assert.Contains("Turn over", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Continued", pages[0], StringComparison.Ordinal);
        Assert.StartsWith("Continued", pages[1].TrimStart(), StringComparison.Ordinal);
        Assert.Contains("Row30", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Circular_overflow_leaders_are_ignored_and_reported()
    {
        // Each list's leader is the other list, and the outer one's leader contains it.
        var xdp = Form($"""
            <subform name="Outer" id="outer" layout="tb"><overflow leader="#inner"/>
              <subform name="Inner" id="inner" layout="tb"><overflow leader="#outer" trailer="Outer"/>{Draw("Content")}</subform>
            </subform>
            """);

        var (pdf, diagnostics) = ConvertWithDiagnostics(xdp);
        Assert.Contains("Content", Pages(pdf)[0], StringComparison.Ordinal);
        Assert.Contains(diagnostics, d => d.Kind == XfaDiagnosticKind.UnsupportedContent && d.Message.Contains("#outer", StringComparison.Ordinal));
    }

    [Fact]
    public void Long_text_continues_in_the_next_content_area_between_its_lines()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"Line{i:000}"));
        var xdp = Form(
            Draw("Before", h: "2in")
            + """<field name="Remarks" w="7in" minH="0.3in"><ui><textEdit multiLine="1"/></ui><font typeface="Arial" size="10pt"/><border><edge/></border>"""
            + """<caption reserve="1in"><value><text>Remarks</text></value></caption></field>"""
            + Draw("After"),
            $"<form1><Remarks>{lines}</Remarks></form1>");

        var pdf = Convert(xdp);
        var pages = Pages(pdf);
        Assert.Equal(2, pages.Length);

        // The lines on page 1 stay above the content area's bottom (0.5 inch from the page's).
        var reader = new RaiCore.Pdf.Extraction.TextExtractor(PdfReader.Open(pdf));
        var lowest = Enumerable.Range(1, 100).SelectMany(i => reader.FindOnPage(0, $"Line{i:000}")).Min(m => m.Bounds.Y);
        Assert.True(lowest >= 36 - 0.5, $"A line reaches {lowest}");

        // Each line once, in order: the first part fills page 1 below "Before", the rest starts page 2.
        var all = string.Join("\n", pages);
        for (var i = 1; i <= 100; i++)
        {
            Assert.Equal(1, all.Split($"Line{i:000}").Length - 1);
        }

        Assert.Contains("Line001", pages[0], StringComparison.Ordinal);
        Assert.Contains("Line100", pages[1], StringComparison.Ordinal);
        Assert.Contains("After", pages[1], StringComparison.Ordinal);
        Assert.Equal(1, all.Split("Remarks").Length - 1);
        Assert.StartsWith("Line", pages[1].TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public void Objects_kept_with_the_next_one_move_with_it()
    {
        var xdp = Form(Draw("Filler", h: "9.5in") + Draw("Heading", content: "<keep next=\"contentArea\"/>") + Draw("Body", h: "1in"));

        var pages = Pages(Convert(xdp));
        Assert.Contains("Heading", pages[1], StringComparison.Ordinal);
        Assert.Contains("Body", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Objects_kept_with_the_next_one_move_with_it_inside_a_flow()
    {
        var xdp = Form($"""<subform layout="tb" w="7.5in">{Draw("Filler", h: "9.3in")}{Draw("Heading", content: "<keep next=\"contentArea\"/>")}{Draw("Body", h: "1in")}</subform>""");

        var pages = Pages(Convert(xdp));
        Assert.Contains("Heading", pages[1], StringComparison.Ordinal);
        Assert.Contains("Body", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Page_areas_are_used_up_to_their_maximum_in_order()
    {
        string Area(string name, string occur) =>
            $"""<pageArea name="{name}">{occur}<contentArea x="0.5in" y="0.5in" w="7.5in" h="10in"/>{Draw(name + "Page", attributes: "x=\"0.5in\" y=\"0.1in\"")}</pageArea>""";
        var xdp = Form($"""<pageSet>{Area("Once", "<occur max=\"1\"/>")}{Area("Then", string.Empty)}</pageSet>""" + Draw("A", h: "9in") + Draw("B", h: "9in") + Draw("C", h: "9in"));

        var pages = Pages(Convert(xdp));
        Assert.Equal(3, pages.Length);
        Assert.Contains("OncePage", pages[0], StringComparison.Ordinal);
        Assert.Contains("ThenPage", pages[1], StringComparison.Ordinal);
        Assert.Contains("ThenPage", pages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Content_taller_than_a_page_is_cut_off_and_reported()
    {
        var (pdf, diagnostics) = ConvertWithDiagnostics(Form(Draw("Huge", h: "20in")));

        Assert.Single(Pages(pdf));
        Assert.Contains(diagnostics, d => d.Kind == XfaDiagnosticKind.Layout);
    }

    [Fact]
    public void Paginated_page_sets_choose_page_areas_by_position()
    {
        string Area(string name) =>
            $"""<pageArea name="{name}" pagePosition="{name.ToLowerInvariant()}"><contentArea x="0.5in" y="0.5in" w="7.5in" h="10in"/>{Draw(name + "Page", attributes: "x=\"0.5in\" y=\"0.1in\"")}</pageArea>""";
        var xdp = Form($"""<pageSet relation="simplexPaginated">{Area("First")}{Area("Rest")}{Area("Last")}</pageSet>""" + Draw("A", h: "9in") + Draw("B", h: "9in") + Draw("C", h: "1.5in"));

        var pages = Pages(Convert(xdp));
        Assert.Equal(3, pages.Length);
        Assert.Contains("FirstPage", pages[0], StringComparison.Ordinal);
        Assert.Contains("RestPage", pages[1], StringComparison.Ordinal);
        Assert.Contains("LastPage", pages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_form_without_a_page_set_is_laid_out_on_letter_pages()
    {
        var xdp = $"""<template xmlns="{TemplateNamespace}"><subform name="form1" layout="tb">{Draw("Alone")}</subform></template>""";

        var (pdf, diagnostics) = ConvertWithDiagnostics(xdp);
        Assert.Equal(612, PdfReader.Open(pdf).Pages[0].Width, 1);
        Assert.Contains(diagnostics, d => d.Kind == XfaDiagnosticKind.Layout);
    }

    [Fact]
    public void Unsupported_content_is_reported()
    {
        var xdp = Form(
            """<field name="Code" w="2in" h="0.5in"><ui><barcode type="code128"/></ui></field>"""
            + """<draw w="1in" h="1in"><value><image href="logo.png"/></value></draw>"""
            + """<draw w="1in" h="1in"><value><image contentType="image/png">bm90IGFuIGltYWdl</image></value></draw>"""
            + """<draw w="1in" h="0.3in"><value><text>Odd</text></value><font typeface="Imaginary Sans"/></draw>"""
            + """<draw w="1in" h="0.3in" use="other.xdp#x"/>""",
            "<form1><Code>12345</Code></form1>");

        var (pdf, diagnostics) = ConvertWithDiagnostics(xdp);
        Assert.Contains("12345", Pages(pdf)[0], StringComparison.Ordinal);
        Assert.Contains(diagnostics, d => d.Kind == XfaDiagnosticKind.UnsupportedContent && d.Message.Contains("barcode", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Kind == XfaDiagnosticKind.UnsupportedContent && d.Message.Contains("other.xdp#x", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Kind == XfaDiagnosticKind.ImageNotLoaded && d.Message.Contains("logo.png", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Kind == XfaDiagnosticKind.ImageNotLoaded && d.Message.Contains("cannot be read", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Kind == XfaDiagnosticKind.FontSubstituted && d.Message.Contains("Imaginary Sans", StringComparison.Ordinal));
    }

    [Fact]
    public void Images_larger_than_the_pixel_limit_are_left_out_and_reported()
    {
        var image = System.Convert.ToBase64String(Fixtures.Read(Path.Combine("PngSuite", "basn2c08.png")));
        var xdp = Form($"""<draw w="1in" h="1in"><value><image contentType="image/png">{image}</image></value></draw>""");

        var (_, diagnostics) = ConvertWithDiagnostics(xdp, new XfaConverterOptions { Limits = ResourceLimits.Default with { MaxImagePixels = 100 } });
        Assert.Contains(diagnostics, d => d.Kind == XfaDiagnosticKind.ImageNotLoaded && d.Message.Contains("MaxImagePixels", StringComparison.Ordinal));
    }
}

internal static class RectangleExtensions
{
    public static double Bottom(this PdfRectangle rectangle) => rectangle.Y;
}
