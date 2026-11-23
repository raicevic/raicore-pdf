using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// What a sheet has that the conversion leaves out or simplifies, counted by kind while it is read, and reported as one
/// <see cref="SheetDiagnostic"/> per kind. A kind that becomes supported is simply no longer counted.
/// </summary>
internal sealed class UnsupportedContent
{
    private static readonly SheetDiagnosticKind[] Kinds = Enum.GetValues<SheetDiagnosticKind>();

    private readonly int[] _counts = new int[Kinds.Length];

    /// <summary>How many there are of a kind.</summary>
    public int this[SheetDiagnosticKind kind] => _counts[(int)kind];

    /// <summary>Counts content of a kind.</summary>
    public void Add(SheetDiagnosticKind kind, int count = 1)
    {
        if (count > 0)
        {
            _counts[(int)kind] = (int)Math.Min((long)_counts[(int)kind] + count, int.MaxValue);
        }
    }

    /// <summary>Reports each kind counted, in the order of <see cref="SheetDiagnosticKind"/>, to <paramref name="diagnostics"/>.</summary>
    public void Report(string? sheetName, Action<SheetDiagnostic>? diagnostics)
    {
        if (diagnostics is null)
        {
            return;
        }

        foreach (var kind in Kinds)
        {
            if (_counts[(int)kind] is var count and > 0)
            {
                diagnostics(new SheetDiagnostic(sheetName, kind, Message(kind, count)));
            }
        }
    }

    /// <summary>The message of a kind: what was left out or simplified, and how many.</summary>
    internal static string Message(SheetDiagnosticKind kind, int count)
    {
        string Of(string one, string many, string what) => $"{count} {(count == 1 ? one + " is" : many + " are")} {what}.";
        return kind switch
        {
            SheetDiagnosticKind.Chart => Of("chart", "charts", "left out"),
            SheetDiagnosticKind.ChartSheet => "The chart sheet is left out.",
            SheetDiagnosticKind.Picture => Of("picture", "pictures", "left out"),
            SheetDiagnosticKind.Shape => Of("shape, text box or form control", "shapes, text boxes and form controls", "left out"),
            SheetDiagnosticKind.OleObject => Of("embedded object or ActiveX control", "embedded objects and ActiveX controls", "left out"),
            SheetDiagnosticKind.Comment => Of("comment", "comments", "left out"),
            SheetDiagnosticKind.ConditionalFormatting => Of("conditional formatting rule", "conditional formatting rules", "not applied"),
            SheetDiagnosticKind.Sparkline => Of("sparkline", "sparklines", "left out"),
            SheetDiagnosticKind.PivotTable => Of("pivot table", "pivot tables", "shown as plain cells, without the pivot table style"),
            SheetDiagnosticKind.HeaderFooter => Of("header or footer", "headers and footers", "left out"),
            SheetDiagnosticKind.PageBreak => Of("page break set by hand", "page breaks set by hand", "not applied"),
            SheetDiagnosticKind.RichText => Of("cell with rich text", "cells with rich text", "shown in the cell's font only"),
            SheetDiagnosticKind.RotatedText => Of("cell with rotated text", "cells with rotated text", "shown horizontally"),
            SheetDiagnosticKind.ShrinkToFit => Of("cell set to shrink to fit", "cells set to shrink to fit", "shown at the full font size"),
            _ => Of("layout problem", "layout problems", "found"),
        };
    }
}

/// <summary>
/// The layout problems of a sheet as the layout engine reports them while the sheet is rendered (content wider than
/// the page, taller than a page, images scaled down), with the pages they are on, reported as one diagnostic.
/// </summary>
internal sealed class LayoutProblems
{
    /// <summary>The most page numbers a message lists.</summary>
    private const int MaxListed = 10;

    private readonly SortedSet<int>[] _pages = [[], [], []];

    /// <summary>
    /// Wires a flow document's diagnostics to this collection, when diagnostics are wanted; the flow's pages are numbered
    /// in the document from <paramref name="firstPage"/>.
    /// </summary>
    public static LayoutProblems? Collect(FlowDocument flow, Action<SheetDiagnostic>? diagnostics, int firstPage)
    {
        if (diagnostics is null)
        {
            return null;
        }

        var problems = new LayoutProblems();
        flow.Diagnostics = d => problems.Add(d.Kind, firstPage + d.Page);
        return problems;
    }

    /// <summary>Notes a problem of a kind on a page (numbered from 1).</summary>
    public void Add(LayoutDiagnosticKind kind, int page)
    {
        var index = kind switch
        {
            LayoutDiagnosticKind.Overflow => 0,
            LayoutDiagnosticKind.Clipped => 1,
            _ => 2,
        };
        _pages[index].Add(page);
    }

    /// <summary>Reports the problems as one diagnostic, if there are any.</summary>
    public void Report(string? sheetName, Action<SheetDiagnostic>? diagnostics)
    {
        string[] phrases =
        [
            "Content too wide for the page or its cell is drawn past the edge on ",
            "Content taller than a page is cut off on ",
            "An image taller than a page is scaled down on ",
        ];
        var sentences = new List<string>();
        for (var i = 0; i < _pages.Length; i++)
        {
            if (_pages[i].Count > 0)
            {
                sentences.Add(phrases[i] + Pages(_pages[i]) + ".");
            }
        }

        if (sentences.Count > 0)
        {
            diagnostics?.Invoke(new SheetDiagnostic(sheetName, SheetDiagnosticKind.Layout, string.Join(' ', sentences)));
        }
    }

    /// <summary><c>page 3</c>, <c>pages 2 and 5</c>, <c>pages 2, 3 and 5</c>, or the first ten and how many more.</summary>
    internal static string Pages(SortedSet<int> pages)
    {
        if (pages.Count == 1)
        {
            return $"page {pages.Min}";
        }

        var listed = pages.Take(MaxListed).Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (pages.Count > MaxListed)
        {
            return $"pages {string.Join(", ", listed)} and {pages.Count - MaxListed} more";
        }

        return $"pages {string.Join(", ", listed[..^1])} and {listed[^1]}";
    }
}
