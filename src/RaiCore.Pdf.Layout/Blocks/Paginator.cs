namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>A drawing operation on a page: receives the canvas and the page height (to convert from top-down y).</summary>
internal delegate void DrawOperation(PdfCanvas canvas, double pageHeight);

/// <summary>Where a piece of a block ended up (for tests and diagnostics). Y values are from the top of the page.</summary>
/// <param name="Page">0-based page index.</param>
/// <param name="Block">The block.</param>
/// <param name="Top">Top of the placed piece.</param>
/// <param name="Bottom">Bottom of the placed piece.</param>
/// <param name="FirstLine">For paragraphs: first line on this page; -1 for box fragments.</param>
/// <param name="EndLine">For paragraphs: the line after the last one on this page; -1 for box fragments.</param>
internal sealed record Placement(int Page, Block Block, double Top, double Bottom, int FirstLine, int EndLine);

/// <summary>The result of pagination: drawing operations per page, and where everything went.</summary>
internal sealed record PaginationResult(IReadOnlyList<IReadOnlyList<DrawOperation>> Pages, IReadOnlyList<Placement> Placements);

/// <summary>
/// Lays blocks out top to bottom and splits them across pages, following CSS block layout and fragmentation:
/// adjacent vertical margins collapse, margins are dropped at the top of a page after an automatic break,
/// paragraphs split between lines while honoring orphans and widows, <see cref="PageBreak.Avoid"/> after a block
/// keeps it with the start of the next one, and boxes split across pages are sliced (top border and padding on
/// the first fragment, bottom ones on the last).
/// </summary>
/// <remarks>All y values here are distances from the top of the page.</remarks>
internal sealed class Paginator
{
    private const double Epsilon = 1e-6;

    private readonly PageSetup _setup;
    private readonly List<List<DrawOperation>> _pages = [];
    private readonly List<Placement> _placements = [];

    private Paginator(PageSetup setup)
    {
        _setup = setup;
        NewPage(forced: true); // The first page keeps top margins, like the start of a CSS document.
    }

    private double ContentTop => _setup.Margins.Top;

    private double ContentBottom => _setup.Size.Height - _setup.Margins.Bottom;

    private int PageIndex => _pages.Count - 1;

    private double Y { get; set; }

    /// <summary>Nothing has been placed on the current page yet.</summary>
    private bool AtPageTop => Y <= ContentTop + Epsilon;

    /// <summary>The current page started after an automatic break, so margins at its top are truncated.</summary>
    private bool TruncateMargins { get; set; }

    private double Remaining => ContentBottom - Y;

    public static PaginationResult Paginate(FlowDocument document)
    {
        var paginator = new Paginator(document.Setup);
        var pendingMargin = new CollapsedMargin();
        paginator.LayoutChildren(document.Blocks, document.Setup.Margins.Left, document.Setup.ContentWidth, ref pendingMargin, firstTopMarginApplied: false);
        return new PaginationResult(paginator._pages, paginator._placements);
    }

    private void NewPage(bool forced = false)
    {
        _pages.Add([]);
        Y = ContentTop;
        TruncateMargins = !forced;
    }

    /// <summary>
    /// Lays out sibling blocks. <paramref name="pending"/> carries the collapsed margin above the next block in and
    /// the margin below the last block out (so it can collapse through a parent without bottom border or padding).
    /// </summary>
    private void LayoutChildren(IReadOnlyList<Block> blocks, double x, double width, ref CollapsedMargin pending, bool firstTopMarginApplied)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var next = i + 1 < blocks.Count ? blocks[i + 1] : null;
            var style = block.Style;

            // Forced break before this block, or after the previous one.
            var forced = style.BreakBefore == PageBreak.Page || (i > 0 && blocks[i - 1].Style.BreakAfter == PageBreak.Page);
            if (forced && !(AtPageTop && _pages[PageIndex].Count == 0))
            {
                NewPage(forced: true);
                pending = default; // The previous block's bottom margin ends with its page; this block keeps its top margin.
            }

            if (!(i == 0 && firstTopMarginApplied))
            {
                pending.Add(style.Margin.Top);
                pending.Add(CollapseThroughTop(block)); // First descendants' top margins collapse with this one.
            }

            // Move to a new page when the start of the block (and, for break-after: avoid, the start of the
            // next block) would not fit here but would fit at the top of a fresh page.
            var needed = MinimumStartHeight(block, width);
            if (style.BreakInside == PageBreakInside.Avoid)
            {
                needed = Math.Max(needed, MeasureHeight(block, width));
            }

            if (style.BreakAfter == PageBreak.Avoid && next is not null)
            {
                needed = Math.Max(needed, MeasureHeight(block, width) + Math.Max(style.Margin.Bottom, next.Style.Margin.Top) + MinimumStartHeight(next, width));
            }

            if (!AtPageTop && needed + pending.Value > Remaining + Epsilon && needed <= _setup.ContentHeight + Epsilon)
            {
                NewPage();
            }

            if (!(AtPageTop && TruncateMargins))
            {
                Y += pending.Value;
            }

            pending = default;

            var boxX = x + style.Margin.Left;
            var boxWidth = style.Width ?? Math.Max(0, width - style.Margin.Horizontal);
            if (block is ImageBlock image && style.Width is null)
            {
                // Like a CSS image, the box shrinks to the image; Alignment then positions the whole box.
                var decoration = style.Border.Widths.Horizontal + style.Padding.Horizontal;
                var shrunk = image.GetSize(Math.Max(1e-3, boxWidth - decoration), ImageMaxHeight(image)).Width + decoration;
                boxX += image.Alignment switch
                {
                    TextAlignment.Center => Math.Max(0, (boxWidth - shrunk) / 2),
                    TextAlignment.Right => Math.Max(0, boxWidth - shrunk),
                    _ => 0,
                };
                boxWidth = Math.Min(boxWidth, shrunk);
            }

            var trailing = LayoutBlock(block, boxX, boxWidth);

            pending = trailing;
            pending.Add(style.Margin.Bottom);
        }
    }

    /// <summary>
    /// The top margins of a container's first descendants that collapse with its own (CSS 2.1 section 8.3.1):
    /// only when the container has no top border or padding.
    /// </summary>
    private static double CollapseThroughTop(Block block)
    {
        if (block is not ContainerBlock { Children.Count: > 0 } container || HasTopEdge(block.Style))
        {
            return 0;
        }

        var first = container.Children[0];
        var margin = new CollapsedMargin();
        margin.Add(first.Style.Margin.Top);
        margin.Add(CollapseThroughTop(first));
        return margin.Value;
    }

    private static bool HasTopEdge(BoxStyle style) => style.Border.Top.Width > 0 || style.Padding.Top > 0;

    private static bool HasBottomEdge(BoxStyle style) => style.Border.Bottom.Width > 0 || style.Padding.Bottom > 0;

    /// <summary>Lays out one block; returns the bottom margin that collapses out of it (from its last child).</summary>
    private CollapsedMargin LayoutBlock(Block block, double x, double width)
    {
        var trailing = default(CollapsedMargin);
        switch (block)
        {
            case SpacerBlock spacer:
                if (!(AtPageTop && TruncateMargins))
                {
                    Y = Math.Min(Y + spacer.Height, ContentBottom);
                }

                break;
            case ParagraphBlock paragraph:
                LayoutBox(block, x, width, (contentX, contentWidth) => LayoutParagraph(paragraph, contentX, contentWidth));
                break;
            case ImageBlock image:
                LayoutBox(block, x, width, (contentX, contentWidth) => LayoutImage(image, contentX, contentWidth));
                break;
            case ContainerBlock container:
                LayoutBox(block, x, width, (contentX, contentWidth) =>
                {
                    var pending = default(CollapsedMargin);
                    LayoutChildren(container.Children, contentX, contentWidth, ref pending, firstTopMarginApplied: !HasTopEdge(block.Style));
                    if (HasBottomEdge(block.Style))
                    {
                        Y = Math.Min(Y + pending.Value, Math.Max(Y, ContentBottom)); // Stays inside the box.
                    }
                    else
                    {
                        trailing = pending; // Collapses through the container's bottom.
                    }
                });
                break;
            default:
                throw new NotSupportedException($"Unknown block type {block.GetType().Name}.");
        }

        return trailing;
    }

    /// <summary>
    /// Lays out a box's content between its top and bottom border and padding, then draws the background and
    /// borders of each page fragment underneath the content.
    /// </summary>
    private void LayoutBox(Block block, double x, double width, Action<double, double> layoutContent)
    {
        var style = block.Style;
        var border = style.Border.Widths;
        var decorated = style.Background is not null || border.Vertical > 0 || border.Horizontal > 0;

        var startPage = PageIndex;
        var startY = Y;
        var insertAt = _pages[startPage].Count;

        Y += border.Top + style.Padding.Top;
        layoutContent(x + border.Left + style.Padding.Left, Math.Max(0, width - border.Horizontal - style.Padding.Horizontal));
        Y += style.Padding.Bottom + border.Bottom;

        if (!decorated)
        {
            return;
        }

        for (var page = startPage; page <= PageIndex; page++)
        {
            var first = page == startPage;
            var last = page == PageIndex;
            var top = first ? startY : ContentTop;
            var bottom = last ? Y : ContentBottom;
            var index = first ? insertAt : 0; // Pages created inside the box: under everything on them.
            _pages[page].Insert(index, (canvas, pageHeight) => DrawBox(canvas, pageHeight, style, x, top, width, bottom - top, first, last));
            _placements.Add(new Placement(page, block, top, bottom, -1, -1));
        }

    }

    private static void DrawBox(PdfCanvas canvas, double pageHeight, BoxStyle style, double x, double top, double width, double height, bool first, bool last)
    {
        var y = pageHeight - top - height; // PDF y of the bottom edge.
        if (style.Background is { } background)
        {
            canvas.SetFillColor(background).Rectangle(x, y, width, height).Fill();
        }

        var b = style.Border;
        void Side(BorderSide side, double sx, double sy, double sw, double sh)
        {
            if (side.Width > 0)
            {
                canvas.SetFillColor(side.Color).Rectangle(sx, sy, sw, sh).Fill();
            }
        }

        if (first)
        {
            Side(b.Top, x, y + height - b.Top.Width, width, b.Top.Width);
        }

        if (last)
        {
            Side(b.Bottom, x, y, width, b.Bottom.Width);
        }

        Side(b.Left, x, y, b.Left.Width, height);
        Side(b.Right, x + width - b.Right.Width, y, b.Right.Width, height);
    }

    private void LayoutImage(ImageBlock block, double x, double width)
    {
        var (w, h) = block.GetSize(width, ImageMaxHeight(block));
        if (h > Remaining + Epsilon && !AtPageTop)
        {
            NewPage(); // Normally avoided by MinimumStartHeight; happens only inside a tall box.
        }

        var left = x + block.Alignment switch
        {
            TextAlignment.Center => Math.Max(0, (width - w) / 2),
            TextAlignment.Right => Math.Max(0, width - w),
            _ => 0,
        };
        var top = Y;
        _pages[PageIndex].Add((canvas, pageHeight) => canvas.DrawImage(block.Image, left, pageHeight - top - h, w, h));
        Y += h;
        _placements.Add(new Placement(PageIndex, block, top, Y, -1, -1));
    }

    /// <summary>An image is scaled down to fit on one page together with its own border and padding.</summary>
    private double ImageMaxHeight(Block block) =>
        Math.Max(1, _setup.ContentHeight - block.Style.Border.Widths.Vertical - block.Style.Padding.Vertical);

    private void LayoutParagraph(ParagraphBlock block, double x, double width)
    {
        var layout = block.Paragraph.Layout(width);
        var lines = layout.Lines;
        var orphans = Math.Max(1, block.Style.Orphans);
        var widows = Math.Max(1, block.Style.Widows);
        var i = 0;
        while (i < lines.Count)
        {
            var fit = CountFittingLines(lines, i, Remaining);
            var rest = lines.Count - i;
            if (fit >= rest)
            {
                Place(block, layout, x, i, lines.Count);
                return;
            }

            // Respect orphans (lines left before the break) and widows (lines carried after it).
            var take = fit;
            if (rest - take < widows)
            {
                take = rest - widows;
            }

            var minimum = i == 0 ? Math.Min(orphans, rest) : 1;
            if (take < minimum || take <= 0)
            {
                if (!AtPageTop)
                {
                    NewPage();
                    continue;
                }

                // Even an empty page cannot satisfy the constraints: place what fits (at least one line).
                take = Math.Max(1, Math.Min(fit, rest));
            }

            Place(block, layout, x, i, i + take);
            i += take;
            if (i < lines.Count)
            {
                NewPage();
            }
        }
    }

    private void Place(ParagraphBlock block, ParagraphLayout layout, double x, int first, int end)
    {
        var top = Y;
        _pages[PageIndex].Add((canvas, pageHeight) => layout.Draw(canvas, x, pageHeight - top, first, end));
        Y += layout.Lines[end - 1].Top + layout.Lines[end - 1].Height - layout.Lines[first].Top;
        _placements.Add(new Placement(PageIndex, block, top, Y, first, end));
    }

    private static int CountFittingLines(IReadOnlyList<LineLayout> lines, int start, double available)
    {
        var used = 0.0;
        var count = 0;
        for (var i = start; i < lines.Count; i++)
        {
            used += lines[i].Height;
            if (used > available + Epsilon)
            {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>Height of a block's border box if it were laid out without page breaks.</summary>
    private double MeasureHeight(Block block, double width)
    {
        var style = block.Style;
        var boxWidth = style.Width ?? Math.Max(0, width - style.Margin.Horizontal);
        var decoration = style.Border.Widths.Vertical + style.Padding.Vertical;
        var contentWidth = Math.Max(1e-3, boxWidth - style.Border.Widths.Horizontal - style.Padding.Horizontal);
        switch (block)
        {
            case SpacerBlock spacer:
                return spacer.Height;
            case ParagraphBlock paragraph:
                return decoration + paragraph.Paragraph.Layout(contentWidth).Height;
            case ImageBlock image:
                return decoration + image.GetSize(contentWidth, ImageMaxHeight(image)).Height;
            case ContainerBlock container:
                var height = 0.0;
                var margin = new CollapsedMargin();
                foreach (var child in container.Children)
                {
                    margin.Add(child.Style.Margin.Top);
                    height += margin.Value + MeasureHeight(child, contentWidth);
                    margin = new CollapsedMargin();
                    margin.Add(child.Style.Margin.Bottom);
                }

                return decoration + height + margin.Value;
            default:
                return 0;
        }
    }

    /// <summary>
    /// The smallest height a block needs at the bottom of a page before it is worth starting there:
    /// its top border and padding plus the first lines a paragraph may not be split from (orphans).
    /// </summary>
    private double MinimumStartHeight(Block block, double width)
    {
        var style = block.Style;
        var boxWidth = style.Width ?? Math.Max(0, width - style.Margin.Horizontal);
        var top = style.Border.Top.Width + style.Padding.Top;
        var contentWidth = Math.Max(1e-3, boxWidth - style.Border.Widths.Horizontal - style.Padding.Horizontal);
        switch (block)
        {
            case ParagraphBlock paragraph:
                var lines = paragraph.Paragraph.Layout(contentWidth).Lines;
                var count = Math.Min(lines.Count, Math.Max(1, style.Orphans));
                var linesHeight = lines.Take(count).Sum(l => l.Height);
                return top + (count == lines.Count ? linesHeight + style.Padding.Bottom + style.Border.Bottom.Width : linesHeight);
            case ImageBlock image:
                return MeasureHeight(image, width); // Images are never split.
            case ContainerBlock container when container.Children.Count > 0:
                var firstChild = container.Children[0];
                return top + firstChild.Style.Margin.Top + MinimumStartHeight(firstChild, contentWidth);
            case SpacerBlock:
                return 0;
            default:
                return top + style.Padding.Bottom + style.Border.Bottom.Width;
        }
    }

    /// <summary>CSS margin collapsing: the largest positive margin plus the most negative one.</summary>
    private struct CollapsedMargin
    {
        private double _positive;
        private double _negative;

        public readonly double Value => _positive + _negative;

        public void Add(double margin)
        {
            _positive = Math.Max(_positive, margin);
            _negative = Math.Min(_negative, margin);
        }
    }
}
