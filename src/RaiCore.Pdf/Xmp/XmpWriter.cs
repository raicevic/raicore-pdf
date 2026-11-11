using System.Text;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Xmp;

/// <summary>
/// Serializes <see cref="XmpMetadata"/> as an XMP packet: RDF/XML in UTF-8 inside an <c>xpacket</c> wrapper with
/// padding, so tools can find and update it in place.
/// </summary>
internal static class XmpWriter
{
    /// <summary>The packet id every XMP packet uses (ISO 16684-1, 7.3.2).</summary>
    private const string PacketId = "W5M0MpCehiHzreSzNTczkc9d";

    /// <summary>Default padding: the 2 KB of whitespace XMP recommends, so the packet can grow when edited in place.</summary>
    public const int DefaultPadding = 2000;

    private const int PaddingLineLength = 100;

    /// <summary>
    /// Creates the document's metadata stream (<c>/Type /Metadata /Subtype /XML</c>). It is never compressed: PDF/A
    /// requires the XMP to be readable without decoding, and tools that are not PDF-aware scan files for the packet.
    /// </summary>
    public static PdfStreamObject CreateStream(XmpMetadata metadata) =>
        new(Serialize(metadata), new PdfDictionary
        {
            ["Type"] = new PdfName("Metadata"),
            ["Subtype"] = new PdfName("XML"),
        });

    /// <summary>Serializes the packet as UTF-8 (without a byte order mark; the <c>begin</c> attribute holds one).</summary>
    public static byte[] Serialize(XmpMetadata metadata, int padding = DefaultPadding)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentOutOfRangeException.ThrowIfNegative(padding);

        var xml = new StringBuilder();
        xml.Append("<?xpacket begin=\"\uFEFF\" id=\"").Append(PacketId).Append("\"?>\n");
        xml.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n");
        xml.Append(" <rdf:RDF xmlns:rdf=\"").Append(XmpNamespace.Rdf.Uri).Append("\">\n");
        foreach (var schema in metadata.Schemas)
        {
            if (schema.Properties.Count == 0)
            {
                continue;
            }

            // One description per schema, declaring every namespace its properties (and their structures) use.
            xml.Append("  <rdf:Description rdf:about=\"\"");
            foreach (var ns in NamespacesOf(schema))
            {
                xml.Append(" xmlns:").Append(ns.Prefix).Append("=\"");
                AppendEscaped(xml, ns.Uri, attribute: true);
                xml.Append('"');
            }

            xml.Append(">\n");
            foreach (var property in schema.Properties)
            {
                AppendProperty(xml, property, 3);
            }

            xml.Append("  </rdf:Description>\n");
        }

        xml.Append(" </rdf:RDF>\n");
        xml.Append("</x:xmpmeta>\n");

        // Whitespace padding, in lines, then the trailer; "w" marks the packet as writable in place.
        for (var remaining = padding; remaining > 0; remaining -= PaddingLineLength)
        {
            var length = Math.Min(PaddingLineLength, remaining);
            xml.Append(' ', length - 1).Append('\n');
        }

        xml.Append("<?xpacket end=\"w\"?>");
        return Encoding.UTF8.GetBytes(xml.ToString());
    }

    private static List<XmpNamespace> NamespacesOf(XmpSchema schema)
    {
        var seen = new List<XmpNamespace> { schema.Namespace };
        void Collect(XmpValue value)
        {
            switch (value)
            {
                case XmpArray array:
                    foreach (var item in array.Items)
                    {
                        Collect(item);
                    }

                    break;
                case XmpStruct structure:
                    foreach (var field in structure.Fields)
                    {
                        if (!seen.Exists(n => n.Uri == field.Namespace.Uri))
                        {
                            seen.Add(field.Namespace);
                        }

                        Collect(field.Value);
                    }

                    break;
            }
        }

        foreach (var property in schema.Properties)
        {
            Collect(property.Value);
        }

        // Two URIs must not share a prefix in one element.
        var duplicate = seen.GroupBy(n => n.Prefix, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"The XMP prefix '{duplicate.Key}' is bound to more than one namespace.");
        }

        return seen;
    }

    private static void AppendProperty(StringBuilder xml, XmpProperty property, int depth)
    {
        var name = property.Namespace.Prefix + ":" + property.Name;
        Indent(xml, depth);
        switch (property.Value)
        {
            case XmpText text:
                xml.Append('<').Append(name).Append('>');
                AppendEscaped(xml, text.Value, attribute: false);
                xml.Append("</").Append(name).Append(">\n");
                break;

            case XmpStruct structure:
                xml.Append('<').Append(name).Append(" rdf:parseType=\"Resource\">\n");
                foreach (var field in structure.Fields)
                {
                    AppendProperty(xml, field, depth + 1);
                }

                Indent(xml, depth);
                xml.Append("</").Append(name).Append(">\n");
                break;

            default:
                xml.Append('<').Append(name).Append(">\n");
                AppendContainer(xml, property.Value, depth + 1);
                Indent(xml, depth);
                xml.Append("</").Append(name).Append(">\n");
                break;
        }
    }

    private static void AppendContainer(StringBuilder xml, XmpValue value, int depth)
    {
        switch (value)
        {
            case XmpLanguageAlternative alternative:
                Indent(xml, depth);
                xml.Append("<rdf:Alt>\n");
                foreach (var (language, text) in alternative.Entries)
                {
                    Indent(xml, depth + 1);
                    xml.Append("<rdf:li xml:lang=\"");
                    AppendEscaped(xml, language, attribute: true);
                    xml.Append("\">");
                    AppendEscaped(xml, text, attribute: false);
                    xml.Append("</rdf:li>\n");
                }

                Indent(xml, depth);
                xml.Append("</rdf:Alt>\n");
                break;

            case XmpArray array:
                var tag = array.Kind == XmpArrayKind.Bag ? "rdf:Bag" : "rdf:Seq";
                Indent(xml, depth);
                xml.Append('<').Append(tag).Append(">\n");
                foreach (var item in array.Items)
                {
                    Indent(xml, depth + 1);
                    switch (item)
                    {
                        case XmpText text:
                            xml.Append("<rdf:li>");
                            AppendEscaped(xml, text.Value, attribute: false);
                            xml.Append("</rdf:li>\n");
                            break;
                        case XmpStruct structure:
                            xml.Append("<rdf:li rdf:parseType=\"Resource\">\n");
                            foreach (var field in structure.Fields)
                            {
                                AppendProperty(xml, field, depth + 2);
                            }

                            Indent(xml, depth + 1);
                            xml.Append("</rdf:li>\n");
                            break;
                        default:
                            xml.Append("<rdf:li>\n");
                            AppendContainer(xml, item, depth + 2);
                            Indent(xml, depth + 1);
                            xml.Append("</rdf:li>\n");
                            break;
                    }
                }

                Indent(xml, depth);
                xml.Append("</").Append(tag).Append(">\n");
                break;

            default:
                throw new InvalidOperationException($"Unexpected XMP value {value.GetType().Name}.");
        }
    }

    private static void Indent(StringBuilder xml, int depth) => xml.Append(' ', depth);

    /// <summary>
    /// Escapes text for XML. Characters XML 1.0 cannot hold (most control characters, unpaired surrogates,
    /// U+FFFE/U+FFFF) are left out; carriage returns and, in attributes, tabs and line feeds are written as character
    /// references so a parser keeps them.
    /// </summary>
    internal static void AppendEscaped(StringBuilder xml, string text, bool attribute)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                xml.Append(c).Append(text[++i]);
                continue;
            }

            switch (c)
            {
                case '&':
                    xml.Append("&amp;");
                    break;
                case '<':
                    xml.Append("&lt;");
                    break;
                case '>':
                    xml.Append("&gt;");
                    break;
                case '"' when attribute:
                    xml.Append("&quot;");
                    break;
                case '\r':
                    xml.Append("&#xD;");
                    break;
                case '\n' or '\t' when attribute:
                    xml.Append(c == '\n' ? "&#xA;" : "&#x9;");
                    break;
                case '\n' or '\t':
                    xml.Append(c);
                    break;
                case < ' ' or '\uFFFE' or '\uFFFF':
                    break;
                default:
                    if (!char.IsSurrogate(c))
                    {
                        xml.Append(c);
                    }

                    break;
            }
        }
    }
}
