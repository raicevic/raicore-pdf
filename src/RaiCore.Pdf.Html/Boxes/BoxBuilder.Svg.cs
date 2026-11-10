using System.Globalization;
using System.Xml;
using AngleSharp.Dom;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Images;

namespace RaiCore.Pdf.Html.Boxes;

/// <summary>Inline <c>svg</c> elements, drawn as SVG images.</summary>
internal sealed partial class BoxBuilder
{
    /// <summary>
    /// Loads an inline <c>svg</c> element as an image. The element is written out as XML (the HTML parser's DOM need
    /// not be well-formed XML: undeclared prefixes, HTML entities) with the element's CSS <c>color</c>, which
    /// <c>currentColor</c> in the SVG refers to. Returns null when the SVG cannot be read.
    /// </summary>
    private PdfImage? LoadInlineSvg(IElement svg, ComputedStyle style)
    {
        var markup = SerializeSvg(svg, style.Color);
        var key = "svg:" + markup; // Image sources are URLs and never start with markup.
        if (_images.TryGetValue(key, out var cached))
        {
            return cached;
        }

        PdfImage? image = null;
        try
        {
            image = PdfImage.LoadSvg(markup);
        }
        catch (ImageFormatException)
        {
            // An unreadable SVG is skipped, as browsers do.
        }

        _images[key] = image;
        return image;
    }

    /// <summary>Writes an SVG element of the HTML DOM as standalone SVG markup.</summary>
    internal static string SerializeSvg(IElement svg, PdfColor color)
    {
        // Characters XML does not allow are passed through, so the SVG parser rejects them instead of the writer throwing.
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, CheckCharacters = false };
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(text, settings))
        {
            WriteSvgElement(writer, svg, color);
        }

        return text.ToString();
    }

    private static void WriteSvgElement(XmlWriter writer, IElement element, PdfColor? rootColor)
    {
        if (!IsXmlName(element.LocalName))
        {
            return;
        }

        writer.WriteStartElement(element.LocalName);
        var written = new HashSet<string>(StringComparer.Ordinal);
        if (rootColor is { } color)
        {
            // The root's color is the element's computed CSS color (also from style sheets the SVG does not see).
            writer.WriteAttributeString("color", string.Create(
                CultureInfo.InvariantCulture,
                $"#{Channel(color.Red):x2}{Channel(color.Green):x2}{Channel(color.Blue):x2}"));
            written.Add("color");
        }

        // Unprefixed attributes first, so that href wins over xlink:href. Namespace declarations are left out: names
        // are matched without namespaces.
        foreach (var attribute in element.Attributes.OrderBy(a => a.Prefix is null ? 0 : 1))
        {
            var name = attribute.Prefix switch
            {
                null => attribute.LocalName,
                "xlink" when attribute.LocalName == "href" => "href",
                _ => null,
            };
            if (name is null || name == "xmlns" || !IsXmlName(name) || !written.Add(name))
            {
                continue;
            }

            writer.WriteAttributeString(name, attribute.Value);
        }

        foreach (var node in element.ChildNodes)
        {
            switch (node)
            {
                case IElement child:
                    WriteSvgElement(writer, child, null);
                    break;
                case IText characters:
                    writer.WriteString(characters.Data);
                    break;
            }
        }

        writer.WriteEndElement();
    }

    private static bool IsXmlName(string name) =>
        name.Length > 0 && XmlConvert.IsStartNCNameChar(name[0]) && name.All(XmlConvert.IsNCNameChar);

    private static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
