using System.Globalization;
using System.Xml.Linq;
using RaiCore.Pdf.Sheets.Workbooks;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets;

/// <summary>What a cell's value is.</summary>
public enum CellValueType
{
    /// <summary>Nothing (also a formula without a calculated value).</summary>
    Empty,

    /// <summary>Text.</summary>
    Text,

    /// <summary>A number.</summary>
    Number,

    /// <summary><c>TRUE</c> or <c>FALSE</c>.</summary>
    Boolean,

    /// <summary>A date or time: a number in a date or time number format.</summary>
    DateTime,

    /// <summary>An error value, such as <c>#DIV/0!</c> or <c>#N/A</c>.</summary>
    Error,
}

/// <summary>
/// A cell of a <see cref="Worksheet"/>: its value, formula and number format, read and written. A cell is a position:
/// after rows or columns are inserted or deleted, get the cell again at its new address.
/// </summary>
/// <remarks>
/// Writing a value keeps the cell's style (font, fill, borders, alignment) and replaces its formula. A new cell takes
/// the style of its row or column, as in Excel. Dates are numbers in a date number format: writing one to a cell
/// whose number format is not a date or time format gives it the short date format (<c>yyyy-mm-dd</c> as shown by
/// this library, the system's short date in Excel), or date and time when the time of day is not midnight.
/// </remarks>
public sealed class Cell
{
    /// <summary>The longest text a cell holds (Excel's limit).</summary>
    private const int MaxText = 32767;

    private readonly int _row;
    private readonly int _column;

    internal Cell(Worksheet worksheet, int row, int column)
    {
        Worksheet = worksheet;
        _row = row;
        _column = column;
    }

    /// <summary>The worksheet the cell is on.</summary>
    public Worksheet Worksheet { get; }

    /// <summary>The cell's row, from 1.</summary>
    public int Row => _row + 1;

    /// <summary>The cell's column, from 1.</summary>
    public int Column => _column + 1;

    /// <summary>The cell's address (<c>B3</c>).</summary>
    public string Address => FormulaReferences.Address(_row, _column);

    /// <summary>What the value is (for a formula, its last calculated value).</summary>
    public CellValueType Type => Read().Type;

    /// <summary>
    /// The value: a <see cref="string"/>, a <see cref="double"/>, a <see cref="bool"/>, a <see cref="System.DateTime"/>,
    /// the text of an error (<c>#N/A</c>), or <c>null</c> when the cell is empty. For a formula, its last calculated value.
    /// </summary>
    public object? Value => Read().Value;

    /// <summary>The text of a <see cref="CellValueType.Text"/> cell, else <c>null</c>.</summary>
    public string? TextValue => Read() is { Type: CellValueType.Text, Value: string text } ? text : null;

    /// <summary>The number of a <see cref="CellValueType.Number"/> cell, or the serial number of a <see cref="CellValueType.DateTime"/> one; else <c>null</c>.</summary>
    public double? NumberValue => Read() is { Type: CellValueType.Number or CellValueType.DateTime } content ? content.Number : null;

    /// <summary>The value of a <see cref="CellValueType.Boolean"/> cell, else <c>null</c>.</summary>
    public bool? BooleanValue => Read() is { Type: CellValueType.Boolean, Value: bool value } ? value : null;

    /// <summary>The date of a <see cref="CellValueType.DateTime"/> cell (in the workbook's date system), else <c>null</c>.</summary>
    public DateTime? DateTimeValue => Read() is { Type: CellValueType.DateTime, Value: DateTime date } ? date : null;

    /// <summary>The error of an <see cref="CellValueType.Error"/> cell (<c>#DIV/0!</c>), else <c>null</c>.</summary>
    public string? ErrorValue => Read() is { Type: CellValueType.Error, Value: string error } ? error : null;

    /// <summary>The cell's formula without the leading <c>=</c> (<c>SUM(A1:A3)</c>), or <c>null</c> if it has none.</summary>
    public string? Formula
    {
        get
        {
            if (Element?.Element(Worksheet.Ns + "f") is not { } f)
            {
                return null;
            }

            if ((string?)f.Attribute("t") == "shared" && f.Value.Length == 0 && (string?)f.Attribute("si") is { } si)
            {
                return Worksheet.SharedFormula(si, _row, _column);
            }

            return f.Value.Length == 0 ? null : f.Value;
        }
    }

    /// <summary>Whether the cell has a formula.</summary>
    public bool HasFormula => Formula is not null;

    /// <summary>
    /// The cell's number format code (<c>General</c>, <c>0.00</c>, <c>yyyy-mm-dd</c>; built-in date formats as the
    /// invariant culture shows them). Setting it gives the cell a style that is its style with the number format.
    /// </summary>
    /// <exception cref="NotSupportedException">The workbook is read only (when set).</exception>
    public string NumberFormat
    {
        get => Worksheet.Workbook.Styles.FormatCode(Worksheet.Workbook.Styles.NumberFormatId(Style), Worksheet.Workbook.Invariant);
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            var workbook = Worksheet.Workbook;
            workbook.EnsureWritable("changed");
            var id = workbook.Styles.NumberFormatId(value, workbook.Invariant);
            SetStyle(workbook.Styles.WithNumberFormat(Style, id));
            workbook.Package.MarkDirty(Worksheet.Part);
        }
    }

    private XElement? Element => Worksheet.FindCell(_row, _column);

    private int Style => Element is { } c ? WorkbookNames.Integer(c, "s") ?? 0 : 0;

    /// <summary>The value as a spreadsheet shows it: in its number format, in a culture (the invariant one by default).</summary>
    /// <param name="culture">The culture, or <c>null</c> for the invariant culture.</param>
    /// <remarks>
    /// Numbers in the General format are shown with up to 15 significant digits (a spreadsheet may show fewer in a
    /// narrow column); booleans as <c>TRUE</c> and <c>FALSE</c>.
    /// </remarks>
    public string GetDisplayText(CultureInfo? culture = null)
    {
        var content = Read();
        var workbook = Worksheet.Workbook;
        var settings = workbook.Settings(culture);
        switch (content.Type)
        {
            case CellValueType.Text:
                return workbook.Styles.Format(Style, settings).FormatText((string)content.Value!).Text;
            case CellValueType.Number or CellValueType.DateTime:
                var format = workbook.Styles.Format(Style, settings);
                return ReferenceEquals(format, Xlsx.NumberFormat.General) ? General(content.Number, settings) : format.Format(content.Number, settings).Text;
            case CellValueType.Boolean:
                return (bool)content.Value! ? "TRUE" : "FALSE";
            case CellValueType.Error:
                return (string)content.Value!;
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// The value as stored, the same in every culture: text as it is, numbers with all their digits in the invariant
    /// culture, dates as ISO 8601, booleans as <c>TRUE</c> and <c>FALSE</c>.
    /// </summary>
    internal string RawText()
    {
        var content = Read();
        return content.Type switch
        {
            CellValueType.Text or CellValueType.Error => (string)content.Value!,
            CellValueType.Number => content.Number.ToString("R", CultureInfo.InvariantCulture),
            CellValueType.DateTime => ((DateTime)content.Value!) is var date && date.TimeOfDay == TimeSpan.Zero
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : date.ToString(date.Millisecond == 0 ? "yyyy-MM-ddTHH:mm:ss" : "yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
            CellValueType.Boolean => (bool)content.Value! ? "TRUE" : "FALSE",
            _ => string.Empty,
        };
    }

    /// <summary>Writes text (a string, also one that starts with <c>=</c>, which is not a formula); <c>null</c> clears the cell.</summary>
    /// <param name="value">The text, up to 32,767 characters.</param>
    /// <exception cref="ArgumentException">The text is longer than 32,767 characters.</exception>
    /// <exception cref="InvalidOperationException">The cell is part of an array formula.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only.</exception>
    public void SetValue(string? value)
    {
        if (value is null)
        {
            Clear();
            return;
        }

        if (value.Length > MaxText)
        {
            throw new ArgumentException($"A cell holds at most {MaxText} characters.", nameof(value));
        }

        var c = Prepare();
        var index = Worksheet.Workbook.Strings.Add(value);
        c.SetAttributeValue("t", "s");
        c.Add(new XElement(Worksheet.Ns + "v", index.ToString(CultureInfo.InvariantCulture)));
        Done();
    }

    /// <summary>Writes a number.</summary>
    /// <param name="value">The number.</param>
    /// <exception cref="ArgumentException">The number is not finite.</exception>
    /// <exception cref="InvalidOperationException">The cell is part of an array formula.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only.</exception>
    public void SetValue(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("A cell holds only finite numbers.", nameof(value));
        }

        var c = Prepare();
        c.Add(new XElement(Worksheet.Ns + "v", value.ToString("R", CultureInfo.InvariantCulture)));
        Done();
    }

    /// <summary>Writes a number.</summary>
    /// <inheritdoc cref="SetValue(double)"/>
    public void SetValue(int value) => SetValue((double)value);

    /// <summary>Writes a number (stored as a double, as spreadsheets do: about 15 significant digits).</summary>
    /// <inheritdoc cref="SetValue(double)"/>
    public void SetValue(long value) => SetValue((double)value);

    /// <summary>Writes a number (stored as a double, as spreadsheets do: about 15 significant digits).</summary>
    /// <inheritdoc cref="SetValue(double)"/>
    public void SetValue(decimal value) => SetValue((double)value);

    /// <summary>Writes <c>TRUE</c> or <c>FALSE</c>.</summary>
    /// <param name="value">The value.</param>
    /// <exception cref="InvalidOperationException">The cell is part of an array formula.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only.</exception>
    public void SetValue(bool value)
    {
        var c = Prepare();
        c.SetAttributeValue("t", "b");
        c.Add(new XElement(Worksheet.Ns + "v", value ? "1" : "0"));
        Done();
    }

    /// <summary>
    /// Writes a date (and time): its serial number in the workbook's date system, in a date number format unless the
    /// cell has a date or time format already.
    /// </summary>
    /// <param name="value">The date, from 1900-01-01 (1904-01-01 in the 1904 date system) to 9999-12-31.</param>
    /// <exception cref="ArgumentOutOfRangeException">The date is before the first date of the workbook's date system.</exception>
    /// <exception cref="InvalidOperationException">The cell is part of an array formula.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only.</exception>
    public void SetValue(DateTime value)
    {
        var serial = Serial(value, Worksheet.Workbook.Date1904);
        var c = Prepare();
        c.Add(new XElement(Worksheet.Ns + "v", serial.ToString("R", CultureInfo.InvariantCulture)));
        var styles = Worksheet.Workbook.Styles;
        if (!styles.Format(Style, Worksheet.Workbook.Invariant).IsDate)
        {
            SetStyle(styles.WithNumberFormat(Style, value.TimeOfDay == TimeSpan.Zero ? 14 : 22));
        }

        Done();
    }

    /// <summary>
    /// Writes a formula (with or without its leading <c>=</c>), in the A1 style and with English function names and
    /// separators (<c>SUM(A1:A3)</c>, <c>IF(B2&gt;0,"yes","no")</c>). It is not calculated: the cell has no value
    /// until Excel or another program calculates it when it opens the workbook.
    /// </summary>
    /// <param name="formula">The formula.</param>
    /// <exception cref="ArgumentException">The formula is empty, longer than 8,192 characters or has control characters.</exception>
    /// <exception cref="InvalidOperationException">The cell is part of an array formula.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only.</exception>
    public void SetFormula(string formula)
    {
        ArgumentNullException.ThrowIfNull(formula);
        var text = formula.StartsWith('=') ? formula[1..] : formula;
        if (text.Length is 0 or > 8192 || text.Any(c => (c < ' ' && c is not '\t' and not '\n' and not '\r') || c is '\uFFFE' or '\uFFFF'))
        {
            throw new ArgumentException("A formula has 1 to 8,192 characters, and no control characters.", nameof(formula));
        }

        var c = Prepare();
        c.Add(new XElement(Worksheet.Ns + "f", text));
        Done();
    }

    /// <summary>Removes the cell's value and formula; its style stays.</summary>
    /// <exception cref="InvalidOperationException">The cell is part of an array formula.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only.</exception>
    public void Clear()
    {
        Worksheet.Workbook.EnsureWritable("changed");
        if (Element is null)
        {
            return;
        }

        var c = Prepare();
        if (!c.Attributes().Any(a => a.Name.LocalName != "r") && !c.HasElements)
        {
            Worksheet.RemoveCell(_row, _column);
        }

        Done();
    }

    /// <inheritdoc/>
    public override string ToString() => Worksheet.Name + "!" + Address;

    /// <summary>The serial number of a date in a date system.</summary>
    internal static double Serial(DateTime value, bool date1904)
    {
        double serial;
        if (date1904)
        {
            serial = (value - new DateTime(1904, 1, 1)).TotalDays;
        }
        else
        {
            serial = (value - new DateTime(1899, 12, 30)).TotalDays;
            serial = serial < 61 ? serial - 1 : serial;
        }

        if (serial < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, date1904
                ? "The 1904 date system has no dates before 1904-01-01."
                : "The 1900 date system has no dates before 1900-01-01.");
        }

        return serial;
    }

    /// <summary>The date of a serial number in a date system, or <c>null</c> if it is not a date.</summary>
    internal static DateTime? Date(double serial, bool date1904)
    {
        if (serial < 0 || serial > 2958465.99999 || !double.IsFinite(serial))
        {
            return null;
        }

        var start = date1904 ? new DateTime(1904, 1, 1) : serial < 60 ? new DateTime(1899, 12, 31) : new DateTime(1899, 12, 30);
        var milliseconds = Math.Round(serial * 86_400_000);
        var date = start.AddMilliseconds(milliseconds);
        return date.Year > 9999 ? null : date;
    }

    /// <summary>A number in the General format with up to 15 significant digits, in a culture.</summary>
    internal static string General(double value, FormatSettings settings) =>
        value.ToString("G15", CultureInfo.InvariantCulture).Replace(".", settings.DecimalSeparator, StringComparison.Ordinal);

    /// <summary>Reads the cell's value.</summary>
    internal CellContent Read()
    {
        if (Element is not { } c)
        {
            return default;
        }

        var ns = Worksheet.Ns;
        var type = (string?)c.Attribute("t");
        var v = c.Element(ns + "v")?.Value;
        var workbook = Worksheet.Workbook;
        switch (type)
        {
            case "s":
                return int.TryParse(v, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var index)
                    && workbook.Strings[index] is { } shared
                    ? new CellContent(CellValueType.Text, shared)
                    : new CellContent(CellValueType.Text, string.Empty);
            case "inlineStr":
                return new CellContent(CellValueType.Text, c.Element(ns + "is") is { } inline ? WorkbookNames.Text(inline) : XlsxWorkbook.Unescape(v ?? string.Empty));
            case "str":
                return v is null ? default : new CellContent(CellValueType.Text, XlsxWorkbook.Unescape(v));
            case "b":
                return v is null ? default : new CellContent(CellValueType.Boolean, v.Trim() is "1" or "true");
            case "e":
                return v is null ? default : new CellContent(CellValueType.Error, v.Trim());
            case "d":
                if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var iso))
                {
                    try
                    {
                        return new CellContent(CellValueType.DateTime, iso, Serial(iso, workbook.Date1904));
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        return new CellContent(CellValueType.Text, v!);
                    }
                }

                return v is null ? default : new CellContent(CellValueType.Text, v);
            default:
                if (string.IsNullOrWhiteSpace(v))
                {
                    return default;
                }

                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                {
                    return new CellContent(CellValueType.Text, v);
                }

                if (workbook.Styles.Format(Style, workbook.Invariant).IsDate && Date(number, workbook.Date1904) is { } date)
                {
                    return new CellContent(CellValueType.DateTime, date, number);
                }

                return new CellContent(CellValueType.Number, number, number);
        }
    }

    /// <summary>Gets the cell ready for a new value: its formula and value removed, its style kept.</summary>
    private XElement Prepare()
    {
        var workbook = Worksheet.Workbook;
        workbook.EnsureWritable("changed");
        if (Worksheet.ArrayAt(_row, _column) is { } array)
        {
            throw new InvalidOperationException($"The cell {Address} is part of the array formula in {array}, which can only be changed as a whole.");
        }

        var c = Worksheet.GetOrCreateCell(_row, _column);
        var ns = Worksheet.Ns;
        if (c.Element(ns + "f") is { } f && (string?)f.Attribute("t") == "shared" && f.Value.Length > 0)
        {
            // The first cell of a shared formula: the other cells get formulas of their own first.
            Worksheet.UnshareFormulas(_ => true);
        }

        foreach (var child in c.Elements().Where(e => e.Name.LocalName is "f" or "v" or "is").ToList())
        {
            child.Remove();
        }

        foreach (var attribute in c.Attributes().Where(a => a.Name.LocalName is "t" or "cm" or "vm" or "ph").ToList())
        {
            attribute.Remove();
        }

        return c;
    }

    private void Done() => Worksheet.Workbook.CellsChanged(Worksheet);

    private void SetStyle(int style)
    {
        var c = Worksheet.GetOrCreateCell(_row, _column);
        c.SetAttributeValue("s", style == 0 ? null : style.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>A cell's value as read: what it is, the value, and the number of a number or date.</summary>
internal readonly record struct CellContent(CellValueType Type, object? Value, double Number = 0);
