namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>A box in a <see cref="FlowDocument"/>: content that stacks vertically and flows across pages.</summary>
public abstract class Block
{
    private protected Block(BoxStyle? style) => Style = style ?? BoxStyle.Default;

    /// <summary>Margin, border, padding, background and page break behavior.</summary>
    public BoxStyle Style { get; }

    /// <summary>
    /// Any object to track: <see cref="PageInfo.Markers"/> lists the markers of the blocks that start on each page.
    /// Used for running headers (e.g. the current chapter title). An empty <see cref="ContainerBlock"/> with a marker
    /// takes no space and can mark a position.
    /// </summary>
    public object? Marker { get; init; }

    /// <summary>
    /// Makes the block an entry in the PDF outline that jumps to where the block starts (where its content starts, if
    /// that moved to a later page). <c>null</c> (the default) for none.
    /// </summary>
    public Bookmark? Bookmark { get; init; }
}

/// <summary>A block containing one paragraph of text.</summary>
public sealed class ParagraphBlock : Block
{
    /// <summary>Creates a paragraph block.</summary>
    public ParagraphBlock(Paragraph paragraph, BoxStyle? style = null)
        : base(style)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        Paragraph = paragraph;
    }

    /// <summary>The paragraph.</summary>
    public Paragraph Paragraph { get; }
}

/// <summary>A block that stacks other blocks (like an HTML <c>div</c>).</summary>
public sealed class ContainerBlock : Block
{
    private readonly List<Block> _children = [];

    /// <summary>Creates a container.</summary>
    public ContainerBlock(BoxStyle? style = null)
        : base(style)
    {
    }

    /// <summary>Creates a container with children.</summary>
    public ContainerBlock(BoxStyle? style, params IEnumerable<Block> children)
        : base(style)
    {
        ArgumentNullException.ThrowIfNull(children);
        _children.AddRange(children);
    }

    /// <summary>The child blocks, top to bottom.</summary>
    public IReadOnlyList<Block> Children => _children;

    /// <summary>Appends a child block.</summary>
    public ContainerBlock Add(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _children.Add(block);
        return this;
    }
}

/// <summary>Empty vertical space of a fixed height (it disappears at the top of a page, like a collapsed margin).</summary>
public sealed class SpacerBlock : Block
{
    /// <summary>Creates a spacer.</summary>
    public SpacerBlock(double height)
        : base(null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Height = height;
    }

    /// <summary>The height in points.</summary>
    public double Height { get; }
}

/// <summary>
/// A block-level image, such as a logo or a figure. Sized like an HTML <c>img</c>: by default at its natural size
/// (96 pixels per inch, as in CSS), scaled down to fit the available width and the page. It is never split across pages.
/// </summary>
public sealed class ImageBlock : Block
{
    /// <summary>
    /// Creates an image block. Give a width or a height to scale the image (the other follows the aspect ratio),
    /// both to stretch it, or neither for the natural size.
    /// </summary>
    public ImageBlock(PdfImage image, double? width = null, double? height = null, BoxStyle? style = null)
        : base(style)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (width is <= 0 || height is <= 0)
        {
            throw new ArgumentOutOfRangeException(width is <= 0 ? nameof(width) : nameof(height), "Sizes must be positive.");
        }

        Image = image;
        Width = width;
        Height = height;
    }

    /// <summary>The image.</summary>
    public PdfImage Image { get; }

    /// <summary>Requested width in points, if any.</summary>
    public double? Width { get; }

    /// <summary>Requested height in points, if any.</summary>
    public double? Height { get; }

    /// <summary>Horizontal position within the available width.</summary>
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>The drawn size: the requested or natural size, scaled down (keeping the aspect ratio) to fit.</summary>
    internal (double Width, double Height) GetSize(double maxWidth, double maxHeight)
    {
        var aspect = Image.NaturalHeight / Image.NaturalWidth;
        var (w, h) = (Width, Height) switch
        {
            ({ } fw, { } fh) => (fw, fh),
            ({ } fw, null) => (fw, fw * aspect),
            (null, { } fh) => (fh / aspect, fh),
            _ => (Image.NaturalWidth, Image.NaturalHeight),
        };

        var scale = Math.Min(1, Math.Min(maxWidth / w, maxHeight / h));
        return scale < 1 ? (w * scale, h * scale) : (w, h);
    }
}
