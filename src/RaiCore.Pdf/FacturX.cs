using System.Xml;
using RaiCore.Pdf.Xmp;

namespace RaiCore.Pdf;

/// <summary>
/// The Factur-X / ZUGFeRD profile of an e-invoice: how complete its XML is. Written as <c>fx:ConformanceLevel</c>.
/// </summary>
public enum FacturXProfile
{
    /// <summary>MINIMUM: the header data needed for booking; not a full invoice in most countries.</summary>
    Minimum,

    /// <summary>BASIC WL ("without lines"): the header and totals, no invoice lines; not a full invoice in most countries.</summary>
    BasicWL,

    /// <summary>BASIC: a subset of EN 16931 with invoice lines.</summary>
    Basic,

    /// <summary>EN 16931 (COMFORT): the European standard's core invoice.</summary>
    EN16931,

    /// <summary>EXTENDED: EN 16931 plus further data for complex invoices.</summary>
    Extended,

    /// <summary>XRECHNUNG: the German CIUS of EN 16931 (ZUGFeRD 2.1.1 and later), attached as <c>xrechnung.xml</c>.</summary>
    XRechnung,
}

/// <summary>
/// Hybrid e-invoices in Factur-X 1.0 (France, Germany as ZUGFeRD 2.x): a PDF/A-3 whose visible pages are the invoice
/// and which carries the same invoice as Cross Industry Invoice (CII) XML.
/// </summary>
public static class FacturX
{
    /// <summary>The namespace of the Factur-X XMP properties (<c>fx:</c>), the same in ZUGFeRD 2.x.</summary>
    internal static readonly XmpNamespace Namespace = new("fx", "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#");

    private const string CiiNamespace = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";

    /// <summary>
    /// Makes <paramref name="document"/> a Factur-X / ZUGFeRD e-invoice: attaches <paramref name="invoiceXml"/> as
    /// <c>factur-x.xml</c> (<c>xrechnung.xml</c> for <see cref="FacturXProfile.XRechnung"/>, MIME type <c>text/xml</c>,
    /// relationship <c>Data</c> for MINIMUM and BASIC WL, <c>Alternative</c> otherwise), sets
    /// <see cref="PdfDocument.PdfAConformance"/> to PDF/A-3b, and adds the <c>fx:</c> XMP properties (document type
    /// INVOICE, file name, version 1.0, conformance level) with their PDF/A extension schema. Returns the document.
    /// </summary>
    /// <remarks>
    /// The XML must be well-formed and its root element a CII <c>rsm:CrossIndustryInvoice</c>; its content is not
    /// validated against the profile's schema or business rules (use a validator such as the official Factur-X/ZUGFeRD
    /// schematron or Mustang for that). The visible pages must still meet PDF/A: embed every font, e.g. with
    /// <c>EmbedStandardFonts()</c> from RaiCore.Pdf.Fonts.
    /// </remarks>
    /// <exception cref="ArgumentException">The XML is not well-formed, or its root is not a CII invoice.</exception>
    /// <exception cref="InvalidOperationException">The document is PDF/A-2b, or already has an e-invoice.</exception>
    public static PdfDocument AttachFacturXInvoice(this PdfDocument document, byte[] invoiceXml, FacturXProfile profile)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(invoiceXml);
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown Factur-X profile.");
        }

        CheckInvoiceXml(invoiceXml);
        if (document.PdfAConformance == PdfAConformance.PdfA2B)
        {
            throw new InvalidOperationException("A Factur-X / ZUGFeRD invoice is a PDF/A-3 document, but this one is set to PDF/A-2b.");
        }

        if (document.XmpExtensions.Exists(extension => extension.Namespace.Uri == Namespace.Uri))
        {
            throw new InvalidOperationException("The document already has a Factur-X / ZUGFeRD invoice.");
        }

        var fileName = profile == FacturXProfile.XRechnung ? "xrechnung.xml" : "factur-x.xml";
        document.Attachments.Add(new PdfAttachment(fileName, invoiceXml, "text/xml")
        {
            Description = "Factur-X / ZUGFeRD invoice",
            Relationship = profile is FacturXProfile.Minimum or FacturXProfile.BasicWL
                ? PdfAttachmentRelationship.Data
                : PdfAttachmentRelationship.Alternative,
        });
        document.PdfAConformance = PdfAConformance.PdfA3B;
        document.XmpExtensions.Add(new XmpExtension(
            Namespace,
            "Factur-X PDFA Extension Schema",
            [
                (new PdfAExtensionProperty("DocumentFileName", "Text", "external", "The name of the embedded XML document"), fileName),
                (new PdfAExtensionProperty("DocumentType", "Text", "external", "The type of the hybrid document in capital letters, e.g. INVOICE or ORDER"), "INVOICE"),
                (new PdfAExtensionProperty("Version", "Text", "external", "The actual version of the standard applying to the embedded XML document"), "1.0"),
                (new PdfAExtensionProperty("ConformanceLevel", "Text", "external", "The conformance level of the embedded XML document"), ConformanceLevel(profile)),
            ]));
        return document;
    }

    /// <summary>The <c>fx:ConformanceLevel</c> value of a profile.</summary>
    internal static string ConformanceLevel(FacturXProfile profile) => profile switch
    {
        FacturXProfile.Minimum => "MINIMUM",
        FacturXProfile.BasicWL => "BASIC WL",
        FacturXProfile.Basic => "BASIC",
        FacturXProfile.EN16931 => "EN 16931",
        FacturXProfile.Extended => "EXTENDED",
        _ => "XRECHNUNG",
    };

    private static void CheckInvoiceXml(byte[] invoiceXml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(invoiceXml), settings);
            reader.MoveToContent();
            if (reader.LocalName != "CrossIndustryInvoice" || reader.NamespaceURI != CiiNamespace)
            {
                throw new ArgumentException(
                    $"The invoice XML must be a Cross Industry Invoice (rsm:CrossIndustryInvoice in {CiiNamespace}), not {reader.Name}.", nameof(invoiceXml));
            }

            while (reader.Read())
            {
                // Reads to the end, so that malformed XML anywhere is reported.
            }
        }
        catch (XmlException ex)
        {
            throw new ArgumentException($"The invoice XML is not well-formed: {ex.Message}", nameof(invoiceXml), ex);
        }
    }
}
