using System.Text;
using System.Xml.Linq;
using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlMetadataTests
{
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";

    private const string Head = """
        <head>
          <title>Quarterly   report</title>
          <meta name="Author" content=" Ada  Lovelace ">
          <meta name="description" content="Sales &amp; costs">
          <meta name="keywords" content="sales, Q3">
          <meta name="generator" content="Report builder 2.1">
        </head>
        """;

    [Fact]
    public void Title_lang_and_meta_elements_become_the_document_metadata()
    {
        var pdf = HtmlToPdf.Convert($"<html lang=\"nb-NO\">{Head}<body><p>Text</p></body></html>");

        Assert.Equal("Quarterly report", pdf.Info.Title);
        Assert.Equal("Ada Lovelace", pdf.Info.Author);
        Assert.Equal("Sales & costs", pdf.Info.Subject);
        Assert.Equal("sales, Q3", pdf.Info.Keywords);
        Assert.Equal("Report builder 2.1", pdf.Info.Creator);
        Assert.Equal("nb-NO", pdf.Language);
    }

    [Fact]
    public void Options_override_the_document_metadata()
    {
        var pdf = HtmlToPdf.Convert($"<html lang=\"nb-NO\">{Head}<body><p>Text</p></body></html>", new HtmlConverterOptions
        {
            Title = "Annual report",
            Author = "Grace Hopper",
            Subject = "Everything",
            Keywords = "annual",
            Creator = "Billing",
            Language = "en-US",
        });

        Assert.Equal("Annual report", pdf.Info.Title);
        Assert.Equal("Grace Hopper", pdf.Info.Author);
        Assert.Equal("Everything", pdf.Info.Subject);
        Assert.Equal("annual", pdf.Info.Keywords);
        Assert.Equal("Billing", pdf.Info.Creator);
        Assert.Equal("en-US", pdf.Language);
    }

    [Fact]
    public void Empty_options_do_not_override()
    {
        var pdf = HtmlToPdf.Convert($"<html lang=\"nb-NO\">{Head}<body><p>Text</p></body></html>", new HtmlConverterOptions { Title = " ", Language = "" });

        Assert.Equal("Quarterly report", pdf.Info.Title);
        Assert.Equal("nb-NO", pdf.Language);
    }

    [Fact]
    public void Options_fill_in_what_the_document_lacks()
    {
        var pdf = HtmlToPdf.Convert("<p>Text</p>", new HtmlConverterOptions { Author = "Ada", Language = "fr" });

        Assert.Null(pdf.Info.Title);
        Assert.Equal("Ada", pdf.Info.Author);
        Assert.Equal("fr", pdf.Language);
    }

    [Fact]
    public void A_document_without_metadata_gets_only_the_default_creator()
    {
        var pdf = HtmlToPdf.Convert("<p>Text</p>");

        Assert.Null(pdf.Info.Title);
        Assert.Null(pdf.Info.Author);
        Assert.Null(pdf.Info.Subject);
        Assert.Null(pdf.Info.Keywords);
        Assert.Equal("RaiCore.Pdf.Html", pdf.Info.Creator);
        Assert.Null(pdf.Language);
    }

    [Theory]
    [InlineData("<html xml:lang=\"de\"><body><p>x</p></body></html>", "de")]
    [InlineData("<html lang=\"en\" xml:lang=\"de\"><body><p>x</p></body></html>", "en")]
    [InlineData("<html lang=\"\" xml:lang=\"de\"><body><p>x</p></body></html>", null)]
    [InlineData("<html><head><meta http-equiv=\"Content-Language\" content=\"sv, en\"></head><body><p>x</p></body></html>", "sv")]
    [InlineData("<html lang=\"fi\"><head><meta http-equiv=\"content-language\" content=\"sv\"></head><body><p>x</p></body></html>", "fi")]
    [InlineData("<html><body lang=\"it\"><p>x</p></body></html>", null)]
    public void Language_comes_from_the_root_element(string html, string? expected) =>
        Assert.Equal(expected, HtmlToPdf.Convert(html).Language);

    [Fact]
    public void Xhtml_xml_lang_is_read()
    {
        var pdf = HtmlToPdf.Convert("<?xml version=\"1.0\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xml:lang=\"pt-BR\"><head><title>T</title></head><body><p>x</p></body></html>");

        Assert.Equal("pt-BR", pdf.Language);
    }

    [Fact]
    public void Converted_pdf_has_lang_and_xmp()
    {
        var bytes = HtmlToPdf.ConvertToBytes($"<html lang=\"nb-NO\">{Head}<body><p>Text</p></body></html>");
        var xmp = PdfMetadataReader.Xmp(bytes);
        var info = PdfMetadataReader.Info(bytes);

        Assert.Contains("/Lang (nb-NO)", PdfMetadataReader.Catalog(Encoding.Latin1.GetString(bytes)), StringComparison.Ordinal);
        Assert.Equal("Quarterly report", info["Title"]);
        Assert.Equal("Quarterly report", PdfMetadataTests.Alt(xmp, Dc + "title"));
        Assert.Equal(["Ada Lovelace"], PdfMetadataTests.Items(xmp, Dc + "creator", "Seq"));
        Assert.Equal("Sales & costs", PdfMetadataTests.Alt(xmp, Dc + "description"));
        Assert.Equal(["sales", "Q3"], PdfMetadataTests.Items(xmp, Dc + "subject", "Bag"));
        Assert.Equal("sales, Q3", PdfMetadataTests.Value(xmp, Pdf + "Keywords"));
        Assert.Equal("Report builder 2.1", PdfMetadataTests.Value(xmp, Xmp + "CreatorTool"));
        Assert.Equal(["nb-NO"], PdfMetadataTests.Items(xmp, Dc + "language", "Bag"));
    }
}
