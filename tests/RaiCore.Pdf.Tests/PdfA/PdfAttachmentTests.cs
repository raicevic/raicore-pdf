using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using RaiCore.Pdf.Filters;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf.Tests.PdfA;

/// <summary>Attached files (embedded files and associated files) and Factur-X / ZUGFeRD e-invoices.</summary>
public class PdfAttachmentTests
{
    private static readonly DateTimeOffset Modified = new(2026, 11, 10, 9, 30, 0, TimeSpan.FromHours(1));
    private static readonly XNamespace Fx = "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#";
    private static readonly XNamespace PdfASchema = "http://www.aiim.org/pdfa/ns/schema#";
    private static readonly XNamespace PdfAProperty = "http://www.aiim.org/pdfa/ns/property#";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    internal static byte[] InvoiceXml => Fixtures.Read("Invoices/factur-x-en16931.xml");

    internal static PdfDocument Invoice(FacturXProfile profile = FacturXProfile.EN16931)
    {
        var doc = new PdfDocument().EmbedStandardFonts();
        doc.Info.CreationDate = PdfASamples.Created;
        doc.Info.Title = "Invoice RC-2026-0042";
        var page = doc.AddPage();
        page.Canvas.DrawText("Invoice RC-2026-0042", StandardFont.HelveticaBold, 18, 72, page.Height - 72);
        page.Canvas.DrawText("Archiving service: EUR 100.00 + 25% VAT = EUR 125.00", StandardFont.Helvetica, 11, 72, page.Height - 100);
        return doc.AttachFacturXInvoice(InvoiceXml, profile);
    }

    [Fact]
    public void Attachments_round_trip_with_their_file_specification()
    {
        var data = Encoding.UTF8.GetBytes("id;amount\n1;100.00\n");
        var doc = new PdfDocument();
        doc.AddPage().Canvas.DrawText("With an attachment", StandardFont.Helvetica, 12, 72, 720);
        doc.Attachments.Add(new PdfAttachment("bel\u00F8b.csv", data, "text/csv")
        {
            Description = "The amounts",
            Relationship = PdfAttachmentRelationship.Data,
            ModificationDate = Modified,
            CreationDate = Modified.AddDays(-1),
        });
        var reader = PdfReader.Open(doc.ToArray());

        var tree = (PdfDictionary)reader.Resolve(((PdfDictionary)reader.Resolve(reader.Catalog["Names"])!)["EmbeddedFiles"])!;
        var names = ((PdfArray)tree["Names"]!).Items;
        Assert.Equal(2, names.Count);
        Assert.Equal(PdfString.FromText("bel\u00F8b.csv").Bytes.ToArray(), ((PdfString)names[0]).Bytes.ToArray());

        var spec = (PdfDictionary)reader.Resolve(names[1])!;
        Assert.Equal("Filespec", Name(spec["Type"]));
        Assert.Equal("bel\u00F8b.csv", Text(spec["UF"]));
        Assert.NotNull(spec["F"]);
        Assert.Equal("The amounts", Text(spec["Desc"]));
        Assert.Equal("Data", Name(spec["AFRelationship"]));

        // The document's associated files list the same file specification.
        var associated = (PdfArray)reader.Resolve(reader.Catalog["AF"])!;
        Assert.Same(spec, reader.Resolve(Assert.Single(associated.Items)));

        var ef = (PdfDictionary)spec["EF"]!;
        var file = (PdfStreamObject)reader.Resolve(ef["F"])!;
        Assert.Same(file, reader.Resolve(ef["UF"]));
        Assert.Equal("EmbeddedFile", Name(file.Dictionary["Type"]));
        Assert.Equal("text/csv", Name(file.Dictionary["Subtype"]));
        Assert.Equal(data, StreamDecoder.Decode(file, reader.Resolve));

        var parameters = (PdfDictionary)file.Dictionary["Params"]!;
        Assert.Equal(data.Length, ((PdfInteger)parameters["Size"]!).Value);
        Assert.Equal("D:20261110093000+01'00'", Text(parameters["ModDate"]));
        Assert.Equal("D:20261109093000+01'00'", Text(parameters["CreationDate"]));
        Assert.Equal(MD5.HashData(data), ((PdfString)parameters["CheckSum"]!).Bytes.ToArray());
    }

    [Fact]
    public void Several_attachments_are_listed_in_name_order_and_keep_named_destinations()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.NamedDestinations["top"] = new PdfDestination(page);
        doc.Attachments.Add(new PdfAttachment("b.txt", "B"u8));
        doc.Attachments.Add(new PdfAttachment("a.txt", "A"u8));
        var reader = PdfReader.Open(doc.ToArray());

        var names = (PdfDictionary)reader.Resolve(reader.Catalog["Names"])!;
        Assert.NotNull(names["Dests"]);
        var keys = ((PdfArray)((PdfDictionary)reader.Resolve(names["EmbeddedFiles"])!)["Names"]!).Items.Where((_, i) => i % 2 == 0);
        Assert.Equal(["a.txt", "b.txt"], keys.Select(Text));
        Assert.Equal(2, ((PdfArray)reader.Resolve(reader.Catalog["AF"])!).Items.Count); // In the order added.

        // Default MIME type, relationship and modification date (the document's).
        var spec = (PdfDictionary)reader.Resolve(((PdfArray)reader.Resolve(reader.Catalog["AF"])!).Items[0])!;
        Assert.Equal("Unspecified", Name(spec["AFRelationship"]));
        Assert.Null(spec["Desc"]);
        var file = (PdfStreamObject)reader.Resolve(((PdfDictionary)spec["EF"]!)["F"])!;
        Assert.Equal("application/octet-stream", Name(file.Dictionary["Subtype"]));
    }

    [Fact]
    public void Without_attachments_there_is_no_name_tree_or_associated_files()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        var reader = PdfReader.Open(doc.ToArray());

        Assert.Null(reader.Catalog["Names"]);
        Assert.Null(reader.Catalog["AF"]);
    }

    [Fact]
    public void Attachment_names_must_be_unique()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        doc.Attachments.Add(new PdfAttachment("data.xml", "<a/>"u8, "text/xml"));
        doc.Attachments.Add(new PdfAttachment("data.xml", "<b/>"u8, "text/xml"));

        Assert.Contains("data.xml", Assert.Throws<InvalidOperationException>(() => doc.ToArray()).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("text/")]
    [InlineData("text/xml; charset=utf-8")]
    [InlineData("text/x ml")]
    public void Mime_types_must_be_type_and_subtype(string mimeType)
    {
        Assert.Throws<ArgumentException>(() => new PdfAttachment("a.xml", "<a/>"u8, mimeType));
    }

    [Fact]
    public void PdfA2b_rejects_attachments_and_PdfA3b_allows_them()
    {
        var doc = new PdfDocument { PdfAConformance = PdfAConformance.PdfA2B }.EmbedStandardFonts();
        doc.AddPage().Canvas.DrawText("Attachment", StandardFont.Helvetica, 12, 72, 720);
        doc.Attachments.Add(new PdfAttachment("notes.txt", "Notes"u8, "text/plain"));

        Assert.Contains("PDF/A-3", Assert.Throws<InvalidOperationException>(() => doc.ToArray()).Message, StringComparison.Ordinal);

        doc.PdfAConformance = PdfAConformance.PdfA3B;
        Assert.NotNull(PdfReader.Open(doc.ToArray()).Catalog["AF"]);
    }

    [Theory]
    [InlineData(FacturXProfile.Minimum, "MINIMUM", "factur-x.xml", "Data")]
    [InlineData(FacturXProfile.BasicWL, "BASIC WL", "factur-x.xml", "Data")]
    [InlineData(FacturXProfile.Basic, "BASIC", "factur-x.xml", "Alternative")]
    [InlineData(FacturXProfile.EN16931, "EN 16931", "factur-x.xml", "Alternative")]
    [InlineData(FacturXProfile.Extended, "EXTENDED", "factur-x.xml", "Alternative")]
    [InlineData(FacturXProfile.XRechnung, "XRECHNUNG", "xrechnung.xml", "Alternative")]
    public void A_FacturX_invoice_is_a_PdfA3b_with_the_xml_and_its_metadata(FacturXProfile profile, string level, string fileName, string relationship)
    {
        var doc = Invoice(profile);
        Assert.Equal(PdfAConformance.PdfA3B, doc.PdfAConformance);
        var bytes = doc.ToArray();
        var reader = PdfReader.Open(bytes);

        var spec = (PdfDictionary)reader.Resolve(Assert.Single(((PdfArray)reader.Resolve(reader.Catalog["AF"])!).Items))!;
        Assert.Equal(fileName, Text(spec["UF"]));
        Assert.Equal(fileName, Text(spec["F"]));
        Assert.Equal(relationship, Name(spec["AFRelationship"]));
        var file = (PdfStreamObject)reader.Resolve(((PdfDictionary)spec["EF"]!)["F"])!;
        Assert.Equal("text/xml", Name(file.Dictionary["Subtype"]));
        Assert.Equal(InvoiceXml, StreamDecoder.Decode(file, reader.Resolve));

        var xmp = PdfMetadataReader.Xmp(bytes);
        Assert.Equal("INVOICE", xmp.Descendants(Fx + "DocumentType").Single().Value);
        Assert.Equal(fileName, xmp.Descendants(Fx + "DocumentFileName").Single().Value);
        Assert.Equal("1.0", xmp.Descendants(Fx + "Version").Single().Value);
        Assert.Equal(level, xmp.Descendants(Fx + "ConformanceLevel").Single().Value);
        Assert.Equal("3", xmp.Descendants(XNamespace.Get("http://www.aiim.org/pdfa/ns/id/") + "part").Single().Value);

        // The fx: properties are described by a PDF/A extension schema with the prefix fx.
        var schema = xmp.Descendants(PdfASchema + "namespaceURI").Single(e => e.Value == Fx.NamespaceName).Parent!;
        Assert.Equal("fx", schema.Element(PdfASchema + "prefix")!.Value);
        Assert.Equal(
            ["DocumentFileName", "DocumentType", "Version", "ConformanceLevel"],
            schema.Element(PdfASchema + "property")!.Element(Rdf + "Seq")!.Elements(Rdf + "li").Select(li => li.Element(PdfAProperty + "name")!.Value));
    }

    [Fact]
    public void The_fx_schema_is_described_next_to_the_custom_properties()
    {
        var doc = Invoice();
        doc.Info.CustomProperties["Order"] = "PO-7";
        var xmp = PdfMetadataReader.Xmp(doc.ToArray());

        Assert.Equal(
            ["http://ns.adobe.com/pdfx/1.3/", Fx.NamespaceName],
            xmp.Descendants(PdfASchema + "namespaceURI").Select(e => e.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Invalid_invoice_xml_is_rejected()
    {
        var doc = new PdfDocument();
        Assert.Throws<ArgumentException>(() => doc.AttachFacturXInvoice("<rsm:CrossIndustryInvoice"u8.ToArray(), FacturXProfile.Basic));
        Assert.Throws<ArgumentException>(() => doc.AttachFacturXInvoice("<Invoice xmlns='urn:oasis:names:specification:ubl:schema:xsd:Invoice-2'/>"u8.ToArray(), FacturXProfile.XRechnung));
        const string Cii = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";

        // The right local name in another namespace.
        Assert.Throws<ArgumentException>(() => doc.AttachFacturXInvoice(Encoding.UTF8.GetBytes("<CrossIndustryInvoice xmlns='urn:example'/>"), FacturXProfile.Basic));

        // A CII root followed by malformed content.
        Assert.Throws<ArgumentException>(() => doc.AttachFacturXInvoice(
            Encoding.UTF8.GetBytes($"<rsm:CrossIndustryInvoice xmlns:rsm='{Cii}'><a></b></rsm:CrossIndustryInvoice>"), FacturXProfile.Basic));

        // A DTD (refused: entities could expand), even before a valid root.
        Assert.Throws<ArgumentException>(() => doc.AttachFacturXInvoice(
            Encoding.UTF8.GetBytes($"<!DOCTYPE rsm:CrossIndustryInvoice [<!ENTITY e 'e'>]><rsm:CrossIndustryInvoice xmlns:rsm='{Cii}'/>"), FacturXProfile.Basic));

        // The same root without those problems is accepted.
        new PdfDocument().AttachFacturXInvoice(Encoding.UTF8.GetBytes($"<rsm:CrossIndustryInvoice xmlns:rsm='{Cii}'><a/></rsm:CrossIndustryInvoice>"), FacturXProfile.Basic);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.AttachFacturXInvoice(InvoiceXml, (FacturXProfile)42));
        Assert.Empty(doc.Attachments);
    }

    [Fact]
    public void An_invoice_needs_PdfA3_and_is_attached_once()
    {
        var pdfA2 = new PdfDocument { PdfAConformance = PdfAConformance.PdfA2B };
        Assert.Throws<InvalidOperationException>(() => pdfA2.AttachFacturXInvoice(InvoiceXml, FacturXProfile.Basic));

        var doc = Invoice();
        Assert.Throws<InvalidOperationException>(() => doc.AttachFacturXInvoice(InvoiceXml, FacturXProfile.Basic));
        Assert.Single(doc.Attachments);
    }

    private static string Name(PdfObject? value) => ((PdfName)value!).Value;

    private static string Text(PdfObject? value)
    {
        var bytes = ((PdfString)value!).Bytes;
        return bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
            ? Encoding.BigEndianUnicode.GetString(bytes[2..])
            : Encoding.Latin1.GetString(bytes);
    }
}
