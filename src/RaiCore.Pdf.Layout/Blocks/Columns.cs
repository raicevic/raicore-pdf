namespace RaiCore.Pdf.Layout;

/// <summary>
/// The columns a <see cref="ContainerBlock"/> lays its children out in (CSS multi-column layout, Word section columns):
/// the content flows down the first column, then the next, and on to the next page. Children with
/// <see cref="BoxStyle.SpanColumns"/> go across all columns, splitting the content into sets of columns above and below
/// them. <see cref="PageBreak.Column"/> starts a new column.
/// </summary>
/// <remarks>
/// The number of columns and their width follow CSS (multi-column layout section 3.4): with only a <see cref="Count"/>,
/// that many columns share the width; with only a <see cref="Width"/>, as many columns of at least that width as fit;
/// with both, at most <see cref="Count"/> of them. <see cref="Widths"/> gives every column its own width instead.
/// A container with columns is a new formatting context: margins do not collapse through it and floats stay inside it.
/// </remarks>
public sealed record ColumnLayout
{
    /// <summary>The number of columns (CSS <c>column-count</c>); <c>null</c> for auto (from <see cref="Width"/>).</summary>
    public int? Count
    {
        get;
        init => field = value is < 1 ? throw new ArgumentOutOfRangeException(nameof(value), "A column count must be at least 1.") : value;
    }

    /// <summary>The smallest width of a column in points (CSS <c>column-width</c>); <c>null</c> for auto (from <see cref="Count"/>).</summary>
    public double? Width
    {
        get;
        init => field = value is <= 0 or double.NaN ? throw new ArgumentOutOfRangeException(nameof(value), "A column width must be positive.") : value;
    }

    /// <summary>
    /// The widths of the columns in points, from the first to the last, each with the gap after it (the last gap is
    /// ignored), like Word's columns of unequal width; <c>null</c> (the default) for equal columns from
    /// <see cref="Count"/>, <see cref="Width"/> and <see cref="Gap"/>. The columns keep these widths even when they do
    /// not fill the container.
    /// </summary>
    public IReadOnlyList<(double Width, double Gap)>? Widths { get; init; }

    /// <summary>The space between columns in points (CSS <c>column-gap</c>; <c>normal</c> is 1em).</summary>
    public double Gap
    {
        get;
        init => field = value is < 0 or double.NaN ? throw new ArgumentOutOfRangeException(nameof(value), "A column gap cannot be negative.") : value;
    }

    /// <summary>
    /// The line drawn in the middle of each gap between two columns that have content (CSS <c>column-rule</c>, Word's
    /// line between columns); <see cref="BorderSide.None"/> (the default) for none.
    /// </summary>
    public BorderSide Rule { get; init; }

    /// <summary>How the content is spread over the columns (CSS <c>column-fill</c>).</summary>
    public ColumnFill Fill { get; init; }

    /// <summary>The number of columns and their widths and gaps in a container's content width.</summary>
    internal IReadOnlyList<(double Width, double Gap)> Resolve(double available)
    {
        if (Widths is { Count: > 0 } widths)
        {
            return widths;
        }

        // CSS multi-column layout section 3.4 (the pseudo-algorithm), with at least one column.
        var gap = Gap;
        int count;
        if (Width is { } width)
        {
            var fitting = (int)Math.Clamp(Math.Floor((available + gap) / (width + gap)), 1, int.MaxValue);
            count = Count is { } most ? Math.Min(most, fitting) : fitting;
        }
        else
        {
            count = Count ?? 1;
        }

        count = Math.Min(count, MaxColumns);
        var columnWidth = Math.Max(0, ((available + gap) / count) - gap);
        return [.. Enumerable.Repeat((columnWidth, gap), count)];
    }

    /// <summary>More columns than this are not made (a guard against absurd counts).</summary>
    internal const int MaxColumns = 1000;
}

/// <summary>How content is spread over a container's columns (CSS <c>column-fill</c>).</summary>
public enum ColumnFill
{
    /// <summary>
    /// The columns of the last set (before a block that spans the columns, or at the end of the container) are made as
    /// short as they can be with the content still fitting in them, so they end at about the same height (CSS
    /// <c>balance</c>; Word's continuous section break).
    /// </summary>
    Balance,

    /// <summary>
    /// The columns are filled one after the other (CSS <c>auto</c>): when the container's height is fixed or the
    /// columns continue on the next page; otherwise they are balanced, as in CSS.
    /// </summary>
    Auto,
}
