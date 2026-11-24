using System.Text;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Tests.Reading;
using RaiCore.Pdf.Xfa;

namespace RaiCore.Pdf.Tests.Xfa;

/// <summary>Builds small XFA forms (XDP documents and PDF files) for tests.</summary>
internal static class Xdp
{
    public const string TemplateNamespace = "http://www.xfa.org/schema/xfa-template/3.3/";

    public const string DataNamespace = "http://www.xfa.org/schema/xfa-data/1.0/";

    /// <summary>A Letter page set with one page area and one content area (0.5 inch margins).</summary>
    public const string PageSet = """
        <pageSet><pageArea name="Page1" id="Page1"><contentArea x="0.5in" y="0.5in" w="7.5in" h="10in"/><medium stock="letter" short="8.5in" long="11in"/></pageArea></pageSet>
        """;

    /// <summary>An XDP document: a root subform <c>form1</c> with the content (and <see cref="PageSet"/> unless the content has one), and data.</summary>
    public static string Form(string content, string? data = null, string layout = "tb", string extraPackets = "")
    {
        var pageSet = content.Contains("<pageSet", StringComparison.Ordinal) ? string.Empty : PageSet;
        var datasets = data is null ? string.Empty : $"""<xfa:datasets xmlns:xfa="{DataNamespace}"><xfa:data>{data}</xfa:data></xfa:datasets>""";
        return $"""
            <xdp:xdp xmlns:xdp="http://ns.adobe.com/xdp/">
            <template xmlns="{TemplateNamespace}"><subform name="form1" layout="{layout}" locale="en_US">{pageSet}{content}</subform></template>
            {datasets}{extraPackets}
            </xdp:xdp>
            """;
    }

    /// <summary>A text field.</summary>
    public static string Field(string name, string w = "2in", string h = "0.3in", string attributes = "", string content = "") =>
        $"""<field name="{name}" w="{w}" h="{h}" {attributes}><ui><textEdit/></ui><font typeface="Arial" size="10pt"/>{content}</field>""";

    /// <summary>A text draw.</summary>
    public static string Draw(string text, string w = "2in", string h = "0.3in", string attributes = "", string content = "") =>
        $"""<draw w="{w}" h="{h}" {attributes}><value><text>{text}</text></value><font typeface="Arial" size="10pt"/>{content}</draw>""";

    /// <summary>Converts an XDP document.</summary>
    public static byte[] Convert(string xdp, XfaConverterOptions? options = null) => XfaToPdf.ConvertToBytes(Encoding.UTF8.GetBytes(xdp), options);

    /// <summary>Converts an XDP document and collects the diagnostics.</summary>
    public static (byte[] Pdf, List<XfaDiagnostic> Diagnostics) ConvertWithDiagnostics(string xdp, XfaConverterOptions? options = null)
    {
        var diagnostics = new List<XfaDiagnostic>();
        options ??= new XfaConverterOptions();
        var pdf = XfaToPdf.ConvertToBytes(Encoding.UTF8.GetBytes(xdp), new XfaConverterOptions
        {
            Data = options.Data,
            Limits = options.Limits,
            Fonts = options.Fonts,
            Diagnostics = diagnostics.Add,
        });
        return (pdf, diagnostics);
    }

    /// <summary>The text of each page.</summary>
    public static string[] Pages(byte[] pdf) => [.. new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    /// <summary>Where a text is on a page (PDF coordinates, y upwards).</summary>
    public static PdfRectangle Find(byte[] pdf, int page, string text)
    {
        var matches = new TextExtractor(PdfReader.Open(pdf)).FindOnPage(page, text);
        Assert.True(matches.Count > 0, $"'{text}' is not on page {page + 1}.");
        return matches[0].Bounds;
    }

    /// <summary>A dynamic XFA PDF with the XFA as one stream (<paramref name="array"/> false) or as an array of packets.</summary>
    public static byte[] XfaPdf(string xdp, bool array = true, string extraCatalog = "/NeedsRendering true")
    {
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA {(array ? "[(preamble) 4 0 R (template) 5 0 R (postamble) 6 0 R]" : "4 0 R")} >> {extraCatalog} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R >>",
        };
        if (array)
        {
            var start = xdp.IndexOf("<template", StringComparison.Ordinal);
            var end = xdp.LastIndexOf("</xdp:xdp>", StringComparison.Ordinal);
            objects.Add(TestPdf.Stream(xdp[..start]));
            objects.Add(TestPdf.Stream(xdp[start..end]));
            objects.Add(TestPdf.Stream(xdp[end..]));
        }
        else
        {
            objects.Add(TestPdf.Stream(xdp));
        }

        return TestPdf.Build(objects);
    }
}
