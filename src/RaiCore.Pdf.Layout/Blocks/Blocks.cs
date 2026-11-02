namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>A box in a <see cref="FlowDocument"/>: content that stacks vertically and flows across pages.</summary>
public abstract class Block
{
    private protected Block(BoxStyle? style) => Style = style ?? BoxStyle.Default;

    /// <summary>Margin, border, padding, background and page break behavior.</summary>
    public BoxStyle Style { get; }
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
