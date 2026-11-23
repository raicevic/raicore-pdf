using System.Globalization;
using System.Xml.Linq;
using RaiCore.Pdf.Sheets.Workbooks;

namespace RaiCore.Pdf.Sheets;

/// <summary>A row of a <see cref="Worksheet"/>: whether it is hidden, its outline (grouping) level and its height.</summary>
public sealed class Row
{
    private readonly int _row;

    internal Row(Worksheet worksheet, int row)
    {
        Worksheet = worksheet;
        _row = row;
    }

    /// <summary>The worksheet the row is on.</summary>
    public Worksheet Worksheet { get; }

    /// <summary>The row's number, from 1.</summary>
    public int Index => _row + 1;

    /// <summary>
    /// Whether the row is hidden (also by a collapsed outline group, or by a filter). Rows the sheet does not list are
    /// hidden only when it hides rows by default.
    /// </summary>
    /// <exception cref="NotSupportedException">The workbook is read only (when set).</exception>
    public bool IsHidden
    {
        get => Element is { } row ? WorkbookNames.Flag(row, "hidden") : Worksheet.RowsHiddenByDefault;
        set
        {
            if (value == IsHidden)
            {
                return;
            }

            var row = Writable();
            row.SetAttributeValue("hidden", value ? "1" : null);
            Changed();
        }
    }

    /// <summary>The row's outline level: 0 when it is in no group, up to 7. Setting it groups the row.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is not from 0 to 7.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only (when set).</exception>
    public int OutlineLevel
    {
        get => Element is { } row ? Math.Clamp(WorkbookNames.Integer(row, "outlineLevel") ?? 0, 0, 7) : 0;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 7);
            if (value == OutlineLevel)
            {
                return;
            }

            var row = Writable();
            row.SetAttributeValue("outlineLevel", value == 0 ? null : value.ToString(CultureInfo.InvariantCulture));
            Worksheet.DeclareOutlineLevel(true, value);
            Changed();
        }
    }

    /// <summary>
    /// Whether the outline group that ends at this row (Excel puts its button on the row after the group) is
    /// collapsed. Collapsing a group also needs its rows hidden.
    /// </summary>
    /// <exception cref="NotSupportedException">The workbook is read only (when set).</exception>
    public bool IsCollapsed
    {
        get => Element is { } row && WorkbookNames.Flag(row, "collapsed");
        set
        {
            if (value == IsCollapsed)
            {
                return;
            }

            Writable().SetAttributeValue("collapsed", value ? "1" : null);
            Changed();
        }
    }

    /// <summary>The row's height in points if the sheet sets one, else <c>null</c> (the default height).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The height is not from 0 to 409 points.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only (when set).</exception>
    public double? Height
    {
        get => Element is { } row && WorkbookNames.Flag(row, "customHeight")
            && double.TryParse((string?)row.Attribute("ht"), NumberStyles.Float, CultureInfo.InvariantCulture, out var height) ? height : null;
        set
        {
            if (value is { } height && (!double.IsFinite(height) || height < 0 || height > 409))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A row is from 0 to 409 points high.");
            }

            var row = Writable();
            row.SetAttributeValue("ht", value?.ToString("R", CultureInfo.InvariantCulture));
            row.SetAttributeValue("customHeight", value is null ? null : "1");
            Changed();
        }
    }

    private XElement? Element => Worksheet.FindRow(_row);

    private XElement Writable()
    {
        Worksheet.Workbook.EnsureWritable("changed");
        return Worksheet.GetOrCreateRow(_row);
    }

    private void Changed() => Worksheet.Workbook.Package.MarkDirty(Worksheet.Part);
}

/// <summary>A column of a <see cref="Worksheet"/>: whether it is hidden, its outline (grouping) level and its width.</summary>
public sealed class Column
{
    private readonly int _column;

    internal Column(Worksheet worksheet, int column)
    {
        Worksheet = worksheet;
        _column = column;
    }

    /// <summary>The worksheet the column is on.</summary>
    public Worksheet Worksheet { get; }

    /// <summary>The column's number, from 1.</summary>
    public int Index => _column + 1;

    /// <summary>The column's letters (<c>A</c>, <c>AB</c>).</summary>
    public string Name => FormulaReferences.ColumnName(_column);

    /// <summary>Whether the column is hidden (also by a collapsed outline group).</summary>
    /// <exception cref="NotSupportedException">The workbook is read only (when set).</exception>
    public bool IsHidden
    {
        get => Element is { } column && WorkbookNames.Flag(column, "hidden");
        set
        {
            if (value == IsHidden)
            {
                return;
            }

            Writable().SetAttributeValue("hidden", value ? "1" : null);
            Changed();
        }
    }

    /// <summary>The column's outline level: 0 when it is in no group, up to 7. Setting it groups the column.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is not from 0 to 7.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only (when set).</exception>
    public int OutlineLevel
    {
        get => Element is { } column ? Math.Clamp(WorkbookNames.Integer(column, "outlineLevel") ?? 0, 0, 7) : 0;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 7);
            if (value == OutlineLevel)
            {
                return;
            }

            Writable().SetAttributeValue("outlineLevel", value == 0 ? null : value.ToString(CultureInfo.InvariantCulture));
            Worksheet.DeclareOutlineLevel(false, value);
            Changed();
        }
    }

    /// <summary>Whether the outline group next to this column (Excel puts its button after the group) is collapsed.</summary>
    /// <exception cref="NotSupportedException">The workbook is read only (when set).</exception>
    public bool IsCollapsed
    {
        get => Element is { } column && WorkbookNames.Flag(column, "collapsed");
        set
        {
            if (value == IsCollapsed)
            {
                return;
            }

            Writable().SetAttributeValue("collapsed", value ? "1" : null);
            Changed();
        }
    }

    /// <summary>
    /// The column's width in characters (of the default font's digits) if the sheet sets one, else <c>null</c> (the
    /// default width).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not from 0 to 255 characters.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only (when set).</exception>
    public double? Width
    {
        get => Element is { } column && double.TryParse((string?)column.Attribute("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            ? width : null;
        set
        {
            if (value is { } width && (!double.IsFinite(width) || width < 0 || width > 255))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A column is from 0 to 255 characters wide.");
            }

            var column = Writable();
            if (value is null)
            {
                var format = Worksheet.Root.Element(Worksheet.Ns + "sheetFormatPr");
                column.SetAttributeValue("width", format?.Attribute("defaultColWidth")?.Value ?? "9.140625");
                column.Attribute("customWidth")?.Remove();
            }
            else
            {
                column.SetAttributeValue("width", value.Value.ToString("R", CultureInfo.InvariantCulture));
                column.SetAttributeValue("customWidth", "1");
            }

            Changed();
        }
    }

    private XElement? Element => Worksheet.FindColumn(_column);

    private XElement Writable()
    {
        Worksheet.Workbook.EnsureWritable("changed");
        return Worksheet.GetOrCreateColumn(_column);
    }

    private void Changed() => Worksheet.Workbook.Package.MarkDirty(Worksheet.Part);
}
