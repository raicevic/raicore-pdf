using System.Globalization;
using System.Xml;

namespace RaiCore.Pdf.Xmp;

/// <summary>An XMP namespace: the prefix it is written with and its URI.</summary>
internal sealed record XmpNamespace
{
    public XmpNamespace(string prefix, string uri)
    {
        XmlConvert.VerifyNCName(prefix);
        ArgumentException.ThrowIfNullOrEmpty(uri);
        Prefix = prefix;
        Uri = uri;
    }

    public string Prefix { get; }

    public string Uri { get; }

    /// <summary>Dublin Core: title, creator, description, subject, language.</summary>
    public static readonly XmpNamespace DublinCore = new("dc", "http://purl.org/dc/elements/1.1/");

    /// <summary>XMP basic: dates and the creator tool.</summary>
    public static readonly XmpNamespace Basic = new("xmp", "http://ns.adobe.com/xap/1.0/");

    /// <summary>Adobe PDF: producer and keywords.</summary>
    public static readonly XmpNamespace AdobePdf = new("pdf", "http://ns.adobe.com/pdf/1.3/");

    /// <summary>
    /// Adobe's namespace for custom document information entries, which Acrobat keeps in sync with the Info dictionary
    /// and ExifTool reads. Not predefined by PDF/A: a PDF/A writer declares its properties in an extension schema.
    /// </summary>
    public static readonly XmpNamespace PdfExtension = new("pdfx", "http://ns.adobe.com/pdfx/1.3/");

    /// <summary>PDF/UA identification: <c>pdfuaid:part</c>, the part of ISO 14289 the document conforms to.</summary>
    public static readonly XmpNamespace PdfUAIdentification = new("pdfuaid", "http://www.aiim.org/pdfua/ns/id/");

    /// <summary>The <c>rdf:</c> namespace; reserved for the packet's own structure.</summary>
    public static readonly XmpNamespace Rdf = new("rdf", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");
}

/// <summary>A property value: simple text, an array, a language alternative or a structure.</summary>
internal abstract record XmpValue
{
    /// <summary>Simple text.</summary>
    public static implicit operator XmpValue(string text) => new XmpText(text);
}

/// <summary>A simple text value.</summary>
internal sealed record XmpText(string Value) : XmpValue;

/// <summary>The kind of an XMP array.</summary>
internal enum XmpArrayKind
{
    /// <summary>An unordered array (<c>rdf:Bag</c>).</summary>
    Bag,

    /// <summary>An ordered array (<c>rdf:Seq</c>).</summary>
    Seq,
}

/// <summary>An array of values (<c>rdf:Bag</c> or <c>rdf:Seq</c>), which may themselves be structures.</summary>
internal sealed record XmpArray(XmpArrayKind Kind, IReadOnlyList<XmpValue> Items) : XmpValue
{
    public static XmpArray Bag(params IEnumerable<XmpValue> items) => new(XmpArrayKind.Bag, [.. items]);

    public static XmpArray Seq(params IEnumerable<XmpValue> items) => new(XmpArrayKind.Seq, [.. items]);
}

/// <summary>
/// A language alternative (<c>rdf:Alt</c> of texts with <c>xml:lang</c>), such as <c>dc:title</c>. The first entry
/// should be the <c>x-default</c> one.
/// </summary>
internal sealed record XmpLanguageAlternative(IReadOnlyList<(string Language, string Text)> Entries) : XmpValue
{
    /// <summary>A language alternative with only the default (<c>x-default</c>) text.</summary>
    public static XmpLanguageAlternative Default(string text) => new([("x-default", text)]);
}

/// <summary>A structure (<c>rdf:parseType="Resource"</c>), such as the schema descriptions of a PDF/A extension schema.</summary>
internal sealed record XmpStruct(IReadOnlyList<XmpProperty> Fields) : XmpValue
{
    public XmpStruct(params XmpProperty[] fields)
        : this((IReadOnlyList<XmpProperty>)fields)
    {
    }
}

/// <summary>A named property in a namespace.</summary>
internal sealed record XmpProperty
{
    public XmpProperty(XmpNamespace ns, string name, XmpValue value)
    {
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(value);
        XmlConvert.VerifyNCName(name);
        Namespace = ns;
        Name = name;
        Value = value;
    }

    public XmpNamespace Namespace { get; }

    public string Name { get; }

    public XmpValue Value { get; }
}

/// <summary>The properties of one schema (namespace), written as one <c>rdf:Description</c>.</summary>
internal sealed class XmpSchema(XmpNamespace ns)
{
    private readonly List<XmpProperty> _properties = [];

    public XmpNamespace Namespace { get; } = ns;

    public IReadOnlyList<XmpProperty> Properties => _properties;

    /// <summary>Sets a property, replacing one with the same name. Returns this schema for chaining.</summary>
    public XmpSchema Set(string name, XmpValue value)
    {
        var property = new XmpProperty(Namespace, name, value);
        var index = _properties.FindIndex(p => p.Name == name);
        if (index >= 0)
        {
            _properties[index] = property;
        }
        else
        {
            _properties.Add(property);
        }

        return this;
    }

    /// <summary>Removes a property if present.</summary>
    public void Remove(string name) => _properties.RemoveAll(p => p.Name == name);
}

/// <summary>
/// An XMP metadata packet (ISO 16684-1) as a list of schemas, written by <see cref="XmpWriter"/>. The document's
/// metadata is built by <c>PdfDocument.CreateXmpMetadata</c>; standards add their own schemas to it before it is
/// written, e.g. PDF/A identification (<c>pdfaid:part</c>, <c>pdfaid:conformance</c>), PDF/UA identification
/// (<c>pdfuaid:part</c>) and PDF/A extension schemas (<c>pdfaExtension:schemas</c>, a bag of
/// <see cref="XmpStruct"/>s).
/// </summary>
internal sealed class XmpMetadata
{
    private readonly List<XmpSchema> _schemas = [];

    /// <summary>The schemas in the order they are written.</summary>
    public IReadOnlyList<XmpSchema> Schemas => _schemas;

    /// <summary>Returns the schema for <paramref name="ns"/>, adding an empty one at the end if there is none.</summary>
    public XmpSchema Schema(XmpNamespace ns)
    {
        ArgumentNullException.ThrowIfNull(ns);
        if (ns.Uri == XmpNamespace.Rdf.Uri)
        {
            throw new ArgumentException("The rdf: namespace is reserved for the packet structure.", nameof(ns));
        }

        var schema = _schemas.Find(s => s.Namespace.Uri == ns.Uri);
        if (schema is null)
        {
            schema = new XmpSchema(ns);
            _schemas.Add(schema);
        }

        return schema;
    }

    /// <summary>Formats a date as XMP (ISO 8601 with the offset), e.g. <c>2026-11-02T19:41:00+01:00</c>, to the second like the Info dictionary.</summary>
    public static string FormatDate(DateTimeOffset date)
    {
        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{date:yyyy-MM-dd'T'HH:mm:ss}{sign}{abs.Hours:D2}:{abs.Minutes:D2}");
    }
}
