using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Xmp;

namespace RaiCore.Pdf.Tests;

public partial class PdfMetadataTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 2, 12, 30, 45, TimeSpan.FromHours(1));

    // Special XML characters, quotes, Danish letters, a CJK character and an emoji (a surrogate pair).
    private const string AwkwardText = "R&D <\"draft\"> 'Bl\u00E5b\u00E6r' \u65E5 \U0001F600";

    private static PdfDocument CreateDocument(Action<PdfDocument>? configure = null)
    {
        var doc = new PdfDocument();
        doc.Info.CreationDate = Created;
        doc.AddPage().Canvas.DrawText("Metadata", StandardFont.Helvetica, 12, 72, 720);
        configure?.Invoke(doc);
        return doc;
    }

    [Fact]
    public void Xmp_parses_as_xml_and_matches_the_info_dictionary()
    {
        var bytes = CreateDocument(doc =>
        {
            doc.Info.Title = AwkwardText;
            doc.Info.Author = "\u00C6blegr\u00F8d & S\u00F8n";
            doc.Info.Subject = "Line one\r\nline two";
            doc.Info.Keywords = "invoice, r\u00E9sum\u00E9; <tax>";
            doc.Info.Creator = "Unit test";
            doc.Language = "da-DK";
        }).ToArray();

        var info = PdfMetadataReader.Info(bytes);
        var xmp = PdfMetadataReader.Xmp(bytes);

        Assert.Equal(AwkwardText, info["Title"]);
        Assert.Equal(AwkwardText, Alt(xmp, Dc + "title"));
        Assert.Equal(info["Author"], Assert.Single(Items(xmp, Dc + "creator", "Seq")));
        Assert.Equal("Line one\r\nline two", info["Subject"]);
        Assert.Equal(info["Subject"], Alt(xmp, Dc + "description"));
        Assert.Equal(info["Keywords"], Value(xmp, Pdf + "Keywords"));
        Assert.Equal(["invoice", "r\u00E9sum\u00E9", "<tax>"], Items(xmp, Dc + "subject", "Bag"));
        Assert.Equal(info["Creator"], Value(xmp, XmpNs + "CreatorTool"));
        Assert.Equal("RaiCore.Pdf", info["Producer"]);
        Assert.Equal(info["Producer"], Value(xmp, Pdf + "Producer"));
        Assert.Equal("application/pdf", Value(xmp, Dc + "format"));
        Assert.Equal(["da-DK"], Items(xmp, Dc + "language", "Bag"));
    }

    [Theory]
    [InlineData(1, 0, "D:20261102123045+01'00'", "2026-11-02T12:30:45+01:00")]
    [InlineData(0, 0, "D:20261102123045+00'00'", "2026-11-02T12:30:45+00:00")]
    [InlineData(-5, -30, "D:20261102123045-05'30'", "2026-11-02T12:30:45-05:30")]
    public void Dates_are_the_same_in_info_and_xmp(int hours, int minutes, string pdfDate, string xmpDate)
    {
        var created = new DateTimeOffset(2026, 11, 2, 12, 30, 45, 678, new TimeSpan(hours, minutes, 0));
        var bytes = CreateDocument(doc =>
        {
            doc.Info.CreationDate = created;
            doc.Info.ModificationDate = created.AddDays(3);
        }).ToArray();

        var info = PdfMetadataReader.Info(bytes);
        var xmp = PdfMetadataReader.Xmp(bytes);

        Assert.Equal(pdfDate, info["CreationDate"]);
        Assert.Equal(xmpDate, Value(xmp, XmpNs + "CreateDate"));
        Assert.Equal(pdfDate.Replace("20261102", "20261105", StringComparison.Ordinal), info["ModDate"]);
        Assert.Equal(xmpDate.Replace("11-02", "11-05", StringComparison.Ordinal), Value(xmp, XmpNs + "ModifyDate"));
        Assert.Equal(Value(xmp, XmpNs + "ModifyDate"), Value(xmp, XmpNs + "MetadataDate"));
    }

    [Fact]
    public void Default_dates_are_one_moment_in_info_and_xmp()
    {
        var doc = CreateDocument(d => d.Info.CreationDate = null);
        var before = DateTimeOffset.Now.AddSeconds(-1);
        var bytes = doc.ToArray();
        var after = DateTimeOffset.Now.AddSeconds(1);

        var info = PdfMetadataReader.Info(bytes);
        var xmp = PdfMetadataReader.Xmp(bytes);
        var created = DateTimeOffset.Parse(Value(xmp, XmpNs + "CreateDate")!, CultureInfo.InvariantCulture);

        Assert.InRange(created, before, after);
        Assert.Equal(PdfDocumentInfo.FormatDate(created), info["CreationDate"]);
        Assert.Equal(info["CreationDate"], info["ModDate"]);
        Assert.Equal(Value(xmp, XmpNs + "CreateDate"), Value(xmp, XmpNs + "ModifyDate"));
    }

    [Fact]
    public void Metadata_stream_is_uncompressed_and_referenced_from_the_catalog()
    {
        var bytes = CreateDocument(doc => doc.Info.Title = "Compressed content").ToArray();
        var text = Encoding.Latin1.GetString(bytes);

        var catalog = PdfMetadataReader.Catalog(text);
        var metadataRef = Regex.Match(catalog, @"/Metadata (\d+) 0 R");
        Assert.True(metadataRef.Success, catalog);

        var obj = PdfMetadataReader.Object(text, int.Parse(metadataRef.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.StartsWith("<</Type /Metadata/Subtype /XML/Length ", obj, StringComparison.Ordinal);
        Assert.DoesNotContain("/Filter", obj[..obj.IndexOf(">>", StringComparison.Ordinal)], StringComparison.Ordinal);

        // The content streams are compressed; the XMP is plain text in the file, inside its xpacket wrapper.
        Assert.Contains("/FlateDecode", text, StringComparison.Ordinal);
        Assert.Contains("<rdf:li xml:lang=\"x-default\">Compressed content</rdf:li>", text, StringComparison.Ordinal);

        var packet = PdfMetadataReader.XmpBytes(bytes);
        Assert.StartsWith("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>", Encoding.UTF8.GetString(packet), StringComparison.Ordinal);
        Assert.Equal([0xEF, 0xBB, 0xBF], packet.AsSpan(17, 3).ToArray()); // The begin attribute is a UTF-8 byte order mark.
        Assert.EndsWith("<?xpacket end=\"w\"?>", Encoding.UTF8.GetString(packet), StringComparison.Ordinal);
        var meta = Encoding.UTF8.GetString(packet);
        var padding = meta[(meta.IndexOf("</x:xmpmeta>", StringComparison.Ordinal) + 12)..meta.IndexOf("<?xpacket end", StringComparison.Ordinal)];
        Assert.True(string.IsNullOrWhiteSpace(padding) && padding.Length >= 2000, $"Padding of {padding.Length} characters.");
    }

    [Fact]
    public void Xmp_can_be_left_out()
    {
        var text = Encoding.Latin1.GetString(CreateDocument(doc => doc.IncludeXmpMetadata = false).ToArray());

        Assert.DoesNotContain("/Metadata", text, StringComparison.Ordinal);
        Assert.DoesNotContain("xpacket", text, StringComparison.Ordinal);
        Assert.Contains("/CreationDate", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Language_is_written_as_catalog_lang_and_dc_language()
    {
        var bytes = CreateDocument(doc => doc.Language = " en-GB ").ToArray();

        Assert.Contains("/Lang (en-GB)", PdfMetadataReader.Catalog(Encoding.Latin1.GetString(bytes)), StringComparison.Ordinal);
        Assert.Equal(["en-GB"], Items(PdfMetadataReader.Xmp(bytes), Dc + "language", "Bag"));
    }

    [Fact]
    public void Without_a_language_there_is_no_lang()
    {
        var bytes = CreateDocument().ToArray();

        Assert.DoesNotContain("/Lang", PdfMetadataReader.Catalog(Encoding.Latin1.GetString(bytes)), StringComparison.Ordinal);
        Assert.Null(PdfMetadataReader.Xmp(bytes).Descendants(Dc + "language").FirstOrDefault());
    }

    [Fact]
    public void Empty_fields_are_left_out_of_info_and_xmp()
    {
        var bytes = CreateDocument(doc => doc.Info.Title = "").ToArray();
        var info = PdfMetadataReader.Info(bytes);
        var xmp = PdfMetadataReader.Xmp(bytes);

        Assert.False(info.ContainsKey("Title"));
        Assert.Empty(xmp.Descendants(Dc + "title"));
        Assert.Empty(xmp.Descendants(Dc + "creator"));
        Assert.Empty(xmp.Descendants(Dc + "subject"));
    }

    [Fact]
    public void Custom_properties_go_to_info_and_xmp()
    {
        var bytes = CreateDocument(doc =>
        {
            doc.Info.CustomProperties["Invoice number"] = "2026-0042";
            doc.Info.CustomProperties["Customer"] = AwkwardText;
            doc.Info.CustomProperties["Approved"] = "";
        }).ToArray();
        var text = Encoding.Latin1.GetString(bytes);

        var info = PdfMetadataReader.Info(bytes);
        var xmp = PdfMetadataReader.Xmp(bytes);

        Assert.Contains("/Invoice#20number (2026-0042)", text, StringComparison.Ordinal);
        Assert.Equal("2026-0042", info["Invoice number"]);
        Assert.Equal(AwkwardText, info["Customer"]);
        Assert.Equal("", info["Approved"]);
        Assert.Equal("2026-0042", Value(xmp, Pdfx + "Invoice_x0020_number"));
        Assert.Equal("Invoice number", XmlConvert.DecodeName("Invoice_x0020_number"));
        Assert.Equal(AwkwardText, Value(xmp, Pdfx + "Customer"));
        Assert.Equal("", Value(xmp, Pdfx + "Approved"));

        // Written in ordinal key order, after the standard entries.
        Assert.True(text.IndexOf("/Approved", StringComparison.Ordinal) < text.IndexOf("/Customer", StringComparison.Ordinal));
        Assert.True(text.IndexOf("/ModDate", StringComparison.Ordinal) < text.IndexOf("/Approved", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("ModDate")]
    [InlineData("")]
    public void Custom_properties_cannot_use_standard_or_empty_keys(string key)
    {
        var doc = CreateDocument(d => d.Info.CustomProperties[key] = "x");

        Assert.Throws<InvalidOperationException>(doc.ToArray);
    }

    [Fact]
    public void Output_with_metadata_is_deterministic() =>
        Assert.Equal(
            CreateDocument(d => { d.Language = "da"; d.Info.CustomProperties["A"] = "1"; d.Info.CustomProperties["B"] = "2"; }).ToArray(),
            CreateDocument(d => { d.Language = "da"; d.Info.CustomProperties["B"] = "2"; d.Info.CustomProperties["A"] = "1"; }).ToArray());

    [Fact]
    public void Passes_qpdf_check_with_metadata() =>
        ExternalTools.AssertQpdfClean(CreateDocument(doc =>
        {
            doc.Info.Title = AwkwardText;
            doc.Language = "da";
            doc.Info.CustomProperties["Invoice number"] = "1";
        }).ToArray());

    [Fact]
    public void Writer_escapes_text_and_drops_characters_xml_cannot_hold()
    {
        var xmp = new XmpMetadata();
        xmp.Schema(XmpNamespace.DublinCore).Set("format", "a\u0001b\u0000c\uFFFEd\uD800e]]>f\tg");

        var parsed = Parse(XmpWriter.Serialize(xmp));

        Assert.Equal("abcde]]>f\tg", Value(parsed, Dc + "format"));
    }

    [Fact]
    public void Writer_writes_structures_for_extension_schemas()
    {
        // The shape of a PDF/A extension schema description: a bag of structures with fields in other namespaces,
        // one of which is a sequence of structures.
        var extension = new XmpNamespace("pdfaExtension", "http://www.aiim.org/pdfa/ns/extension/");
        var schema = new XmpNamespace("pdfaSchema", "http://www.aiim.org/pdfa/ns/schema#");
        var property = new XmpNamespace("pdfaProperty", "http://www.aiim.org/pdfa/ns/property#");
        var xmp = new XmpMetadata();
        xmp.Schema(new XmpNamespace("pdfaid", "http://www.aiim.org/pdfa/ns/id/")).Set("part", "3").Set("conformance", "B");
        xmp.Schema(extension).Set("schemas", XmpArray.Bag(new XmpStruct(
            new XmpProperty(schema, "namespaceURI", XmpNamespace.PdfExtension.Uri),
            new XmpProperty(schema, "prefix", "pdfx"),
            new XmpProperty(schema, "property", XmpArray.Seq(new XmpStruct(
                new XmpProperty(property, "name", "Invoice_x0020_number"),
                new XmpProperty(property, "valueType", "Text"),
                new XmpProperty(property, "category", "external"),
                new XmpProperty(property, "description", "Custom property")))))));

        var parsed = Parse(XmpWriter.Serialize(xmp, padding: 0));

        XNamespace pdfaid = "http://www.aiim.org/pdfa/ns/id/";
        Assert.Equal("3", Value(parsed, pdfaid + "part"));
        Assert.Equal("B", Value(parsed, pdfaid + "conformance"));
        var schemaItem = Assert.Single(parsed.Descendants((XNamespace)extension.Uri + "schemas").Single().Descendants(Rdf + "Bag").Single().Elements(Rdf + "li"));
        Assert.Equal("Resource", schemaItem.Attribute(Rdf + "parseType")?.Value);
        Assert.Equal("pdfx", schemaItem.Element((XNamespace)schema.Uri + "prefix")?.Value);
        var propertyItem = Assert.Single(schemaItem.Descendants(Rdf + "Seq").Single().Elements(Rdf + "li"));
        Assert.Equal("Invoice_x0020_number", propertyItem.Element((XNamespace)property.Uri + "name")?.Value);
    }

    [Fact]
    public void Writer_rejects_one_prefix_for_two_namespaces()
    {
        var xmp = new XmpMetadata();
        xmp.Schema(XmpNamespace.DublinCore).Set("x", new XmpStruct(new XmpProperty(new XmpNamespace("dc", "urn:other"), "y", "z")));

        Assert.Throws<InvalidOperationException>(() => XmpWriter.Serialize(xmp));
    }

    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace XmpNs = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";
    private static readonly XNamespace Pdfx = "http://ns.adobe.com/pdfx/1.3/";

    internal static XDocument Parse(byte[] packet) => PdfMetadataReader.ParseXml(packet);

    internal static string? Value(XDocument xmp, XName name) => xmp.Descendants(name).SingleOrDefault()?.Value;

    internal static string? Alt(XDocument xmp, XName name) =>
        xmp.Descendants(name).SingleOrDefault()?.Element(Rdf + "Alt")?.Elements(Rdf + "li")
            .Single(li => li.Attribute(XNamespace.Xml + "lang")?.Value == "x-default").Value;

    internal static List<string> Items(XDocument xmp, XName name, string container) =>
        [.. xmp.Descendants(name).Single().Element(Rdf + container)!.Elements(Rdf + "li").Select(li => li.Value)];
}

/// <summary>Reads the catalog, Info dictionary and XMP packet back from a PDF written by <see cref="PdfDocument"/>.</summary>
internal static partial class PdfMetadataReader
{
    public static string Catalog(string pdf)
    {
        var start = pdf.IndexOf("<</Type /Catalog", StringComparison.Ordinal);
        Assert.True(start >= 0, "No catalog.");
        return pdf[start..pdf.IndexOf("\nendobj", start, StringComparison.Ordinal)];
    }

    public static string Object(string pdf, int number)
    {
        var start = pdf.IndexOf($"\n{number} 0 obj\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No object {number}.");
        start = pdf.IndexOf('\n', start + 1) + 1;
        return pdf[start..pdf.IndexOf("\nendobj", start, StringComparison.Ordinal)];
    }

    /// <summary>The raw bytes of the metadata stream (which must not be filtered).</summary>
    public static byte[] XmpBytes(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var match = MetadataStreamRegex().Match(text);
        Assert.True(match.Success, "No uncompressed metadata stream.");
        var length = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return pdf.AsSpan(match.Index + match.Length, length).ToArray();
    }

    public static XDocument Xmp(byte[] pdf) => ParseXml(XmpBytes(pdf));

    public static XDocument ParseXml(byte[] packet)
    {
        using var reader = XmlReader.Create(new MemoryStream(packet), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    /// <summary>The Info dictionary's string entries, decoded (PDFDocEncoding as ASCII, or UTF-16BE with a BOM).</summary>
    public static Dictionary<string, string> Info(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var number = int.Parse(InfoRefRegex().Match(text).Groups[1].Value, CultureInfo.InvariantCulture);
        var dict = Object(text, number);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 2;
        while (dict[i] == '/')
        {
            var keyEnd = dict.IndexOf(' ', i);
            var key = DecodeName(dict[(i + 1)..keyEnd]);
            (result[key], i) = ReadString(dict, keyEnd + 1);
        }

        return result;
    }

    private static string DecodeName(string name) =>
        Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(NameEscapeRegex().Replace(name, m => ((char)System.Convert.ToInt32(m.Groups[1].Value, 16)).ToString())));

    private static (string Value, int End) ReadString(string dict, int i)
    {
        Assert.Equal('(', dict[i]);
        var bytes = new List<byte>();
        var depth = 0;
        for (i++; ; i++)
        {
            var c = dict[i];
            if (c == '\\')
            {
                var next = dict[++i];
                if (next is >= '0' and <= '7')
                {
                    var digits = 1;
                    while (digits < 3 && dict[i + digits] is >= '0' and <= '7')
                    {
                        digits++;
                    }

                    bytes.Add((byte)System.Convert.ToInt32(dict.Substring(i, digits), 8));
                    i += digits - 1;
                }
                else
                {
                    bytes.Add(next switch { 'n' => (byte)'\n', 'r' => (byte)'\r', 't' => (byte)'\t', _ => (byte)next });
                }

                continue;
            }

            if (c == ')' && depth-- == 0)
            {
                break;
            }

            if (c == '(')
            {
                depth++;
            }

            bytes.Add((byte)c);
        }

        var raw = bytes.ToArray();
        var value = raw is [0xFE, 0xFF, ..] ? Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2) : Encoding.ASCII.GetString(raw);
        return (value, i + 1);
    }

    [GeneratedRegex(@"<</Type /Metadata/Subtype /XML/Length (\d+)>>\nstream\n")]
    private static partial Regex MetadataStreamRegex();

    [GeneratedRegex(@"/Info (\d+) 0 R")]
    private static partial Regex InfoRefRegex();

    [GeneratedRegex("#([0-9A-Fa-f]{2})")]
    private static partial Regex NameEscapeRegex();
}
