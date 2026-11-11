namespace RaiCore.Pdf.Layout;

/// <summary>
/// What a block, a table cell or a run of text is in a tagged document's logical structure: a heading, paragraph,
/// list item, figure and so on (see <see cref="PdfStructureTypes"/>), or an <see cref="Artifact"/>. Rendering into a
/// <see cref="PdfDocument.Tagged"/> document turns tags into structure elements in document order.
/// </summary>
/// <remarks>
/// Untagged content gets defaults: paragraph blocks are paragraphs (<c>P</c>), image blocks figures, tables tables with
/// rows and data cells, and links in text links; containers and flex boxes add no element of their own. Decorations
/// (backgrounds, borders, text decorations) and repeated table headers are always artifacts.
/// </remarks>
public sealed class StructureTag
{
    /// <summary>Creates a tag of a structure type, such as <see cref="PdfStructureTypes.Heading1"/>.</summary>
    public StructureTag(string type)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        Type = type;
    }

    private StructureTag()
    {
    }

    /// <summary>Content that is not part of the logical structure (decorative images, ornaments), which screen readers skip.</summary>
    public static StructureTag Artifact { get; } = new();

    /// <summary>The structure type; <c>null</c> for <see cref="Artifact"/>.</summary>
    public string? Type { get; }

    /// <summary>Whether this is <see cref="Artifact"/>.</summary>
    public bool IsArtifact => Type is null;

    /// <summary>A description for readers who cannot see the content (figures and formulas need one for PDF/UA).</summary>
    public string? AlternateText { get; init; }

    /// <summary>The exact text the content stands for.</summary>
    public string? ActualText { get; init; }

    /// <summary>The language of the content as a BCP 47 tag, where it differs from the document's.</summary>
    public string? Language { get; init; }

    /// <summary>A title for the element.</summary>
    public string? Title { get; init; }

    /// <summary>For a table header cell: which cells it is the header of.</summary>
    public PdfTableScope? Scope { get; init; }

    /// <summary>For a list: how its items are labelled.</summary>
    public PdfListNumbering? ListNumbering { get; init; }

    /// <summary>
    /// The type of an element created inside this one to hold its content and children, such as
    /// <see cref="PdfStructureTypes.ListBody"/> inside a <see cref="PdfStructureTypes.ListItem"/>; runs of text tagged
    /// <see cref="PdfStructureTypes.Label"/> stay outside it. <c>null</c> (the default) for none.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>A heading tag of a level from 1 to 6 (<c>H1</c>–<c>H6</c>).</summary>
    public static StructureTag Heading(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 6);
        return new StructureTag("H" + (char)('0' + level));
    }

    /// <inheritdoc />
    public override string ToString() => Type ?? "Artifact";

    /// <summary>Adds an element for this tag (and its content element, if any) under <paramref name="parent"/>.</summary>
    /// <param name="parent">The parent element.</param>
    /// <param name="inline">Whether the element is inline: it takes its place among the parent's content where its first content is drawn.</param>
    internal (PdfStructureElement Element, PdfStructureElement Content) CreateElement(PdfStructureElement parent, bool inline = false)
    {
        var element = parent.AddChild(Type!, inline);
        element.AlternateText = AlternateText;
        element.ActualText = ActualText;
        element.Language = Language;
        element.Title = Title;
        element.Scope = Scope;
        element.ListNumbering = ListNumbering;
        return (element, ContentType is { } content ? element.AddChild(content) : element);
    }
}

/// <summary>A run of a paragraph's text with a structure tag (see <see cref="Paragraph.BeginTag"/>).</summary>
/// <param name="Start">The index of the run's first character in the paragraph's text.</param>
/// <param name="End">The index after its last character.</param>
/// <param name="Tag">The tag.</param>
public sealed record InlineTag(int Start, int End, StructureTag Tag);

/// <summary>
/// Begins and ends tags on a canvas for layout objects (blocks, table cells, tagged runs, links), using the structure
/// elements that <see cref="Blocks.FlowDocument.RenderTo"/> created for them. Does nothing in an untagged document.
/// </summary>
internal static class LayoutTagging
{
    /// <summary>Starts the content of <paramref name="owner"/>: its element, an artifact, or (if it has none) the enclosing tag.</summary>
    public static void Begin(PdfCanvas canvas, object owner)
    {
        if (canvas.Document is not { Tagged: true } document)
        {
            return;
        }

        if (!document.LayoutStructure.TryGetValue(owner, out var element))
        {
            canvas.BeginInherited();
        }
        else if (element is null)
        {
            canvas.BeginArtifact();
        }
        else
        {
            canvas.BeginTag(element);
        }
    }

    /// <summary>Starts an artifact (decorations, repeated headers).</summary>
    public static void BeginArtifact(PdfCanvas canvas, PdfArtifactKind kind = PdfArtifactKind.Layout)
    {
        if (canvas.Document is { Tagged: true })
        {
            canvas.BeginArtifact(kind);
        }
    }

    /// <summary>Ends what <see cref="Begin"/> or <see cref="BeginArtifact"/> started.</summary>
    public static void End(PdfCanvas canvas)
    {
        if (canvas.Document is { Tagged: true })
        {
            canvas.EndTag();
        }
    }

    /// <summary>Draws decorations as an artifact.</summary>
    public static void Decoration(PdfCanvas canvas, Action draw)
    {
        BeginArtifact(canvas);
        draw();
        End(canvas);
    }
}
