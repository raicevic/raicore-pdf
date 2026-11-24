using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Documents.Rendering;

/// <summary>Blocks: the spacing between paragraphs, paragraph boxes, page breaks, anchored images and tables.</summary>
internal sealed partial class DocumentRenderer
{
    /// <summary>
    /// Lays out blocks in a column <paramref name="width"/> points wide. The space between two paragraphs is the
    /// space after the first plus the space before the second (word processors add them; CSS margins would collapse),
    /// except where contextual spacing leaves it out between paragraphs of the same style. Adjacent paragraphs with the
    /// same borders share one box.
    /// </summary>
    internal List<Block> RenderBlocks(List<WordBlock> blocks, double width, bool inTable)
    {
        var output = new List<Block>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var previous = i > 0 ? blocks[i - 1] as WordParagraph : null;
            var next = i + 1 < blocks.Count ? blocks[i + 1] as WordParagraph : null;
            switch (blocks[i])
            {
                case WordParagraph paragraph:
                    var format = paragraph.Format;
                    var before = previous is not null && format.ContextualSpacing && previous.Format.StyleId == format.StyleId ? 0 : format.SpaceBefore;
                    var after = next is not null && format.ContextualSpacing && next.Format.StyleId == format.StyleId ? 0 : format.SpaceAfter;
                    var gap = before + (previous is not null ? SpaceAfter(previous, paragraph) : 0);
                    var bottom = next is null && i == blocks.Count - 1 ? after : 0;
                    RenderParagraph(paragraph, output, width, gap, bottom, SameBorders(previous, paragraph), SameBorders(paragraph, next), inTable);
                    break;
                case WordTable table:
                    var top = previous is not null ? previous.Format.SpaceAfter : 0;
                    output.Add(RenderTable(table, width, top));
                    break;
            }
        }

        return output;
    }

    /// <summary>The space after a paragraph that is followed by another (none with contextual spacing in the same style).</summary>
    private static double SpaceAfter(WordParagraph paragraph, WordParagraph next) =>
        paragraph.Format.ContextualSpacing && paragraph.Format.StyleId == next.Format.StyleId ? 0 : paragraph.Format.SpaceAfter;

    /// <summary>Whether two paragraphs have the same (visible) borders, so they share one box.</summary>
    private static bool SameBorders(WordParagraph? a, WordParagraph? b) =>
        a is not null && b is not null && a.Format.Borders.Any && a.Format.Borders == b.Format.Borders && a.Format.IndentStart == b.Format.IndentStart
        && a.Format.IndentEnd == b.Format.IndentEnd;

    /// <summary>A border side for layout.</summary>
    private static BorderSide Side(BorderLine? line) => line is { IsVisible: true } visible
        ? new BorderSide(visible.Width, visible.Color, visible.Style switch
        {
            BorderLineStyle.Double => BorderStyle.Double,
            BorderLineStyle.Dotted => BorderStyle.Dotted,
            BorderLineStyle.Dashed => BorderStyle.Dashed,
            _ => BorderStyle.Solid,
        })
        : BorderSide.None;

    /// <summary>
    /// Lays out a paragraph: its anchored images before it (floating, or on lines of their own), its text split into
    /// one block per page break, then the text of its text boxes.
    /// </summary>
    private void RenderParagraph(WordParagraph paragraph, List<Block> output, double width, double top, double bottom, bool joinAbove, bool joinBelow, bool inTable)
    {
        var format = paragraph.Format;
        var atoms = new List<Atom>();
        var anchored = new List<AnchoredBlocks>();
        var images = new List<ImageInline>();
        Flatten(paragraph.Inlines, null, atoms, anchored, images);
        EndCommentedParagraph();

        var first = output.Count;
        var textWidth = Math.Max(1, width - Math.Max(0, format.IndentStart) - Math.Max(0, format.IndentEnd));
        foreach (var image in images)
        {
            if (AnchoredImage(image, width) is { } block)
            {
                output.Add(block);
            }
        }

        var segments = Segments(paragraph, atoms, textWidth);
        for (var s = 0; s < segments.Count; s++)
        {
            var isFirst = s == 0;
            var isLast = s == segments.Count - 1;
            var borders = format.Borders;
            var box = new BoxStyle
            {
                Margin = new Thickness(
                    isFirst ? top : 0,
                    format.RightToLeft ? format.IndentStart : format.IndentEnd,
                    isLast ? bottom : 0,
                    format.RightToLeft ? format.IndentEnd : format.IndentStart),
                Background = format.Shading,
                Border = new Borders(
                    joinAbove && isFirst ? Side(format.BetweenBorder) : isFirst ? Side(borders.Top) : BorderSide.None,
                    Side(borders.Right),
                    joinBelow || !isLast ? BorderSide.None : Side(borders.Bottom),
                    Side(borders.Left)),
                Padding = new Thickness(
                    joinAbove ? 0 : BorderPadding(borders.Top),
                    BorderPadding(borders.Right),
                    joinBelow || !isLast ? 0 : BorderPadding(borders.Bottom),
                    BorderPadding(borders.Left)),
                BreakBefore = segments[s].AfterBreak || (format.PageBreakBefore && !inTable && output.Count == first) ? PageBreak.Page : PageBreak.Auto,
                BreakAfter = isLast && format.KeepWithNext && !inTable ? PageBreak.Avoid : PageBreak.Auto,
                BreakInside = format.KeepLines ? PageBreakInside.Avoid : PageBreakInside.Auto,
                Orphans = format.WidowControl ? 2 : 1,
                Widows = format.WidowControl ? 2 : 1,
            };

            var heading = format.OutlineLevel is { } level && !inTable && _page is null && _depth == 0 && isFirst;
            var title = heading ? PlainText(paragraph) : null;
            output.Add(new ParagraphBlock(segments[s].Paragraph, box)
            {
                Tag = heading && format.OutlineLevel < 6 ? StructureTag.Heading(format.OutlineLevel!.Value + 1) : null,
                Bookmark = heading && _options.HeadingBookmarks && !string.IsNullOrWhiteSpace(title) ? new Bookmark(title, format.OutlineLevel!.Value + 1) : null,
            });
        }

        if (format.PageBreakBefore && !inTable && output.Count > first && output[first] is not ParagraphBlock)
        {
            // The paragraph starts with an anchored image: the page break goes before it.
            output.Insert(first, new ContainerBlock(new BoxStyle { BreakBefore = PageBreak.Page }));
        }

        foreach (var blocks in anchored)
        {
            if (_depth < 8)
            {
                _depth++;
                try
                {
                    output.AddRange(RenderBlocks(blocks.Blocks, width, inTable));
                }
                finally
                {
                    _depth--;
                }
            }
        }
    }

    /// <summary>The space between a border and the text: a little room, as word processors leave (their border spacing).</summary>
    private static double BorderPadding(BorderLine? line) => line is { IsVisible: true } ? 2 : 0;

    /// <summary>The plain text of a paragraph (label and text), for bookmarks.</summary>
    private static string PlainText(WordParagraph paragraph)
    {
        var text = new System.Text.StringBuilder();
        if (paragraph.Label is { } label && label.Text.Length > 0)
        {
            text.Append(label.Text).Append(' ');
        }

        void Append(List<WordInline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case TextRun run:
                        text.Append(run.Text);
                        break;
                    case TabInline or BreakInline:
                        text.Append(' ');
                        break;
                    case HyperlinkInline link:
                        Append(link.Inlines);
                        break;
                }
            }
        }

        Append(paragraph.Inlines);
        return string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A floating image (at the margin it is aligned or positioned nearer to, text wrapping around it) or an image on a
    /// line of its own, scaled down to the column width.
    /// </summary>
    private ImageBlock? AnchoredImage(ImageInline image, double width)
    {
        if (Image(image) is not { } loaded)
        {
            return null;
        }

        var (w, h) = Fit(image, loaded, width);
        var alignment = image.Offset is { } offset
            ? offset + (w / 2) > width / 2 ? WordAlignment.End : WordAlignment.Start
            : image.Alignment;
        var tag = _options.Tagged && image.AlternativeText is { } alternative ? new StructureTag("Figure") { AlternateText = alternative } : null;
        if (image.Placement == ImagePlacement.Float && alignment != WordAlignment.Center)
        {
            var right = alignment == WordAlignment.End;
            return new ImageBlock(loaded, w, h, new BoxStyle
            {
                Float = right ? FloatSide.Right : FloatSide.Left,
                Margin = new Thickness(0, right ? 0 : 9, 6, right ? 9 : 0),
            })
            { Tag = tag };
        }

        var left = image.Offset is { } x && image.Alignment == WordAlignment.Start ? Math.Clamp(x, 0, Math.Max(0, width - w)) : 0;
        return new ImageBlock(loaded, w, h, new BoxStyle { Margin = new Thickness(0, 0, 6, left) })
        {
            Alignment = left > 0 ? BlockAlignment.Left : alignment switch
            {
                WordAlignment.Center => BlockAlignment.Center,
                WordAlignment.End => BlockAlignment.Right,
                _ => BlockAlignment.Left,
            },
            Tag = tag,
        };
    }

    /// <summary>An image's size: as the document gives it (or its natural size), scaled down to the width and a page.</summary>
    private (double Width, double Height) Fit(ImageInline image, PdfImage loaded, double width)
    {
        var w = image.Width > 0 ? image.Width : image.Height > 0 && loaded.NaturalHeight > 0 ? image.Height * loaded.NaturalWidth / loaded.NaturalHeight : loaded.NaturalWidth;
        var h = image.Height > 0 ? image.Height : w * (loaded.NaturalWidth > 0 ? loaded.NaturalHeight / loaded.NaturalWidth : 1);
        w = Math.Max(w, 0.1);
        h = Math.Max(h, 0.1);
        var maxHeight = MaxImageHeight();
        var scale = Math.Min(1, Math.Min(Math.Max(1, width) / w, maxHeight / h));
        return (w * scale, h * scale);
    }

    /// <summary>The tallest an image may be: the smallest content height of the sections' pages.</summary>
    private double MaxImageHeight() => _setups.Count == 0 ? 600 : _setups.Values.Min(s => s.ContentHeight) - 2;
}
