using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Email;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Reading;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Slides;
using RaiCore.Pdf.Tests.Slides;
using RaiCore.Pdf.Tests.WordDocuments;

namespace RaiCore.Pdf.Tests;

/// <summary>
/// No converter reaches a file or the network because of what a document references: every kind of external
/// reference points at a real local image here, and none of them may end up in the PDF.
/// </summary>
public sealed class ExternalResourcesTests : IDisposable
{
    private const string PixelBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly string _png = Path.Combine(Path.GetTempPath(), $"raicore-external-{Guid.NewGuid():N}.png");

    public ExternalResourcesTests() => File.WriteAllBytes(_png, Convert.FromBase64String(PixelBase64));

    /// <summary>The local image as a <c>file:</c> URL.</summary>
    private string FileUrl => new Uri(_png).AbsoluteUri;

    public void Dispose() => File.Delete(_png);

    private static int Images(byte[] pdf)
    {
        return new ImageExtractor(PdfReader.Open(pdf)).ExtractPages().Sum(p => p.Images.Count);
    }

    private static string Text(byte[] pdf) => new TextExtractor(PdfReader.Open(pdf)).ExtractText();

    [Fact]
    public void Html_with_the_None_loader_shows_data_uri_images_and_loads_nothing_else()
    {
        var html = $"<link rel=\"stylesheet\" href=\"{FileUrl}.css\"><style>@font-face {{ font-family: F; src: url({FileUrl}.woff) }} p {{ font-family: F }}</style>"
            + $"<p>x</p><img src=\"data:image/png;base64,{PixelBase64}\"><img src=\"{FileUrl}\"><img src=\"{Path.GetFileName(_png)}\"><img src=\"https://example.com/a.png\">";
        var baseUri = new Uri(Path.GetDirectoryName(_png) + Path.DirectorySeparatorChar);
        var diagnostics = new List<HtmlDiagnostic>();

        var pdf = HtmlToPdf.ConvertToBytes(html, new HtmlConverterOptions { ResourceLoader = ResourceLoaders.None, BaseUri = baseUri, Diagnostics = diagnostics.Add });

        Assert.Equal(1, Images(pdf)); // Only the data: URI.
        var refused = diagnostics.Where(d => d.Kind == HtmlDiagnosticKind.ResourceNotLoaded).Select(d => d.Message).ToList();
        Assert.Equal(5, refused.Count); // Style sheet, font, file: image, relative image, https: image.

        // The default loader does read the local file, twice (absolute and relative): what None is for.
        Assert.Equal(3, Images(HtmlToPdf.ConvertToBytes(html, new HtmlConverterOptions { BaseUri = baseUri })));
    }

    [Fact]
    public void None_refuses_every_request()
    {
        Assert.Null(ResourceLoaders.None(new ResourceRequest(new Uri(FileUrl), ResourceKind.Image)));
        Assert.Null(ResourceLoaders.None(new ResourceRequest(new Uri("https://example.com/a.css"), ResourceKind.StyleSheet)));
        Assert.Throws<ArgumentNullException>(() => ResourceLoaders.None(null!));
    }

    [Fact]
    public void Email_with_the_None_loader_still_shows_the_images_the_message_carries()
    {
        var message = Encoding.ASCII.GetBytes(
            "Content-Type: multipart/related; boundary=b\r\n\r\n"
            + "--b\r\nContent-Type: text/html\r\n\r\n"
            + $"<p>Hi</p><img src=\"cid:logo@x\"><img src=\"{FileUrl}\"><img src=\"https://example.com/track.png\">\r\n"
            + $"--b\r\nContent-Type: image/png\r\nContent-ID: <logo@x>\r\nContent-Transfer-Encoding: base64\r\n\r\n{PixelBase64}\r\n--b--\r\n");

        var pdf = EmailToPdf.ConvertToBytes(message, new EmailConverterOptions { Html = new HtmlConverterOptions { ResourceLoader = ResourceLoaders.None } });

        Assert.Equal(1, Images(pdf));
    }

    [Fact]
    public void Docx_does_not_follow_external_image_relationships_or_includepicture_fields()
    {
        var drawing = "<w:p><w:r><w:drawing><wp:inline><wp:extent cx=\"914400\" cy=\"914400\"/><wp:docPr id=\"1\" name=\"p\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">"
            + "<pic:pic><pic:blipFill><a:blip r:embed=\"rX\"/></pic:blipFill></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";
        var linked = drawing.Replace("r:embed", "r:link", StringComparison.Ordinal);
        var vml = "<w:p><w:r><w:pict><v:shape xmlns:v=\"urn:schemas-microsoft-com:vml\" style=\"width:72pt;height:72pt\"><v:imagedata r:id=\"rX\"/></v:shape></w:pict></w:r></w:p>";
        var fields = $"<w:p><w:fldSimple w:instr=\" INCLUDEPICTURE &quot;{FileUrl}&quot; \\d \"><w:r><w:t>SimpleResult</w:t></w:r></w:fldSimple></w:p>"
            + $"<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> INCLUDEPICTURE \"{FileUrl}\" \\d </w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>ComplexResult</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>";
        var docx = Docx.Document(drawing + linked + vml + fields, relationships: [("rX", "image", FileUrl, true)]);

        var (pdf, diagnostics) = Docx.Convert(docx);
        var bytes = pdf.ToArray();

        Assert.Equal(0, Images(bytes));
        Assert.Contains("SimpleResult", Text(bytes), StringComparison.Ordinal);
        Assert.Contains("ComplexResult", Text(bytes), StringComparison.Ordinal);
        Assert.Contains(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded);
    }

    [Fact]
    public void Rtf_does_not_follow_includepicture_fields()
    {
        var url = FileUrl.Replace("\\", "\\\\", StringComparison.Ordinal);
        var (pdf, _) = Rtf.Convert(Rtf.Document($"{{\\field{{\\*\\fldinst INCLUDEPICTURE \"{url}\" \\\\d}}{{\\fldrslt FieldResult}}}}\\par"));
        var bytes = pdf.ToArray();

        Assert.Equal(0, Images(bytes));
        Assert.Contains("FieldResult", Text(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Odt_does_not_load_linked_images()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var odt = Odt.Document(Odt.P($"<draw:frame text:anchor-type=\"as-char\" svg:width=\"2cm\" svg:height=\"2cm\"><draw:image xlink:href=\"{FileUrl}\"/></draw:frame>after"));

        var pdf = OdtToPdf.Convert(odt, new OdtConverterOptions { Diagnostics = diagnostics.Add }).ToArray();

        Assert.Equal(0, Images(pdf));
        Assert.Contains("after", Text(pdf), StringComparison.Ordinal);
        Assert.Contains(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded);
    }

    [Fact]
    public void Pptx_does_not_load_linked_pictures()
    {
        var reports = new List<SlideDiagnostic>();
        var picture = "<p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"Pic\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:link=\"rL\"/></p:blipFill><p:spPr>"
            + Deck.Xfrm(10, 20, 30, 40) + "</p:spPr></p:pic>";
        var deck = new Deck { SlideRelationships = $"<Relationship Id=\"rL\" Type=\"{Deck.R}/image\" Target=\"{FileUrl}\" TargetMode=\"External\"/>" }.With(picture);

        var pdf = PptxToPdf.Convert(deck.Build(), new PptxConverterOptions { Diagnostics = reports.Add }).ToArray();

        Assert.Equal(0, Images(pdf));
        Assert.Contains(reports, r => r.Message.Contains("linked", StringComparison.Ordinal));
    }

    [Fact]
    public void Xlsx_shows_cached_values_of_external_links_and_never_opens_the_linked_workbook_or_connections()
    {
        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string Package = "http://schemas.openxmlformats.org/package/2006/relationships";
        var linked = FileUrl.Replace(".png", ".xlsx", StringComparison.Ordinal);
        var xlsx = Zip(
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            ("_rels/.rels", $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"rId1\" Type=\"{R}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets>"
                + "<externalReferences><externalReference r:id=\"rId2\"/></externalReferences></workbook>"),
            ("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"rId1\" Type=\"{R}/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + $"<Relationship Id=\"rId2\" Type=\"{R}/externalLink\" Target=\"externalLinks/externalLink1.xml\"/>"
                + $"<Relationship Id=\"rId3\" Type=\"{R}/connections\" Target=\"connections.xml\"/></Relationships>"),
            ("xl/externalLinks/externalLink1.xml", $"<externalLink xmlns=\"{Main}\" xmlns:r=\"{R}\"><externalBook r:id=\"rId1\"><sheetNames><sheetName val=\"Data\"/></sheetNames></externalBook></externalLink>"),
            ("xl/externalLinks/_rels/externalLink1.xml.rels", $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"rId1\" Type=\"{R}/externalLinkPath\" Target=\"{linked}\" TargetMode=\"External\"/></Relationships>"),
            ("xl/connections.xml", $"<connections xmlns=\"{Main}\"><connection id=\"1\" name=\"web\" type=\"4\" refreshOnLoad=\"1\"><webPr url=\"{FileUrl}\"/></connection></connections>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"A1\"><f>[1]Data!A1</f><v>4711</v></c></row></sheetData></worksheet>"));

        var pdf = XlsxToPdf.ConvertToBytes(xlsx);

        Assert.Contains("4711", Text(pdf), StringComparison.Ordinal);
        Assert.Equal(0, Images(pdf));
    }

    /// <summary>
    /// The Office converters contain no code that opens files, sockets or processes, apart from reading the input file
    /// a caller names and writing the output file a caller names: whatever a document references, they cannot reach it.
    /// The HTML converter reaches resources only through <see cref="HtmlConverterOptions.ResourceLoader"/>, and the email
    /// converter through it as well.
    /// </summary>
    [Theory]
    [InlineData(typeof(DocxToPdf))]
    [InlineData(typeof(XlsxToPdf))]
    [InlineData(typeof(PptxToPdf))]
    [InlineData(typeof(EmailToPdf))]
    public void Office_and_email_converters_have_no_file_or_network_access_of_their_own(Type converter)
    {
        Assert.Empty(ForbiddenReferences(converter.Assembly));
    }

    /// <summary>Calls into file, network and process APIs other than reading or writing a path the caller passes.</summary>
    private static List<string> ForbiddenReferences(Assembly assembly)
    {
        string[] fileTypes = ["File", "FileInfo", "FileStream", "Directory", "DirectoryInfo", "FileSystemWatcher"];

        // A path the caller passes: Convert(path) reads it; saving to a path writes it.
        string[] allowed = ["File.ReadAllBytes", "File.ReadAllBytesAsync", "File.WriteAllBytes", "File.WriteAllBytesAsync", "File.Create", "File.OpenRead"];

        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var found = new List<string>();
        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            var ns = reader.GetString(type.Namespace);
            var name = reader.GetString(type.Name);
            var full = $"{name}.{reader.GetString(member.Name)}";
            var forbidden = (ns == "System.IO" && fileTypes.Contains(name)) || ns.StartsWith("System.Net", StringComparison.Ordinal)
                || (ns == "System.Diagnostics" && name == "Process");
            if (forbidden && !allowed.Contains(full))
            {
                found.Add($"{ns}.{full}");
            }
        }

        return found;
    }

    private static byte[] Zip(params (string Name, string Content)[] parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in parts)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }
}
