using System.Globalization;
using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Docx;

/// <summary>Images, shapes and text boxes (DrawingML and VML).</summary>
internal sealed partial class DocxReader
{
    /// <summary>
    /// Reads a <c>w:drawing</c>: a picture (inline, or anchored as a floating or block image), the text of the text
    /// boxes in a shape or group (laid out after the paragraph), and diagnostics for charts, SmartArt and other shapes.
    /// </summary>
    private void ReadDrawing(XElement drawing, RunFormat format, List<WordInline> target)
    {
        var container = drawing.Elements().FirstOrDefault(e => e.Name.LocalName is "inline" or "anchor");
        if (container is null)
        {
            return;
        }

        var graphicData = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "graphicData");
        var uri = Ooxml.Attr(graphicData, "uri") ?? string.Empty;
        if (uri.EndsWith("/chart", StringComparison.Ordinal) || uri.Contains("chartex", StringComparison.OrdinalIgnoreCase))
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "A chart is left out.");
            return;
        }

        if (uri.EndsWith("/diagram", StringComparison.Ordinal))
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "A SmartArt diagram is left out.");
            return;
        }

        var extent = container.Elements().FirstOrDefault(e => e.Name.LocalName == "extent");
        var width = Ooxml.Emu(extent, "cx") ?? 0;
        var height = Ooxml.Emu(extent, "cy") ?? 0;
        var properties = container.Elements().FirstOrDefault(e => e.Name.LocalName == "docPr");
        var alternative = Ooxml.Attr(properties, "descr") is { Length: > 0 } description ? description : Ooxml.Attr(properties, "title");
        var anchored = container.Name.LocalName == "anchor";
        var (placement, alignment, offset) = anchored ? Placement(container) : (ImagePlacement.Inline, WordAlignment.Start, null);

        var pictures = graphicData?.Descendants().Where(e => e.Name.LocalName == "pic").ToList() ?? [];
        var textBoxes = graphicData?.Descendants().Where(e => e.Name.LocalName == "txbxContent" && Ooxml.IsW(e)).ToList() ?? [];
        if (pictures.Count == 1 && textBoxes.Count == 0 && !uri.Contains("Group", StringComparison.OrdinalIgnoreCase))
        {
            if (Picture(pictures[0], width, height, placement, alignment, offset, alternative) is { } image)
            {
                Emit(image, target);
            }

            return;
        }

        // A shape, group or canvas: its pictures as block images and its text boxes' text after the paragraph.
        foreach (var picture in pictures)
        {
            var size = picture.Descendants().FirstOrDefault(e => e.Name.LocalName == "ext" && e.Parent?.Name.LocalName == "xfrm");
            if (Picture(picture, Ooxml.Emu(size, "cx") ?? width, Ooxml.Emu(size, "cy") ?? height, ImagePlacement.Block, alignment, offset, alternative) is { } image)
            {
                Emit(image, target);
            }
        }

        if (textBoxes.Count > 0)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "The text of a text box or shape is laid out after its paragraph.");
            AddTextBoxes(textBoxes, target);
        }
        else if (pictures.Count == 0)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "A drawing shape is left out.");
        }
    }

    /// <summary>The blocks of text boxes, laid out after the paragraph that anchors them.</summary>
    private void AddTextBoxes(List<XElement> textBoxes, List<WordInline> target)
    {
        var anchored = new AnchoredBlocks();
        foreach (var textBox in textBoxes)
        {
            // Nested text boxes are read with their outer one.
            if (textBox.Ancestors().Any(a => a.Name.LocalName == "txbxContent" && textBoxes.Contains(a)))
            {
                continue;
            }

            ReadBlocks(textBox, anchored.Blocks, TableContext.None);
        }

        if (anchored.Blocks.Count > 0)
        {
            Emit(anchored, target);
        }
    }

    /// <summary>
    /// How an anchored drawing is placed: with text wrapping around it (square, tight, through) it floats at the margin
    /// it is aligned or positioned nearer to; otherwise (top and bottom, in front of or behind the text) it stands on a
    /// line of its own.
    /// </summary>
    private static (ImagePlacement, WordAlignment, double?) Placement(XElement anchor)
    {
        var wrap = anchor.Elements().Select(e => e.Name.LocalName).FirstOrDefault(n => n.StartsWith("wrap", StringComparison.Ordinal));
        var placement = wrap is "wrapSquare" or "wrapTight" or "wrapThrough" ? ImagePlacement.Float : ImagePlacement.Block;
        var horizontal = anchor.Elements().FirstOrDefault(e => e.Name.LocalName == "positionH");
        var align = horizontal?.Elements().FirstOrDefault(e => e.Name.LocalName == "align")?.Value.Trim();
        var alignment = align switch
        {
            "center" => WordAlignment.Center,
            "right" or "outside" => WordAlignment.End,
            _ => WordAlignment.Start,
        };
        double? offset = null;
        if (align is null && horizontal?.Elements().FirstOrDefault(e => e.Name.LocalName == "posOffset") is { } position
            && Ooxml.Measure(position.Value, Ooxml.PointsPerEmu) is { } points)
        {
            offset = Math.Max(0, points);
        }

        return (placement, alignment, offset);
    }

    /// <summary>A picture (<c>pic:pic</c>) as an image, or <c>null</c> (with a diagnostic) if its data is missing or linked.</summary>
    private ImageInline? Picture(XElement picture, double width, double height, ImagePlacement placement, WordAlignment alignment, double? offset, string? alternative)
    {
        var blip = picture.Descendants().FirstOrDefault(e => e.Name.LocalName == "blip");
        if (blip is null)
        {
            return null;
        }

        if (RelationshipId(blip, "embed") is not { } id)
        {
            _context.Report(DocumentDiagnosticKind.ImageNotLoaded, "A linked image (not embedded in the document) is left out.");
            return null;
        }

        return Image(id, width, height, placement, alignment, offset, alternative);
    }

    /// <summary>An image by relationship id, or <c>null</c> (with a diagnostic) if the package does not have it.</summary>
    private ImageInline? Image(string id, double width, double height, ImagePlacement placement, WordAlignment alignment, double? offset, string? alternative)
    {
        if (_context.Has(ReadExtras.NoImages))
        {
            return null;
        }

        if (!_relationships.TryGetValue(id, out var relationship) || relationship.External)
        {
            _context.Report(DocumentDiagnosticKind.ImageNotLoaded, "A linked image (not embedded in the document) is left out.");
            return null;
        }

        if (!_media.TryGetValue(relationship.Target, out var data))
        {
            data = _package.Read(relationship.Target);
            _media[relationship.Target] = data;
        }

        if (data is null)
        {
            _context.Report(DocumentDiagnosticKind.ImageNotLoaded, $"The image '{relationship.Target}' is missing from the package.");
            return null;
        }

        return new ImageInline(data, Sane(width), Sane(height))
        {
            Placement = placement,
            Alignment = alignment,
            Offset = offset,
            AlternativeText = alternative,
            Name = relationship.Target,
        };
    }

    /// <summary>A size in points, or 0 (the image's natural size) for sizes that make no sense.</summary>
    private static double Sane(double size) => size is > 0 and < 14400 ? size : 0;

    /// <summary>
    /// Reads VML (<c>w:pict</c>, and the preview of an embedded object, <c>w:object</c>): an image
    /// (<c>v:imagedata</c>) sized by its shape's style, and the text of text boxes.
    /// </summary>
    private void ReadVml(XElement element, List<WordInline> target)
    {
        if (element.Name.LocalName == "object")
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "An embedded object is shown as its preview image.");
        }

        foreach (var data in element.Descendants().Where(e => e.Name.LocalName == "imagedata"))
        {
            if (RelationshipId(data) is not { } id)
            {
                continue;
            }

            var (width, height) = VmlSize(Ooxml.Attr(data.Parent, "style"));
            if (Image(id, width, height, ImagePlacement.Inline, WordAlignment.Start, null, Ooxml.Attr(data, "title")) is { } image)
            {
                Emit(image, target);
            }
        }

        var textBoxes = element.Descendants().Where(e => e.Name.LocalName == "txbxContent" && Ooxml.IsW(e)).ToList();
        if (textBoxes.Count > 0)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "The text of a text box or shape is laid out after its paragraph.");
            AddTextBoxes(textBoxes, target);
        }
    }

    /// <summary>The width and height of a VML shape's style (<c>width:120pt;height:3cm</c>), in points (0 when unknown).</summary>
    internal static (double Width, double Height) VmlSize(string? style)
    {
        double width = 0, height = 0;
        foreach (var declaration in (style ?? string.Empty).Split(';'))
        {
            var colon = declaration.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var name = declaration[..colon].Trim();
            var value = declaration[(colon + 1)..].Trim();
            var points = value.EndsWith("px", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels)
                ? pixels * 0.75
                : Ooxml.Measure(value, 1) ?? 0;
            if (name.Equals("width", StringComparison.OrdinalIgnoreCase))
            {
                width = Sane(points);
            }
            else if (name.Equals("height", StringComparison.OrdinalIgnoreCase))
            {
                height = Sane(points);
            }
        }

        return (width, height);
    }
}
