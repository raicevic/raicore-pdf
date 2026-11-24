using RaiCore.Pdf.Xfa.Form;
using RaiCore.Pdf.Xfa.Template;

namespace RaiCore.Pdf.Xfa.Layout;

/// <summary>Tables (<c>layout="table"</c>): rows of cells in the table's columns, and overflow leaders and trailers.</summary>
internal sealed partial class Measurer
{
    /// <summary>The column widths of the tables being laid out, innermost last.</summary>
    private readonly Stack<double[]> _columns = new();

    private long _cells;

    /// <summary>How deep in laying out overflow leaders and trailers the measurer is.</summary>
    private int _overflow;

    /// <summary>Lays out a table: its rows (and the rows of sections in it) top to bottom, cells in its columns.</summary>
    private (double Width, double Height) Table(FormNode node, double inner, List<LayoutBox> children)
    {
        var columns = ColumnWidths(node, inner);
        _columns.Push(columns);
        var cells = _cells;
        _cells = 0;
        try
        {
            double y = 0, width = columns.Sum();
            foreach (var child in Laid(node))
            {
                var box = Measure(child, Math.Max(inner, width));
                box.X = 0;
                box.Y = y;
                y += box.ExtentHeight;
                width = Math.Max(width, box.ExtentWidth);
                children.Add(box);
            }

            return (width, y);
        }
        finally
        {
            _columns.Pop();
            _cells = cells;
        }
    }

    /// <summary>Places a row's cells in the current table's columns (or one after another outside a table).</summary>
    private (double Width, double Height) Row(FormNode node, double inner, List<LayoutBox> cells)
    {
        var columns = _columns.Count > 0 ? _columns.Peek() : [];
        var column = 0;
        double x = 0, height = 0;
        foreach (var cell in Laid(node))
        {
            if (++_cells > _limits.MaxTableCells)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxTableCells), _limits.MaxTableCells,
                    $"A table has more than ResourceLimits.MaxTableCells ({_limits.MaxTableCells}) cells.");
            }

            var span = cell.Template.Int("colSpan", 1);
            span = span < 0 ? Math.Max(columns.Length - column, 1) : Math.Max(span, 1);
            LayoutBox box;
            if (column < columns.Length)
            {
                var width = 0.0;
                for (var i = column; i < Math.Min(column + span, columns.Length); i++)
                {
                    width += columns[i];
                }

                box = Measure(cell, width, forcedWidth: width);
            }
            else
            {
                box = Measure(cell, inner);
            }

            box.X = x;
            box.Y = 0;
            x += box.ExtentWidth;
            column += span;
            height = Math.Max(height, box.ExtentHeight);
            cells.Add(box);
        }

        return (Math.Max(x, columns.Sum()), height);
    }

    /// <summary>
    /// The table's column widths: its <c>columnWidths</c>, where <c>-1</c> (or a missing width) is as wide as the
    /// widest cell in the column.
    /// </summary>
    private double[] ColumnWidths(FormNode node, double inner)
    {
        var given = (node.Template.Attr("columnWidths") ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Take(1024)
            .Select(text => Measurement.TryParse(text, "in", out var value) && value >= 0 ? value : (double?)null)
            .ToList();
        var rows = Laid(node).Where(r => r.Template.Attr("layout") == "row").ToList();
        var count = Math.Max(given.Count, rows.Count == 0 ? 0 : rows.Max(r => Laid(r).Count()));
        var widths = new double[Math.Min(count, 1024)];
        for (var i = 0; i < widths.Length; i++)
        {
            if (i < given.Count && given[i] is { } width)
            {
                widths[i] = width;
                continue;
            }

            // An automatic column: its widest cell without a span.
            foreach (var row in rows)
            {
                var cells = Laid(row).ToList();
                var column = 0;
                foreach (var cell in cells)
                {
                    var span = cell.Template.Int("colSpan", 1);
                    if (column == i && span == 1)
                    {
                        widths[i] = Math.Max(widths[i], Measure(cell, inner).ExtentWidth);
                    }

                    column += span < 0 ? widths.Length : Math.Max(span, 1);
                    if (column > i)
                    {
                        break;
                    }
                }
            }
        }

        return widths;
    }

    /// <summary>
    /// An overflow leader or trailer of a container, laid out (<c>#id</c>, or a name found in or near it). The leaders
    /// and trailers of what a leader or trailer contains are ignored, and so is a reference to the container itself or
    /// to one that contains it: they would lead around in circles.
    /// </summary>
    private LayoutBox? Overflow(FormNode node, string reference, double inner)
    {
        if (_overflow > 0)
        {
            return null;
        }

        var target = Find(node, reference);
        var circular = false;
        for (var ancestor = node; ancestor is not null && !circular; ancestor = ancestor.Parent)
        {
            circular = ancestor == target;
        }

        if (target is null || circular)
        {
            _report?.Invoke(XfaDiagnosticKind.UnsupportedContent, $"The overflow leader or trailer '{reference}' of {node.Path} cannot be found.");
            return null;
        }

        _overflow++;
        try
        {
            return Measure(target, inner);
        }
        finally
        {
            _overflow--;
        }
    }

    /// <summary>
    /// A form object by reference: <c>#id</c>, or a SOM name (its last part), looked for in the container, then in the
    /// whole form; an object only the template has (a leader that is not in the form) is made without data.
    /// </summary>
    private FormNode? Find(FormNode node, string reference)
    {
        if (reference.StartsWith('#'))
        {
            var id = reference[1..];
            if (_form.Ids.TryGetValue(id, out var byId))
            {
                return byId;
            }

            return _form.TemplateIds.TryGetValue(id, out var element) ? _form.Detach(element, node) : null;
        }

        var name = reference.Split('.')[^1].Split('[')[0].Trim();
        var found = Descendants(node).FirstOrDefault(n => n.Name == name);
        if (found is not null)
        {
            return found;
        }

        var template = node.Template.Descendants().FirstOrDefault(e => e.Attr("name") == name && e.Name.LocalName == "subform")
            ?? _form.Root.Template.Descendants().FirstOrDefault(e => e.Attr("name") == name && e.Name.LocalName == "subform");
        return template is null ? null : _form.Detach(template, node);
    }

    private static IEnumerable<FormNode> Descendants(FormNode node)
    {
        var stack = new Stack<FormNode>(node.Children.AsEnumerable().Reverse());
        var visited = 0;
        while (stack.Count > 0 && visited++ < 1_000_000)
        {
            var next = stack.Pop();
            yield return next;
            for (var i = next.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(next.Children[i]);
            }
        }
    }
}
