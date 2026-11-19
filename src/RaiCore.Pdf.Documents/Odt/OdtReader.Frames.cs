using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Odt;

/// <summary>Frames (images, text boxes, embedded objects), drawing shapes and links around them.</summary>
internal sealed partial class OdtReader
{
    private readonly Dictionary<string, byte[]?> _files = new(StringComparer.Ordinal);

    /// <summary>Reads a drawing element in a paragraph: a frame, a link around frames, or a shape.</summary>
    private void ReadDrawing(XElement element, List<WordInline> target, TextFormat format)
    {
        ReadDrawingContent(element, target, format);
        _afterSpace = false; // A drawing is not white space: spaces around it are kept.
    }

    private void ReadDrawingContent(XElement element, List<WordInline> target, TextFormat format)
    {
        switch (element.Name.LocalName)
        {
            case "frame":
                ReadFrame(element, target);
                break;
            case "a":
                var link = new HyperlinkInline { Uri = LinkUri(Odf.Attr(element, Odf.XLink, "href")?.Trim() ?? string.Empty) };
                foreach (var child in element.Elements())
                {
                    ReadInline(child, link.Inlines, format);
                }

                if (link.Inlines.Count > 0)
                {
                    target.Add(link);
                }

                break;
            case "control":
                _context.Report(DocumentDiagnosticKind.UnsupportedContent, "A form control is left out.");
                break;
            case "rect" or "line" or "polyline" or "polygon" or "regular-polygon" or "path" or "circle" or "ellipse" or "g" or "page-thumbnail"
                or "connector" or "caption" or "measure" or "custom-shape":
                ReadShape(element, target);
                break;
        }
    }

    /// <summary>
    /// Reads a frame: its image (the first one that can be drawn, of several alternatives; the replacement image of an
    /// embedded object such as a chart), and the text of its text box, laid out after the paragraph.
    /// </summary>
    private void ReadFrame(XElement frame, List<WordInline> target)
    {
        var anchor = Odf.Attr(frame, Odf.Text, "anchor-type") ?? "paragraph";
        var graphic = _styles.Properties("graphic", Odf.Attr(frame, Odf.Draw, "style-name"), _scope, "graphic-properties");
        var wrap = OdtStyles.Property(graphic, Odf.Style, "wrap");
        var horizontal = OdtStyles.Property(graphic, Odf.Style, "horizontal-pos");
        var placement = anchor == "as-char" ? ImagePlacement.Inline
            : anchor is "paragraph" or "char" && wrap is "left" or "right" or "parallel" or "dynamic" or "biggest" ? ImagePlacement.Float
            : ImagePlacement.Block;
        var alignment = horizontal switch
        {
            "center" => WordAlignment.Center,
            "right" or "outside" => WordAlignment.End,
            _ => WordAlignment.Start,
        };
        double? offset = placement != ImagePlacement.Inline && horizontal is null or "from-left" or "from-inside" && Odf.Length(frame, Odf.Svg, "x") is { } x
            ? Math.Clamp(x, 0, 14400)
            : null;
        var alternative = (Odf.Child(frame, Odf.Svg, "desc") ?? Odf.Child(frame, Odf.Svg, "title"))?.Value.Trim() is { Length: > 0 } text ? text : null;

        var embedded = frame.Elements().FirstOrDefault(e => Odf.Is(e, Odf.Draw, "object") || Odf.Is(e, Odf.Draw, "object-ole"));
        if (embedded is not null)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "An embedded object (such as a chart or a formula) is shown as its replacement image.");
        }

        var images = Odf.Children(frame, Odf.Draw, "image").ToList();
        if (Image(images) is { } image)
        {
            target.Add(new ImageInline(image.Data, Size(frame, Odf.Svg, "width", Odf.Fo, "min-width"), Size(frame, Odf.Svg, "height", Odf.Fo, "min-height"))
            {
                Placement = placement,
                Alignment = alignment,
                Offset = offset,
                AlternativeText = alternative,
                Name = image.Name,
            });
        }
        else if (embedded is not null && images.Count == 0)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "An embedded object without a replacement image is left out.");
        }

        foreach (var textBox in Odf.Children(frame, Odf.Draw, "text-box"))
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "The text of a text box is laid out after its paragraph.");
            AddAnchored(textBox, target);
        }

        if (frame.Elements().Any(e => e.Name.Namespace == Odf.Draw && e.Name.LocalName is "plugin" or "applet" or "floating-frame"))
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "A plug-in, applet or floating frame is left out.");
        }
    }

    /// <summary>A size attribute in points (with a fallback attribute), or 0 for the image's natural size.</summary>
    private static double Size(XElement frame, XNamespace ns, string name, XNamespace fallbackNs, string fallback)
    {
        var size = Odf.Length(frame, ns, name) ?? Odf.Length(frame, fallbackNs, fallback) ?? 0;
        return size is > 0 and < 14400 ? size : 0;
    }

    /// <summary>
    /// Reads the text of a drawing shape (laid out after the paragraph) and the frames in a group; a shape without
    /// text is left out. Both are reported.
    /// </summary>
    private void ReadShape(XElement shape, List<WordInline> target)
    {
        if (AddAnchored(shape, target))
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "The text of a drawing shape is laid out after its paragraph.");
        }
        else
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "A drawing shape is left out.");
        }
    }

    /// <summary>Adds the blocks of a text box or shape as blocks anchored in the paragraph; returns whether there were any.</summary>
    private bool AddAnchored(XElement container, List<WordInline> target)
    {
        var anchored = new AnchoredBlocks();
        var (afterSpace, breakBefore, skipNumber, width) = (_afterSpace, _breakBeforeNext, _skipNumber, _width);
        try
        {
            _skipNumber = false;
            ReadBlocks(container, anchored.Blocks, body: false);
        }
        finally
        {
            (_afterSpace, _breakBeforeNext, _skipNumber, _width) = (afterSpace, breakBefore, skipNumber, width);
        }

        if (anchored.Blocks.Count == 0)
        {
            return false;
        }

        target.Add(anchored);
        return true;
    }

    /// <summary>
    /// Reads a drawing outside a paragraph (anchored to the page): its images on lines of their own and its text after
    /// them.
    /// </summary>
    private void ReadBlockDrawing(XElement element, List<WordBlock> output)
    {
        var style = _styles.Paragraph(null, _scope);
        var inlines = new List<WordInline>();
        ReadDrawing(element, inlines, style.Text);
        var holder = new WordParagraph { Format = style.Format, MarkFormat = style.Text.Run };
        var anchored = new List<WordBlock>();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case AnchoredBlocks blocks:
                    anchored.AddRange(blocks.Blocks);
                    break;
                case ImageInline image:
                    holder.Inlines.Add(new ImageInline(image.Data, image.Width, image.Height)
                    {
                        Placement = ImagePlacement.Inline,
                        AlternativeText = image.AlternativeText,
                        Name = image.Name,
                    });
                    holder.Format = holder.Format with
                    {
                        Alignment = image.Alignment,
                        IndentStart = image.Alignment == WordAlignment.Start ? image.Offset ?? 0 : 0,
                    };
                    break;
                default:
                    holder.Inlines.Add(inline);
                    break;
            }
        }

        if (holder.Inlines.Count > 0)
        {
            output.Add(holder);
        }

        output.AddRange(anchored);
    }

    /// <summary>
    /// The image of a frame's <c>draw:image</c> elements to draw: the first in a format that can be drawn (PNG, JPEG,
    /// GIF, BMP, TIFF, WebP, then SVG), else the first that loads, which is reported when drawn. Linked and missing
    /// images are reported and left out.
    /// </summary>
    private (byte[] Data, string Name)? Image(List<XElement> images)
    {
        var candidates = new List<(byte[] Data, string Name)>();
        foreach (var image in images)
        {
            if (ImageData(image) is { } data)
            {
                candidates.Add(data);
            }
        }

        foreach (var rank in new[] { 0, 1 })
        {
            foreach (var candidate in candidates)
            {
                if (Rank(candidate.Data) == rank)
                {
                    return candidate;
                }
            }
        }

        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>0 for raster formats that can be drawn, 1 for SVG, 2 for others (EMF, WMF, StarView metafiles).</summary>
    private static int Rank(byte[] data)
    {
        ReadOnlySpan<byte> span = data;
        if (span.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']) || span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])
            || span.StartsWith("GIF8"u8) || span.StartsWith("BM"u8) || span.StartsWith("II*\0"u8) || span.StartsWith("MM\0*"u8)
            || (span.StartsWith("RIFF"u8) && span.Length >= 12 && span[8..12].SequenceEqual("WEBP"u8)))
        {
            return 0;
        }

        var head = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 1024));
        return head.Contains("<svg", StringComparison.Ordinal) ? 1 : 2;
    }

    /// <summary>Whether a reference points outside the package: an address with a scheme, or a path from the root or above it.</summary>
    internal static bool IsLinked(string href)
    {
        var colon = href.IndexOf(':', StringComparison.Ordinal);
        var slash = href.IndexOf('/', StringComparison.Ordinal);
        return href.StartsWith("../", StringComparison.Ordinal) || href.StartsWith('/') || href.StartsWith('\\') || (colon > 0 && (slash < 0 || colon < slash));
    }

    /// <summary>The data of a <c>draw:image</c>: the package file it refers to, or its embedded base64 data.</summary>
    private (byte[] Data, string Name)? ImageData(XElement image)
    {
        if (Odf.Child(image, Odf.Office, "binary-data") is { } binary)
        {
            if (_package.Decode(binary.Value) is { Length: > 0 } decoded)
            {
                return (decoded, "embedded image");
            }

            _context.Report(DocumentDiagnosticKind.ImageNotLoaded, "An embedded image whose data is not base64 is left out.");
            return null;
        }

        var href = Odf.Attr(image, Odf.XLink, "href")?.Trim();
        if (string.IsNullOrEmpty(href))
        {
            return null;
        }

        if (IsLinked(href))
        {
            _context.Report(DocumentDiagnosticKind.ImageNotLoaded, "A linked image (not embedded in the document) is left out.");
            return null;
        }

        if (!_files.TryGetValue(href, out var data))
        {
            data = _package.ReadFile(href);
            _files[href] = data;
        }

        if (data is null)
        {
            _context.Report(DocumentDiagnosticKind.ImageNotLoaded, $"The image '{href}' is missing from the package.");
            return null;
        }

        return (data, href);
    }
}
