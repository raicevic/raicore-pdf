using System.Text;
using System.Text.Json;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Xfa;
using static RaiCore.Pdf.Tests.Xfa.Xdp;

namespace RaiCore.Pdf.Tests.Xfa;

/// <summary>
/// Rendering dynamic XFA forms to flattened PDF pages: public government forms and hand-made forms checked against the
/// expectations an independent Python reading of their XFA computes (make-xfa.py: which data values are shown, on which
/// page), the PDF and XDP inputs, diagnostics, and malformed and hostile forms.
/// </summary>
public class XfaToPdfTests
{
    private static byte[] Fixture(string name) => Fixtures.Read(Path.Combine("Xfa", name));

    private static void AssertValid(byte[] pdf)
    {
        if (ExternalTools.Find("qpdf") is not null)
        {
            ExternalTools.AssertQpdfClean(pdf);
        }
    }

    public static TheoryData<string> FixtureNames()
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(Fixtures.Path(Path.Combine("Xfa", "expected.json"))));
        return [.. expected.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixtures_show_their_data_on_the_pages_the_oracle_expects(string name)
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(Fixtures.Path(Path.Combine("Xfa", "expected.json"))));
        var expectation = expected.RootElement.GetProperty(name);
        var pdf = XfaToPdf.ConvertToBytes(Fixture(name));
        AssertValid(pdf);

        // Whitespace is compared loosely: line breaks and the spaces text extraction puts between words.
        var pages = Pages(pdf).Select(p => string.Join(' ', p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).ToArray();
        Assert.Equal(expectation.GetProperty("pages").GetInt32(), pages.Length);
        var missing = new List<string>();
        foreach (var value in expectation.GetProperty("values").EnumerateArray())
        {
            var text = value.GetProperty("text").GetString()!;
            var page = value.GetProperty("page").GetInt32();
            if (!pages[page - 1].Contains(text, StringComparison.Ordinal))
            {
                var found = Enumerable.Range(0, pages.Length).Where(i => pages[i].Contains(text, StringComparison.Ordinal)).Select(i => i + 1);
                missing.Add($"'{text}' on page {page} (found on {string.Join(", ", found)})");
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing));
        foreach (var absent in expectation.GetProperty("absent").EnumerateArray())
        {
            Assert.DoesNotContain(pages, p => p.Contains(absent.GetString()!, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_output_has_no_xfa_and_no_form()
    {
        var pdf = XfaToPdf.Convert(Fixture("invoice.pdf"));
        var reader = PdfReader.Open(pdf.ToArray());

        Assert.Null(reader.Catalog["AcroForm"]);
        Assert.Null(reader.Catalog["NeedsRendering"]);
        Assert.Equal("invoice.pdf", pdf.Info.Title);
        Assert.Equal("RaiCore.Pdf.Xfa", pdf.Info.Creator);
    }

    [Fact]
    public void Pages_have_the_page_areas_size_and_orientation()
    {
        var pdf = PdfReader.Open(XfaToPdf.ConvertToBytes(Fixture("gsa2131.pdf")));

        // The form's first page area is landscape Letter, its second portrait.
        Assert.Equal((792, 612), Size(pdf.Pages[0]));
        Assert.Equal((612, 792), Size(pdf.Pages[1]));

        static (double, double) Size(PdfReaderPage page) => (Math.Round(page.Width), Math.Round(page.Height));
    }

    [Fact]
    public void Repeated_rows_continue_on_the_next_page_under_the_overflow_leader()
    {
        var pdf = XfaToPdf.ConvertToBytes(Fixture("invoice.pdf"));
        var pages = Pages(pdf);

        // The header row is the table's leader: it is at the top of the table on both pages.
        Assert.Contains("Description", pages[0], StringComparison.Ordinal);
        Assert.Contains("Description", pages[1], StringComparison.Ordinal);
        Assert.True(Find(pdf, 1, "Description").Y > Find(pdf, 1, "Item number 26").Y);
        Assert.DoesNotContain("Item number 26", pages[0], StringComparison.Ordinal);
        Assert.Contains("Item number 25", pages[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Content_fills_the_content_areas_in_order()
    {
        var pdf = XfaToPdf.ConvertToBytes(Fixture("layouts.pdf"));

        // Two content areas side by side: 18 entries of half an inch fill the left one (9 inches), the rest go right.
        var left = Find(pdf, 1, "Entry 18");
        var right = Find(pdf, 1, "Entry 19");
        Assert.InRange(left.X, 36, 60);
        Assert.InRange(right.X, 316, 340);
        Assert.True(right.Y > left.Y);
    }

    [Fact]
    public void Scripts_are_reported_and_not_run()
    {
        var diagnostics = new List<XfaDiagnostic>();
        XfaToPdf.Convert(Fixture("sf328.pdf"), new XfaConverterOptions { Diagnostics = diagnostics.Add });

        var script = Assert.Single(diagnostics, d => d.Kind == XfaDiagnosticKind.ScriptNotRun);
        Assert.Contains("script(s)", script.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_xfa_is_read_from_an_array_of_packets_or_one_stream(bool array)
    {
        var pdf = XfaPdf(Form(Field("Name"), "<form1><Name>Grace Hopper</Name></form1>"), array);

        Assert.Contains("Grace Hopper", Pages(XfaToPdf.ConvertToBytes(pdf))[0], StringComparison.Ordinal);
    }

    [Fact]
    public void An_xdp_file_converts_like_a_pdf()
    {
        var pdf = Convert(Form(Field("Name"), "<form1><Name>Grace Hopper</Name></form1>"));

        Assert.Contains("Grace Hopper", Pages(pdf)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_path_and_async_overloads_agree()
    {
        var bytes = Fixture("gsa52.pdf");
        var expected = Pages(XfaToPdf.ConvertToBytes(bytes));
        var path = Path.Combine(Path.GetTempPath(), $"raicore-xfa-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(expected, Pages(XfaToPdf.Convert(path).ToArray()));
            using var stream = new MemoryStream(bytes);
            Assert.Equal(expected, Pages(XfaToPdf.ConvertToBytes(stream)));
            using var async = new MemoryStream(bytes);
            Assert.Equal(expected, Pages((await XfaToPdf.ConvertAsync(async, cancellationToken: TestContext.Current.CancellationToken)).ToArray()));
            Assert.Equal(expected, Pages((await XfaToPdf.ConvertAsync(bytes, cancellationToken: TestContext.Current.CancellationToken)).ToArray()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Cancellation_stops_the_conversion()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XfaToPdf.ConvertAsync(Fixture("gsa52.pdf"), cancellationToken: cancel.Token));
    }

    [Fact]
    public void Data_given_in_the_options_replaces_the_forms_own()
    {
        var xdp = Form(Field("Name"), "<form1><Name>Old</Name></form1>");

        Assert.Contains("New", Pages(Convert(xdp, new XfaConverterOptions { Data = "<form1><Name>New</Name></form1>" }))[0], StringComparison.Ordinal);
        Assert.Contains("Datasets", Pages(Convert(xdp, new XfaConverterOptions
        {
            Data = $"""<xfa:datasets xmlns:xfa="{DataNamespace}"><xfa:data><form1><Name>Datasets</Name></form1></xfa:data></xfa:datasets>""",
        }))[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Old", Pages(Convert(xdp, new XfaConverterOptions { Data = "<form1/>" }))[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_pdf_without_xfa_is_a_format_error()
    {
        var pdf = Reading.TestPdf.Pages(string.Empty, ("/MediaBox [0 0 100 100]", string.Empty));

        Assert.Throws<XfaFormatException>(() => XfaToPdf.Convert(pdf));
    }

    [Theory]
    [InlineData("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><config/></xdp:xdp>")]
    [InlineData("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><template><subform>")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><template><subform name=\"f\"><draw><value><text>&e;</text></value></draw></subform></template>")]
    [InlineData("<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"><pageSet/></template>")]
    [InlineData("not xml at all")]
    public void Malformed_xfa_is_a_format_error(string xdp)
    {
        Assert.Throws<XfaFormatException>(() => Convert(xdp));
    }

    [Fact]
    public void A_malformed_pdf_is_a_pdf_format_error()
    {
        Assert.ThrowsAny<PdfFormatException>(() => XfaToPdf.Convert(Encoding.ASCII.GetBytes("%PDF-1.7\ngarbage")));
    }

    [Fact]
    public void Too_many_elements_exceed_the_limit()
    {
        var xdp = Form(string.Concat(Enumerable.Repeat(Draw("x"), 200)));
        var limits = ResourceLimits.Default with { MaxElements = 300 };

        var e = Assert.Throws<ResourceLimitExceededException>(() => Convert(xdp, new XfaConverterOptions { Limits = limits }));
        Assert.Equal(nameof(ResourceLimits.MaxElements), e.LimitName);
    }

    [Fact]
    public void Repetition_without_bound_is_limited_by_the_element_limit()
    {
        // A billion initial instances of a subform.
        var xdp = Form("""<subform name="Row" w="1in" h="0.1in"><occur min="0" max="-1" initial="1000000000"/></subform>""");
        var limits = ResourceLimits.Default with { MaxElements = 10_000 };

        var e = Assert.Throws<ResourceLimitExceededException>(() => Convert(xdp, new XfaConverterOptions { Limits = limits }));
        Assert.Equal(nameof(ResourceLimits.MaxElements), e.LimitName);
    }

    [Fact]
    public void Deep_nesting_exceeds_the_limit()
    {
        var depth = 100;
        var xdp = Form(string.Concat(Enumerable.Repeat("<subform layout=\"tb\">", depth)) + string.Concat(Enumerable.Repeat("</subform>", depth)));
        var limits = ResourceLimits.Default with { MaxNestingDepth = 50 };

        var e = Assert.Throws<ResourceLimitExceededException>(() => Convert(xdp, new XfaConverterOptions { Limits = limits }));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), e.LimitName);
    }

    [Fact]
    public void Too_many_pages_exceed_the_limit()
    {
        var rows = string.Concat(Enumerable.Range(0, 100).Select(i => $"<Row><V>{i}</V></Row>"));
        var xdp = Form("""<subform name="Row" w="7in" h="5in"><occur min="0" max="-1"/>""" + Field("V") + "</subform>", $"<form1>{rows}</form1>");
        var limits = ResourceLimits.Default with { MaxPages = 10 };

        var e = Assert.Throws<ResourceLimitExceededException>(() => Convert(xdp, new XfaConverterOptions { Limits = limits }));
        Assert.Equal(nameof(ResourceLimits.MaxPages), e.LimitName);
    }

    [Fact]
    public void Too_many_table_cells_exceed_the_limit()
    {
        var rows = string.Concat(Enumerable.Repeat($"""<subform layout="row">{Draw("a", "1in")}{Draw("b", "1in")}</subform>""", 30));
        var xdp = Form($"""<subform layout="table" columnWidths="1in 1in">{rows}</subform>""");
        var limits = ResourceLimits.Default with { MaxTableCells = 40 };

        var e = Assert.Throws<ResourceLimitExceededException>(() => Convert(xdp, new XfaConverterOptions { Limits = limits }));
        Assert.Equal(nameof(ResourceLimits.MaxTableCells), e.LimitName);
    }

    [Fact]
    public void Xfa_packets_larger_than_the_limit_are_refused()
    {
        var pdf = XfaPdf(Form(Draw(new string('x', 5000))));
        var limits = ResourceLimits.Default with { MaxDecompressedBytes = 4000 };

        var e = Assert.Throws<ResourceLimitExceededException>(() => XfaToPdf.Convert(pdf, new XfaConverterOptions { Limits = limits }));
        Assert.Equal(nameof(ResourceLimits.MaxDecompressedBytes), e.LimitName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fonts_the_pdf_embeds_for_the_form_are_used(bool useEmbedded)
    {
        var font = Fixtures.Read("RaiCoreTestTT-Regular.ttf");
        var family = RaiCore.Pdf.Fonts.PdfFont.Load(font).FamilyName;
        var xdp = Form($"""<draw w="3in" h="0.5in"><value><text>A</text></value><font typeface="{family}"/></draw>""");
        var pdf = Reading.TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA 4 0 R /DR << /Font << /F1 5 0 R /F2 8 0 R >> >> >> /NeedsRendering true >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            Reading.TestPdf.Stream(xdp),
            "<< /Type /Font /Subtype /TrueType /BaseFont /Test /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /Test /FontFile2 7 0 R >>",
            Reading.TestPdf.Stream(Encoding.Latin1.GetString(font)),
            "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Subset /FontDescriptor 9 0 R >>",
            "<< /Type /FontDescriptor /FontName /ABCDEF+Subset /FontFile2 10 0 R >>",
            Reading.TestPdf.Stream("not a font"),
        ]);

        var diagnostics = new List<XfaDiagnostic>();
        var output = XfaToPdf.ConvertToBytes(pdf, new XfaConverterOptions { UseEmbeddedFonts = useEmbedded, Diagnostics = diagnostics.Add });
        Assert.Equal(!useEmbedded, diagnostics.Any(d => d.Kind == XfaDiagnosticKind.FontSubstituted && d.Message.Contains(family, StringComparison.Ordinal)));
        Assert.Equal(useEmbedded, Encoding.Latin1.GetString(output).Contains(family.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ArialMT", "Arial")]
    [InlineData("Arial-BoldMT", "Arial")]
    [InlineData("TimesNewRomanPSMT", "Times New Roman")]
    [InlineData("Myriad Pro", "Myriad Pro")]
    public void Typefaces_are_found_by_their_postscript_names_too(string typeface, string family)
    {
        Assert.Contains(family, RaiCore.Pdf.Xfa.Layout.XfaFonts.Names(typeface));
    }

    [Fact]
    public void Xfa_packets_larger_than_the_limit_together_are_refused()
    {
        // Three packets of about 2500 bytes each, under the limit one by one.
        var padding = $"<!--{new string('x', 2500)}-->";
        var xdp = Form(Draw("x") + padding);
        var start = xdp.IndexOf("<template", StringComparison.Ordinal);
        var end = xdp.LastIndexOf("</xdp:xdp>", StringComparison.Ordinal);
        var pdf = Reading.TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA [(preamble) 4 0 R (template) 5 0 R (postamble) 6 0 R] >> /NeedsRendering true >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            Reading.TestPdf.Stream(xdp[..start] + padding),
            Reading.TestPdf.Stream(xdp[start..end]),
            Reading.TestPdf.Stream(padding + xdp[end..]),
        ]);
        var limits = ResourceLimits.Default with { MaxDecompressedBytes = 6000 };

        var e = Assert.Throws<ResourceLimitExceededException>(() => XfaToPdf.Convert(pdf, new XfaConverterOptions { Limits = limits }));
        Assert.Equal(nameof(ResourceLimits.MaxDecompressedBytes), e.LimitName);
        Assert.NotEmpty(XfaToPdf.ConvertToBytes(pdf));
    }

    [Fact]
    public void A_prototype_cycle_does_not_hang()
    {
        var xdp = Form("""
            <proto><font id="a" use="#b" size="12pt"/><font id="b" use="#a" weight="bold"/></proto>
            <draw w="2in" h="0.3in" use="#self" id="self"><value><text>Cycle</text></value><font use="#a"/></draw>
            """);

        Assert.Contains("Cycle", Pages(Convert(xdp))[0], StringComparison.Ordinal);
    }
}
