namespace RaiCore.Pdf.Xmp;

/// <summary>A property in a PDF/A extension schema: its name, XMP value type (e.g. <c>Text</c>), category and description.</summary>
/// <param name="Name">The property's local name.</param>
/// <param name="ValueType">An XMP value type, such as <c>Text</c>, <c>Date</c> or <c>URI</c>.</param>
/// <param name="Category"><c>external</c> (shown to users) or <c>internal</c> (managed by software).</param>
/// <param name="Description">What the property holds.</param>
internal sealed record PdfAExtensionProperty(string Name, string ValueType, string Category, string Description);

/// <summary>
/// PDF/A extension schemas (ISO 19005-2, 6.6.2.3): every XMP property outside the schemas PDF/A predefines must be
/// described in the packet's <c>pdfaExtension:schemas</c> bag, one structure per namespace with its properties.
/// </summary>
internal static class PdfAExtensionSchema
{
    /// <summary>Adds the description of the schema <paramref name="ns"/> to the packet's extension schemas.</summary>
    public static void Add(XmpMetadata xmp, XmpNamespace ns, string description, IEnumerable<PdfAExtensionProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(xmp);
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(properties);

        var schema = new XmpStruct(
            new XmpProperty(XmpNamespace.PdfASchema, "schema", description),
            new XmpProperty(XmpNamespace.PdfASchema, "namespaceURI", ns.Uri),
            new XmpProperty(XmpNamespace.PdfASchema, "prefix", ns.Prefix),
            new XmpProperty(XmpNamespace.PdfASchema, "property", XmpArray.Seq(properties.Select(p => (XmpValue)new XmpStruct(
                new XmpProperty(XmpNamespace.PdfAProperty, "name", p.Name),
                new XmpProperty(XmpNamespace.PdfAProperty, "valueType", p.ValueType),
                new XmpProperty(XmpNamespace.PdfAProperty, "category", p.Category),
                new XmpProperty(XmpNamespace.PdfAProperty, "description", p.Description))))));

        var container = xmp.Schema(XmpNamespace.PdfAExtension);
        var existing = container.Properties.FirstOrDefault(p => p.Name == "schemas")?.Value as XmpArray;
        container.Set("schemas", XmpArray.Bag([.. existing?.Items ?? [], schema]));
    }
}
