using RaiCore.Pdf.Xfa.Form;
using RaiCore.Pdf.Xfa.Template;

namespace RaiCore.Pdf.Xfa.Layout;

/// <summary>A content area of a page area: where the form's flowed content goes, in page coordinates (y downwards).</summary>
internal sealed record ContentArea(double X, double Y, double W, double H, string? Id, string? Name);

/// <summary>A page laid out: its page area, size, and the boxes placed on it (in page coordinates, y downwards).</summary>
internal sealed class LaidPage
{
    public required FormNode PageArea { get; set; }

    public required double Width { get; set; }

    public required double Height { get; set; }

    public List<(LayoutBox Box, double X, double Y)> Items { get; } = [];
}

/// <summary>
/// Paginates the form (XFA 3.3, "Layout for Dynamic Forms"): the root subform's content flows into the content areas of
/// the page areas, in order (<c>orderedOccurrence</c>, each page area up to its maximum occurrences) or by page position
/// (<c>simplexPaginated</c>, <c>duplexPaginated</c>). A flowed container that does not fit is split between its
/// children, with its overflow trailer at the bottom of the content area and its leader at the top of the next; breaks
/// go to the next content area or page area (or a named one); keep rules keep objects with the next or previous one.
/// </summary>
internal sealed class Paginator
{
    private const double Epsilon = 0.01;

    private readonly MergedForm _form;
    private readonly ResourceLimits _limits;
    private readonly Action<XfaDiagnosticKind, string>? _report;
    private readonly List<LaidPage> _pages = [];
    private readonly Dictionary<FormNode, int> _uses = [];
    private List<ContentArea> _areas = [];
    private int _area;
    private double _cursor;
    private double _offsetX;

    public Paginator(MergedForm form, ResourceLimits limits, Action<XfaDiagnosticKind, string>? report)
    {
        _form = form;
        _limits = limits;
        _report = report;
    }

    /// <summary>The page size of a page area, from its <c>medium</c> (Letter by default).</summary>
    public static (double Width, double Height) PageSize(FormNode pageArea)
    {
        var medium = pageArea.Template.Child("medium");
        var (shortSide, longSide) = (medium.Attr("stock") ?? "letter").ToLowerInvariant() switch
        {
            "a4" => (595.28, 841.89),
            "a3" => (841.89, 1190.55),
            "a5" => (419.53, 595.28),
            "legal" => (612.0, 1008.0),
            "tabloid" or "ledger" => (792.0, 1224.0),
            "executive" => (522.0, 756.0),
            _ => (612.0, 792.0),
        };
        shortSide = medium.MeasureOrNull("short") is { } s and > 0 ? Math.Min(s, 14400) : shortSide;
        longSide = medium.MeasureOrNull("long") is { } l and > 0 ? Math.Min(l, 14400) : longSide;
        return medium.Attr("orientation") == "landscape" ? (longSide, shortSide) : (shortSide, longSide);
    }

    /// <summary>A page area's content areas (the whole page when it has none).</summary>
    public static List<ContentArea> ContentAreas(FormNode pageArea)
    {
        var areas = pageArea.Template.Children("contentArea")
            .Select(c => new ContentArea(c.Measure("x"), c.Measure("y"), Math.Max(c.Measure("w"), 1), Math.Max(c.Measure("h"), 1), c.Attr("id"), c.Attr("name")))
            .ToList();
        if (areas.Count == 0)
        {
            var (width, height) = PageSize(pageArea);
            areas.Add(new ContentArea(0, 0, width, height, null, null));
        }

        return areas;
    }

    /// <summary>The width the form's content is laid out at: the first content area's.</summary>
    public double ContentWidth => _form.PageAreas.Count > 0 ? ContentAreas(First())[0].W : 612;

    private FormNode First() => _form.PageAreas.FirstOrDefault(p => DataMerger.Occurrence(p.Template).Max != 0) ?? _form.PageAreas[0];

    /// <summary>Paginates the root's laid out boxes: the children of a flowed root, or the positioned root itself.</summary>
    public List<LaidPage> Paginate(IReadOnlyList<LayoutBox> boxes, double offsetX)
    {
        _offsetX = offsetX;
        if (_form.PageAreas.Count == 0)
        {
            _report?.Invoke(XfaDiagnosticKind.Layout, "The form has no page set: its content is laid out on Letter pages.");
            _form.PageAreas.Add(new FormNode(NodeKind.Area, new System.Xml.Linq.XElement("pageArea"), null));
        }

        NewPage(null);
        var queue = new LinkedList<LayoutBox>(boxes);
        BreakSpec? pending = null;
        while (queue.First is { } first)
        {
            ResourceLimits.Checkpoint();
            var box = first.Value;
            queue.RemoveFirst();
            if (pending is not null)
            {
                Break(pending);
                pending = null;
            }

            if (box.BreakBefore is { } before)
            {
                Break(before);
            }

            var area = _areas[_area];
            var remaining = area.H - _cursor;
            if (box.KeepNext && queue.First?.Value is { Splittable: false } next && !AtTop
                && box.ExtentHeight <= remaining + Epsilon && box.ExtentHeight + next.ExtentHeight > remaining + Epsilon
                && box.ExtentHeight + next.ExtentHeight <= area.H + Epsilon)
            {
                NextContentArea();
                remaining = _areas[_area].H;
            }

            var (head, tail, split) = Fragment(box, remaining);
            if (head is null)
            {
                if (AtTop)
                {
                    _report?.Invoke(XfaDiagnosticKind.Layout, $"{box.Node?.Path ?? "A line of objects"} is taller than its content area and is cut off.");
                    Place(box);
                    pending = box.BreakAfter;
                }
                else
                {
                    NextContentArea();
                    queue.AddFirst(box);
                }

                continue;
            }

            Place(head);
            if (tail is not null)
            {
                if (split is null)
                {
                    NextContentArea();
                }

                queue.AddFirst(tail);
            }
            else
            {
                pending = box.BreakAfter;
            }
        }

        LastPage();
        return _pages;
    }

    /// <summary>Whether nothing is placed in the current content area yet.</summary>
    private bool AtTop => _cursor <= Epsilon;

    /// <summary>Whether the current page has nothing placed yet.</summary>
    private bool AtPageTop => _area == 0 && AtTop && _pages[^1].Items.Count == 0;

    private void Place(LayoutBox box)
    {
        var area = _areas[_area];
        _pages[^1].Items.Add((box, area.X + _offsetX + box.X, area.Y + _cursor));
        _cursor += box.ExtentHeight;
    }

    /// <summary>
    /// Splits a box to fit a height: the part that fits (or <c>null</c>), the rest (or <c>null</c>), and the break that
    /// ended the first part, if a break did.
    /// </summary>
    private static (LayoutBox? Head, LayoutBox? Tail, BreakSpec? Break) Fragment(LayoutBox box, double available)
    {
        using var nesting = ResourceLimits.EnterNesting();
        if (!box.ContainsBreak && box.ExtentHeight <= available + Epsilon)
        {
            return (box, null, null);
        }

        if (box.Content is { SplitsText: true, Text: { } } content)
        {
            return SplitText(box, content, available);
        }

        if (!box.Splittable || box.Children.Count == 0)
        {
            return (null, box, null);
        }

        var children = box.Children;
        var trailer = box.Trailer?.ExtentHeight ?? 0;
        var limit = available - box.Margin.Bottom - trailer;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (i > 0 && (child.BreakBefore ?? children[i - 1].BreakAfter) is { } brk)
            {
                return Split(box, i, null, null, brk);
            }

            var top = box.Margin.Top + child.Y;
            if (!child.ContainsBreak && top + child.ExtentHeight <= limit + Epsilon)
            {
                continue;
            }

            var (head, tail, inner) = Fragment(child, limit - top);
            if (head is null)
            {
                // Nothing of it fits: what it is kept with goes with it.
                var j = i;
                while (j > 0 && (children[j - 1].KeepNext || children[j].KeepPrevious))
                {
                    j--;
                }

                return j == 0 ? (null, box, null) : Split(box, j, null, null, null);
            }

            if (tail is null)
            {
                continue;
            }

            return Split(box, i, head, tail, inner);
        }

        return (box, null, null);
    }

    /// <summary>
    /// Splits a box's children before child <paramref name="index"/> (or within it: <paramref name="head"/> ends the first
    /// part and <paramref name="tail"/> starts the rest), adding the overflow trailer and leader.
    /// </summary>
    private static (LayoutBox Head, LayoutBox Tail, BreakSpec? Break) Split(LayoutBox box, int index, LayoutBox? head, LayoutBox? tail, BreakSpec? brk)
    {
        var first = box.Children.Take(index).ToList();
        if (head is not null)
        {
            first.Add(head);
        }

        var bottom = first.Count == 0 ? 0 : first.Max(c => c.Y + c.ExtentHeight);
        if (box.Trailer is { } trailer)
        {
            first.Add(trailer.At(0, bottom));
            bottom += trailer.ExtentHeight;
        }

        var rest = new List<LayoutBox>();
        if (tail is not null)
        {
            rest.Add(tail);
        }

        rest.AddRange(box.Children.Skip(index + (tail is null ? 0 : 1)));
        var shift = rest.Count == 0 ? 0 : rest[0].Y;
        var moved = new List<LayoutBox>();
        var y = 0.0;
        if (box.Leader is { } leader)
        {
            moved.Add(leader.At(0, 0));
            y = leader.ExtentHeight;
        }

        foreach (var child in rest)
        {
            child.Y = child.Y - shift + y;
            moved.Add(child);
        }

        var restHeight = moved.Count == 0 ? 0 : moved.Max(c => c.Y + c.ExtentHeight);
        var headBox = box.Fragment(first, box.Margin.Top + bottom + box.Margin.Bottom);
        headBox.BreakBefore = null;
        var tailBox = box.Fragment(moved, box.Margin.Top + restHeight + box.Margin.Bottom);
        tailBox.BreakBefore = brk;
        tailBox.BreakAfter = box.BreakAfter;
        tailBox.ContainsBreak = moved.Skip(1).Any(c => c.BreakBefore is not null) || moved.SkipLast(1).Any(c => c.BreakAfter is not null) || moved.Any(c => c.ContainsBreak);
        return (headBox, tailBox, brk);
    }

    /// <summary>
    /// Splits a draw's or field's text between its lines: the lines that fit stay, the rest go on in a box of their own
    /// (with the same width, margins and border; the caption only with the first part).
    /// </summary>
    private static (LayoutBox? Head, LayoutBox? Tail, BreakSpec? Break) SplitText(LayoutBox box, WidgetContent content, double available)
    {
        var text = content.Text!;
        var top = content.Margin.Top + content.UiMargin.Top;
        var bottom = content.Margin.Bottom + content.UiMargin.Bottom;
        var limit = available - bottom - top + content.TextTop;
        var end = content.TextBottom ?? text.Height;
        double cut = content.TextTop;
        var more = false;
        foreach (var paragraph in text.Paragraphs)
        {
            var lines = paragraph.Layout?.Lines;
            var count = lines?.Count ?? 0;
            for (var i = 0; i < Math.Max(count, 1); i++)
            {
                var lineBottom = paragraph.Top + (lines is null || count == 0 ? paragraph.Height : lines[i].Top + lines[i].Height);
                if (lineBottom <= content.TextTop + Epsilon || lineBottom > end + Epsilon)
                {
                    continue;
                }

                if (lineBottom <= limit + Epsilon)
                {
                    cut = lineBottom;
                }
                else
                {
                    more = true;
                    break;
                }
            }

            if (more)
            {
                break;
            }
        }

        if (!more)
        {
            return (box, null, null);
        }

        if (cut <= content.TextTop + Epsilon)
        {
            return (null, box, null);
        }

        var head = new LayoutBox
        {
            Node = box.Node,
            X = box.X,
            Y = box.Y,
            W = box.W,
            H = top + (cut - content.TextTop) + bottom,
            Margin = box.Margin,
            Content = content with { TextBottom = cut, Para = content.Para with { VAlign = "top" } },
            Splittable = true,
            KeepPrevious = box.KeepPrevious,
        };
        var tail = new LayoutBox
        {
            Node = box.Node,
            X = box.X,
            W = box.W,
            H = top + (end - cut) + bottom,
            Margin = box.Margin,
            Content = content with { TextTop = cut, Caption = null, Para = content.Para with { VAlign = "top" } },
            Splittable = true,
            KeepNext = box.KeepNext,
        };
        return (head, tail, null);
    }

    private void Break(BreakSpec brk)
    {
        if (brk.TargetType == "contentArea")
        {
            ContentAreaBreak(brk);
            return;
        }

        var target = brk.Area ?? (brk.Target is { } reference ? PageAreaOf(reference) : null);
        var page = _pages[^1];
        if (AtPageTop && !brk.StartNew && target is not null && target != page.PageArea)
        {
            // An empty page takes the target's page area instead of staying blank.
            _uses[page.PageArea]--;
            _uses[target] = _uses.GetValueOrDefault(target) + 1;
            page.PageArea = target;
            (page.Width, page.Height) = PageSize(target);
            _areas = ContentAreas(target);
        }

        if (AtPageTop && !brk.StartNew && (target is null || target == page.PageArea) && Parity(brk))
        {
            return;
        }

        NewPage(target);
        if (!Parity(brk))
        {
            NewPage(target);
        }
    }

    /// <summary>
    /// A break to a content area: the next one, or a named one (<c>#id</c>, a name, or <c>Page2.#contentArea</c> for the
    /// first content area of a page area), on this page when it comes later on it, else on a new page of its page area.
    /// </summary>
    private void ContentAreaBreak(BreakSpec brk)
    {
        if (brk.Target is { } reference && FindContentArea(reference) is { } target)
        {
            var (pageArea, index) = target;
            if (pageArea == _pages[^1].PageArea && (index > _area || (index == _area && AtTop && !brk.StartNew)))
            {
                if (index != _area)
                {
                    (_area, _cursor) = (index, 0);
                }

                return;
            }

            Break(new BreakSpec("pageArea", "#" + (pageArea.Template.Attr("id") ?? string.Empty), brk.StartNew) { Area = pageArea });
            (_area, _cursor) = (Math.Min(index, _areas.Count - 1), 0);
            return;
        }

        if (!AtTop || brk.StartNew)
        {
            NextContentArea();
        }
    }

    /// <summary>A content area by reference, and its page area.</summary>
    private (FormNode PageArea, int Index)? FindContentArea(string reference)
    {
        var parts = reference.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var last = parts[^1];
        IEnumerable<FormNode> candidates = _form.PageAreas;
        if (parts.Length > 1)
        {
            var area = parts[^2].TrimStart('#');
            candidates = _form.PageAreas.Where(p => p.Name == area || p.Template.Attr("id") == area);
        }

        // The current page area first, then the others in order.
        var current = _pages[^1].PageArea;
        foreach (var pageArea in candidates.OrderBy(p => p == current ? 0 : 1))
        {
            var areas = ContentAreas(pageArea);
            for (var i = 0; i < areas.Count; i++)
            {
                var matches = last == "#contentArea" || (last.StartsWith('#') ? areas[i].Id == last[1..] : areas[i].Name == last);
                if (matches && (pageArea != current || i > _area || parts.Length > 1 || last != "#contentArea"))
                {
                    return (pageArea, i);
                }
            }
        }

        return null;
    }

    /// <summary>Whether the current page is on the side a <c>pageEven</c> or <c>pageOdd</c> break asks for.</summary>
    private bool Parity(BreakSpec brk) => brk.TargetType switch
    {
        "pageEven" => _pages.Count % 2 == 0,
        "pageOdd" => _pages.Count % 2 == 1,
        _ => true,
    };

    private FormNode? PageAreaOf(string reference)
    {
        var key = reference.TrimStart('#');
        var name = key.Split('.')[^1].Split('[')[0];
        var found = _form.PageAreas.FirstOrDefault(p => p.Template.Attr("id") == key) ?? _form.PageAreas.FirstOrDefault(p => p.Name == name);
        if (found is null)
        {
            _report?.Invoke(XfaDiagnosticKind.Layout, $"The break target '{reference}' is not a page area of the form.");
        }

        return found;
    }

    private void NextContentArea()
    {
        if (_area + 1 < _areas.Count)
        {
            _area++;
            _cursor = 0;
            return;
        }

        NewPage(null);
    }

    private void NewPage(FormNode? target)
    {
        if (_pages.Count >= _limits.MaxPages)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxPages), _limits.MaxPages,
                $"The form lays out to more than ResourceLimits.MaxPages ({_limits.MaxPages}) pages.");
        }

        var pageArea = target ?? Choose();
        _uses[pageArea] = _uses.GetValueOrDefault(pageArea) + 1;
        var (width, height) = PageSize(pageArea);
        _pages.Add(new LaidPage { PageArea = pageArea, Width = width, Height = height });
        _areas = ContentAreas(pageArea);
        _area = 0;
        _cursor = 0;
    }

    /// <summary>The page area of the next page.</summary>
    private FormNode Choose()
    {
        var areas = _form.PageAreas;
        if (_form.Relation is "simplexPaginated" or "duplexPaginated")
        {
            var position = _pages.Count == 0 ? new[] { "first", "only", "any" } : ["rest", "any"];
            var odd = _pages.Count % 2 == 0;
            foreach (var wanted in position)
            {
                var match = areas.FirstOrDefault(a => (a.Template.Attr("pagePosition") ?? "any") == wanted
                    && (_form.Relation == "simplexPaginated" || (a.Template.Attr("oddOrEven") ?? "any") is "any" || (a.Template.Attr("oddOrEven") == "odd") == odd));
                if (match is not null)
                {
                    return match;
                }
            }

            return areas[0];
        }

        if (_pages.Count == 0)
        {
            return First();
        }

        var current = _pages[^1].PageArea;
        if (Available(current))
        {
            return current;
        }

        var index = areas.IndexOf(current);
        for (var i = index + 1; i < areas.Count; i++)
        {
            if (Available(areas[i]))
            {
                return areas[i];
            }
        }

        return current;
    }

    private bool Available(FormNode pageArea)
    {
        var max = DataMerger.Occurrence(pageArea.Template).Max;
        return max < 0 || _uses.GetValueOrDefault(pageArea) < max;
    }

    /// <summary>The last page of a paginated page set takes the page area for the last (or only) page, where its content fits.</summary>
    private void LastPage()
    {
        if (_form.Relation is not ("simplexPaginated" or "duplexPaginated"))
        {
            return;
        }

        var position = _pages.Count == 1 ? "only" : "last";
        var last = _form.PageAreas.FirstOrDefault(a => a.Template.Attr("pagePosition") == position);
        var page = _pages[^1];
        if (last is null || last == page.PageArea)
        {
            return;
        }

        var used = page.Items.Count == 0 ? 0 : page.Items.Max(i => i.Y + i.Box.ExtentHeight) - ContentAreas(page.PageArea)[0].Y;
        var area = ContentAreas(last)[0];
        if (used <= area.H + Epsilon)
        {
            var shiftX = area.X - ContentAreas(page.PageArea)[0].X;
            var shiftY = area.Y - ContentAreas(page.PageArea)[0].Y;
            for (var i = 0; i < page.Items.Count; i++)
            {
                var (box, x, y) = page.Items[i];
                page.Items[i] = (box, x + shiftX, y + shiftY);
            }

            page.PageArea = last;
            (page.Width, page.Height) = PageSize(last);
        }
    }
}
