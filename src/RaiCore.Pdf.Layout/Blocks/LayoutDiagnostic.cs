namespace RaiCore.Pdf.Layout;

/// <summary>
/// Content that did not fit where it was laid out (see <see cref="FlowDocument.Diagnostics"/>): wider than its box,
/// taller than a page, or scaled down to fit one.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Block">The block it happened in: the paragraph, table or image.</param>
/// <param name="Page">The 0-based page it happened on.</param>
/// <param name="Message">A sentence describing it, with the sizes involved.</param>
public sealed record LayoutDiagnostic(LayoutDiagnosticKind Kind, Block Block, int Page, string Message);

/// <summary>The kinds of <see cref="LayoutDiagnostic"/>.</summary>
public enum LayoutDiagnosticKind
{
    /// <summary>
    /// Content wider than its box: a line with an unbreakable word, image or inline box, or a block (a table whose
    /// columns need more room, a box or image with a fixed width) wider than the room it has. It is drawn past the box's edge (where a page may cut it off), unless the box clips its
    /// overflow on purpose (<see cref="BoxStyle.ClipOverflow"/>).
    /// </summary>
    Overflow,

    /// <summary>Content taller than a whole page that cannot be split (a line, made tall by an image or inline box): it is cut at the page's bottom.</summary>
    Clipped,

    /// <summary>An image taller than a page, scaled down to fit on one.</summary>
    ScaledToFit,
}
