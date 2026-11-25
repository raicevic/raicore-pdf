using System.Buffers.Binary;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets.Xls;

/// <summary>
/// Reads the conditional formatting of a BIFF8 worksheet into the rules <see cref="ConditionalFormatting"/> applies:
/// CFHEADER and CFHEADER12 records give the ranges of the CF (Excel 97 rules: cell values and formulas) and CF12 records
/// (Excel 2007 rules: color scales, data bars, icon sets, top and bottom, and formula rules made from templates) that
/// follow them; CFEX records give the CF rules their priority and template ([MS-XLS] 2.4.42-2.4.44). The exact colors
/// of Excel 2007 and later (any RGB or theme color) are kept after a rule's format (DXFN12); they replace its palette
/// colors.
/// </summary>
internal sealed class XlsConditionalFormatting(XlsStyles styles)
{
    /// <summary>The most rules a sheet keeps.</summary>
    private const int MaxRules = 100_000;

    /// <summary>The rules read, with the id of their CFHEADER and their index in it (for CFEX records).</summary>
    private readonly List<(ConditionalRule Rule, int Header, int Index)> _rules = [];

    /// <summary>
    /// The priority, template and exact colors CFEX records give CF rules, by their CFHEADER's id and their index in it.
    /// </summary>
    private readonly Dictionary<(int Header, int Index), (int Priority, int Template, int Parameter, bool Stop, ExtendedColors? Colors)> _extensions = [];

    private List<CellRange> _ranges = [];
    private int _header;
    private int _index;

    /// <summary>A rule that is damaged or not supported: it is counted.</summary>
    private ConditionalRule Unknown => new() { Type = "unknown", Ranges = _ranges };

    /// <summary>The rules, with their priorities (Excel 97 rules without one go in the order of their records, after the others).</summary>
    public List<ConditionalRule> Rules => [.. _rules.Select((r, i) => _extensions.TryGetValue((r.Header, r.Index), out var extension)
        ? Templated(r.Rule with { Format = WithColors(r.Rule.Format, extension.Colors) }, extension.Template, extension.Parameter)
            with { Priority = extension.Priority, StopIfTrue = extension.Stop }
        : r.Rule.Priority == int.MaxValue ? r.Rule with { Priority = 1_000_000 + i } : r.Rule)];

    /// <summary>A CFHEADER (or, with <paramref name="future"/>, CFHEADER12 after its 12 bytes of header): the ranges of the rules that follow.</summary>
    public void Header(ReadOnlySpan<byte> data, bool future)
    {
        var at = future ? 12 : 0;
        _header = (BiffReader.U16(data, at + 2) >> 1) & 0x7FFF;
        _index = 0;
        _ranges = [];
        var count = BiffReader.U16(data, at + 12);
        for (var i = 0; i < count && at + 14 + (8 * i) + 8 <= data.Length && _ranges.Count < 1000; i++)
        {
            var p = at + 14 + (8 * i);
            int row1 = BiffReader.U16(data, p), row2 = BiffReader.U16(data, p + 2), column1 = BiffReader.U16(data, p + 4), column2 = BiffReader.U16(data, p + 6);
            if (row1 <= row2 && column1 <= column2)
            {
                _ranges.Add(new CellRange(row1, Math.Min(column1, 255), row2, Math.Min(column2, 255)));
            }
        }
    }

    /// <summary>A CF record: a rule on cell values (type 1) or a formula (type 2), its format and its formulas.</summary>
    public void Rule(ReadOnlySpan<byte> data)
    {
        var index = _index++;
        if (_rules.Count >= MaxRules)
        {
            return;
        }

        if (data.Length < 6)
        {
            _rules.Add((Unknown, _header, index));
            return;
        }

        var position = 6;
        var format = Format(data, ref position);
        var formulas = Formulas(data, ref position, BiffReader.U16(data, 2), BiffReader.U16(data, 4));
        if (format is not null && data[0] is 1 or 2)
        {
            _rules.Add((Base(data[0], data[1], format, formulas), _header, index));
        }
        else
        {
            _rules.Add((Unknown, _header, index));
        }
    }

    /// <summary>A CF12 record (after its 12 bytes of header): an Excel 2007 rule.</summary>
    public void Rule12(ReadOnlySpan<byte> data)
    {
        if (_rules.Count >= MaxRules)
        {
            return;
        }

        if (data.Length < 22)
        {
            _rules.Add((Unknown, -1, 0));
            return;
        }

        var (type, comparison) = (data[12], data[13]);
        var length = BiffReader.U32(data, 18);
        var position = 22;
        DifferentialFormat? format = null;
        var known = true;
        if (length == 0)
        {
            position += 2;
        }
        else
        {
            var start = position;
            var end = (int)Math.Min(start + (long)length, data.Length);
            format = Format(data[..end], ref position);
            known = format is not null;
            format = WithColors(format, Colors(data[..end], position));
            position = end;
        }

        var formulas = Formulas(data, ref position, BiffReader.U16(data, 14), BiffReader.U16(data, 16));
        position += 2 + BiffReader.U16(data, position); // The formula that makes the rule active (not used).
        var flags = position < data.Length ? data[position] : 0;
        var priority = BiffReader.U16(data, position + 1);
        var template = BiffReader.U16(data, position + 3);
        var parameter = BiffReader.U16(data, position + 6);
        position += 6 + (position + 5 < data.Length ? data[position + 5] : 0);
        ConditionalRule? rule = type switch
        {
            1 or 2 => Templated(Base(type, comparison, format, formulas), template, parameter),
            3 => ColorScale(data, position),
            4 => DataBar(data, position),
            5 => Filter(data, position, format),
            6 => IconSet(data, position),
            _ => null,
        };
        rule = known && rule is not null
            ? rule with { Ranges = _ranges, Priority = priority, StopIfTrue = (flags & 0x02) != 0 }
            : Unknown;
        _rules.Add((rule, -1, 0));
    }

    /// <summary>A CFEX record (after its 12 bytes of header): the priority and template of a CF rule.</summary>
    public void Extension(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 || BiffReader.U32(data, 12) != 0 || _extensions.Count >= MaxRules)
        {
            return; // Not for a CF rule (it extends a CF12 rule, which has its own).
        }

        var header = BiffReader.U16(data, 16);
        var index = BiffReader.U16(data, 18);
        var template = data[21];
        var priority = BiffReader.U16(data, 22);
        var stop = (data.Length > 24 ? data[24] : 0) & 0x02;

        // The template's parameters come after the format (if it has one: its size, then it, with its exact colors after
        // it); only the first two bytes (a standard deviation) are used.
        var hasFormat = data.Length > 25 && data[25] != 0;
        var at = 26L + (hasFormat ? 4L + BiffReader.U32(data, 26) : 0);
        var parameter = at + 3 <= data.Length ? BiffReader.U16(data, (int)at + 1) : 0;
        ExtendedColors? colors = null;
        if (hasFormat)
        {
            var format = data[..(int)Math.Min(at, data.Length)];
            var position = 30;
            colors = Format(format, ref position) is null ? null : Colors(format, position);
        }

        _extensions[(header, index)] = (priority, template, parameter, stop != 0, colors);
    }

    /// <summary>A rule on cell values (type 1, with its comparison) or a formula (type 2).</summary>
    private ConditionalRule Base(int type, int comparison, DifferentialFormat? format, List<string?> formulas) => new()
    {
        Type = formulas.Any(f => f is null) ? "unknown" : type == 1 ? "cellIs" : "expression",
        Ranges = _ranges,
        Format = format,
        Operator = comparison switch
        {
            1 => "between",
            2 => "notBetween",
            3 => "equal",
            4 => "notEqual",
            5 => "greaterThan",
            6 => "lessThan",
            7 => "greaterThanOrEqual",
            8 => "lessThanOrEqual",
            _ => null,
        },
        Formulas = [.. formulas.OfType<string>().Where(f => f.Length > 0)],
    };

    /// <summary>
    /// A formula rule made from a template: those that are not computed by their formula (duplicate and unique values,
    /// above and below average, dates in a period) as their own rules; the others (text, blanks, errors) by their formula.
    /// </summary>
    private static ConditionalRule Templated(ConditionalRule rule, int template, int parameter) => template switch
    {
        0x07 => rule with { Type = "uniqueValues" },
        0x1B => rule with { Type = "duplicateValues" },
        0x19 or 0x1A or 0x1D or 0x1E => rule with
        {
            Type = "aboveAverage",
            AboveAverage = template is 0x19 or 0x1D,
            EqualAverage = template is 0x1D or 0x1E,
            StandardDeviations = Math.Clamp(parameter, 0, 3),
        },
        >= 0x0F and <= 0x18 => rule with
        {
            Type = "timePeriod",
            TimePeriod = (string[])["today", "tomorrow", "yesterday", "last7Days", "lastMonth", "nextMonth", "thisWeek", "nextWeek", "lastWeek", "thisMonth"] is var periods
                ? periods[template - 0x0F] : null,
        },
        _ => rule,
    };

    /// <summary>The formulas of a rule (each <c>null</c> if it is not supported), from their tokens' lengths.</summary>
    private List<string?> Formulas(ReadOnlySpan<byte> data, ref int position, int first, int second)
    {
        var (row, column) = _ranges.Count > 0 ? (_ranges[0].Row1, _ranges[0].Column1) : (0, 0);
        var formulas = new List<string?>();
        foreach (var length in (int[])[first, second])
        {
            if (length == 0)
            {
                continue;
            }

            var available = Math.Clamp(data.Length - position, 0, length);
            formulas.Add(available < length ? null : BiffFormula.ToText(data.Slice(position, length), row, column));
            position += available;
        }

        return formulas;
    }

    /// <summary>
    /// A rule's format (DXFN, [MS-XLS] 2.5.79): flags of what it sets, then the blocks it has (number format, font,
    /// alignment, border, pattern, protection); <c>null</c> where the data ends inside it.
    /// </summary>
    private DifferentialFormat? Format(ReadOnlySpan<byte> data, ref int position)
    {
        if (position + 6 > data.Length)
        {
            return null;
        }

        var flags = BiffReader.U32(data, position);
        var userFormat = (BiffReader.U16(data, position + 4) & 0x0001) != 0;
        position += 6;
        bool Has(int bit) => (flags & (1u << bit)) != 0;
        bool Unset(int bit) => (flags & (1u << bit)) != 0; // The "not specified" flags of the parts of a block.
        if (Has(25))
        {
            position += userFormat ? BiffReader.U16(data, position) : 2;
        }

        bool? bold = null, italic = null, strike = null;
        CellUnderline? underline = null;
        PdfColor? color = null;
        if (Has(26))
        {
            if (position + 118 > data.Length)
            {
                return null;
            }

            var font = data.Slice(position, 118);
            var style = BinaryPrimitives.ReadUInt32LittleEndian(font[68..]);
            var styleUnset = BinaryPrimitives.ReadUInt32LittleEndian(font[88..]);
            italic = (styleUnset & 0x02) == 0 ? (style & 0x02) != 0 : null;
            strike = (styleUnset & 0x80) == 0 ? (style & 0x80) != 0 : null;
            bold = BinaryPrimitives.ReadUInt32LittleEndian(font[100..]) == 0 ? BinaryPrimitives.ReadUInt16LittleEndian(font[72..]) >= 700 : null;
            underline = BinaryPrimitives.ReadUInt32LittleEndian(font[96..]) == 0
                ? font[76] switch { 0 => CellUnderline.None, 2 or 0x22 => CellUnderline.Double, _ => CellUnderline.Single }
                : null;
            var icv = BinaryPrimitives.ReadInt32LittleEndian(font[80..]);
            color = icv is >= 0 and < 0x7FFF ? styles.PaletteColor(icv) : null;
            position += 118;
        }

        if (Has(27))
        {
            position += 8;
        }

        CellBorder? left = null, right = null, top = null, bottom = null;
        if (Has(28))
        {
            if (position + 8 > data.Length)
            {
                return null;
            }

            var lines = BiffReader.U32(data, position);
            var colors = BiffReader.U32(data, position + 4);
            left = Unset(10) ? null : styles.Border((int)(lines & 0xF), (int)((lines >> 16) & 0x7F));
            right = Unset(11) ? null : styles.Border((int)((lines >> 4) & 0xF), (int)((lines >> 23) & 0x7F));
            top = Unset(12) ? null : styles.Border((int)((lines >> 8) & 0xF), (int)(colors & 0x7F));
            bottom = Unset(13) ? null : styles.Border((int)((lines >> 12) & 0xF), (int)((colors >> 7) & 0x7F));
            position += 8;
        }

        PdfColor? fill = null;
        if (Has(29))
        {
            if (position + 4 > data.Length)
            {
                return null;
            }

            // As in .xlsx, a solid fill of a conditional format is its background color, else its pattern's.
            var pattern = BiffReader.U16(data, position) >> 10;
            var fillColors = BiffReader.U16(data, position + 2);
            var background = Unset(18) ? null : styles.PaletteColor((fillColors >> 7) & 0x7F);
            var foreground = Unset(17) ? null : styles.PaletteColor(fillColors & 0x7F);
            fill = !Unset(16) && pattern == 0 ? null : background ?? foreground;
            position += 4;
        }

        if (Has(30))
        {
            position += 2;
        }

        return new DifferentialFormat(bold, italic, underline, strike, color, fill, top, right, bottom, left);
    }

    /// <summary>
    /// The exact colors of a format (DXFN12), by what they color: its text, its fill's pattern and background, and its
    /// borders; <c>null</c> where it has none.
    /// </summary>
    private sealed record ExtendedColors(PdfColor? Text, PdfColor? Foreground, PdfColor? Background, PdfColor? Top, PdfColor? Bottom, PdfColor? Left, PdfColor? Right);

    /// <summary>
    /// The exact colors kept after a format (XFExtNoFRT, [MS-XLS] 2.5.283: six reserved bytes, the number of properties,
    /// then the properties, ExtProp 2.5.108: their type, their size with this header, and their data, FullColorExt
    /// 2.5.155 for colors), from <paramref name="position"/> to the end of <paramref name="data"/>; <c>null</c> if there are none.
    /// </summary>
    private ExtendedColors? Colors(ReadOnlySpan<byte> data, int position)
    {
        if (position + 8 > data.Length)
        {
            return null;
        }

        var count = BiffReader.U16(data, position + 6);
        position += 8;
        PdfColor? text = null, foreground = null, background = null, top = null, bottom = null, left = null, right = null;
        for (var i = 0; i < count && position + 4 <= data.Length; i++)
        {
            var (type, size) = (BiffReader.U16(data, position), BiffReader.U16(data, position + 2));
            if (size < 4 || position + size > data.Length)
            {
                break;
            }

            if (size >= 12 && type is 0x04 or 0x05 or 0x07 or 0x08 or 0x09 or 0x0A or 0x0D)
            {
                // Its type (automatic, palette, RGB or theme), its tint or shade (a signed fraction of 32767) and its value.
                var tint = (short)BiffReader.U16(data, position + 6) / 32767.0;
                var color = styles.ExtendedColor(BiffReader.U16(data, position + 4), BiffReader.U32(data, position + 8), tint);
                switch (type)
                {
                    case 0x04: foreground = color; break;
                    case 0x05: background = color; break;
                    case 0x07: top = color; break;
                    case 0x08: bottom = color; break;
                    case 0x09: left = color; break;
                    case 0x0A: right = color; break;
                    default: text = color; break;
                }
            }

            position += size;
        }

        return (text ?? foreground ?? background ?? top ?? bottom ?? left ?? right) is null
            ? null
            : new ExtendedColors(text, foreground, background, top, bottom, left, right);
    }

    /// <summary>
    /// A format with its exact colors in place of its palette colors: of its text, its fill (a solid fill's background
    /// color, else its pattern's, as <see cref="Format"/> chooses) and its borders, where it sets them.
    /// </summary>
    private static DifferentialFormat? WithColors(DifferentialFormat? format, ExtendedColors? colors)
    {
        if (format is null || colors is null)
        {
            return format;
        }

        static CellBorder? Side(CellBorder? border, PdfColor? color) => border is { } b && color is { } c ? b with { Color = c } : border;
        return format with
        {
            Color = format.Color is null ? null : colors.Text ?? format.Color,
            Fill = format.Fill is null ? null : colors.Background ?? colors.Foreground ?? format.Fill,
            Top = Side(format.Top, colors.Top),
            Bottom = Side(format.Bottom, colors.Bottom),
            Left = Side(format.Left, colors.Left),
            Right = Side(format.Right, colors.Right),
        };
    }

    /// <summary>A point of a scale, bar or icon set (CFVO): its type, and its formula or number.</summary>
    private ConditionalValue? Point(ReadOnlySpan<byte> data, ref int position)
    {
        if (position + 3 > data.Length)
        {
            return null;
        }

        var type = data[position];
        var length = BiffReader.U16(data, position + 1);
        position += 3;
        string? value;
        if (length > 0)
        {
            var (row, column) = _ranges.Count > 0 ? (_ranges[0].Row1, _ranges[0].Column1) : (0, 0);
            value = position + length <= data.Length ? BiffFormula.ToText(data.Slice(position, length), row, column) : null;
            position += length;
            if (value is null)
            {
                return null;
            }
        }
        else if (type is not (2 or 3))
        {
            if (position + 8 > data.Length)
            {
                return null;
            }

            value = BinaryPrimitives.ReadDoubleLittleEndian(data[position..]).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            position += 8;
        }
        else
        {
            value = null;
        }

        var name = type switch
        {
            1 => "num",
            2 => "min",
            3 => "max",
            4 => "percent",
            5 => "percentile",
            7 => "formula",
            _ => null,
        };
        return name is null ? null : new ConditionalValue(length > 0 && name == "num" ? "formula" : name, value);
    }

    /// <summary>A color of a scale or bar (CFColor: XColor, [MS-XLS] 2.5.287): its type, value and tint.</summary>
    private PdfColor? Color(ReadOnlySpan<byte> data, ref int position)
    {
        if (position + 16 > data.Length)
        {
            position = data.Length;
            return null;
        }

        var color = styles.ExtendedColor((int)BiffReader.U32(data, position), BiffReader.U32(data, position + 4), BinaryPrimitives.ReadDoubleLittleEndian(data[(position + 8)..]));
        position += 16;
        return color;
    }

    /// <summary>A color scale (CFGradient): its points with their positions, and a color at each.</summary>
    private ConditionalRule? ColorScale(ReadOnlySpan<byte> data, int position)
    {
        if (position + 6 > data.Length)
        {
            return null;
        }

        int points = data[position + 3], curves = data[position + 4];
        position += 6;
        var values = new List<ConditionalValue>();
        for (var i = 0; i < points && i < 16; i++)
        {
            if (Point(data, ref position) is not { } point)
            {
                return null;
            }

            values.Add(point);
            position += 8; // The point's position on the scale.
        }

        var colors = new List<PdfColor?>();
        for (var i = 0; i < curves && i < 16; i++)
        {
            position += 8;
            colors.Add(Color(data, ref position));
        }

        return new ConditionalRule { Type = "colorScale", Values = values, Colors = colors };
    }

    /// <summary>A data bar (CFDatabar): whether it shows the value, its lengths, its color and its minimum and maximum.</summary>
    private ConditionalRule? DataBar(ReadOnlySpan<byte> data, int position)
    {
        if (position + 6 > data.Length)
        {
            return null;
        }

        var options = data[position + 3];
        var lengths = (Math.Min((int)data[position + 4], 100), Math.Min((int)data[position + 5], 100));
        position += 6;
        var color = Color(data, ref position);
        var low = Point(data, ref position);
        var high = Point(data, ref position);
        return low is null || high is null ? null : new ConditionalRule
        {
            Type = "dataBar",
            Values = [low, high],
            Colors = [color],
            Lengths = lengths,
            ShowValue = (options & 0x01) == 0,
        };
    }

    /// <summary>A top or bottom rule (CFFilter): top or bottom, a rank or a percent.</summary>
    private static ConditionalRule? Filter(ReadOnlySpan<byte> data, int position, DifferentialFormat? format)
    {
        if (position + 6 > data.Length)
        {
            return null;
        }

        var flags = data[position + 3];
        return new ConditionalRule
        {
            Type = "top10",
            Format = format,
            Bottom = (flags & 0x01) == 0,
            Percent = (flags & 0x02) != 0,
            Rank = BiffReader.U16(data, position + 4),
        };
    }

    /// <summary>An icon set (CFMultistate): the set, whether it shows the value or is reversed, and each icon's threshold.</summary>
    private ConditionalRule? IconSet(ReadOnlySpan<byte> data, int position)
    {
        string[] sets =
        [
            "3Arrows", "3ArrowsGray", "3Flags", "3TrafficLights1", "3TrafficLights2", "3Signs", "3Symbols", "3Symbols2", "4Arrows",
            "4ArrowsGray", "4RedToBlack", "4Rating", "4TrafficLights", "5Arrows", "5ArrowsGray", "5Rating", "5Quarters",
        ];
        if (position + 6 > data.Length || data[position + 4] >= sets.Length)
        {
            return null;
        }

        var set = sets[data[position + 4]];
        var options = data[position + 5];
        position += 6;
        var values = new List<ConditionalValue>();
        for (var i = 0; i < set[0] - '0'; i++)
        {
            if (Point(data, ref position) is not { } point || position + 5 > data.Length)
            {
                return null;
            }

            values.Add(point with { GreaterOrEqual = data[position] != 0 });
            position += 5;
        }

        return new ConditionalRule
        {
            Type = "iconSet",
            IconSet = set,
            Values = values,
            ShowValue = (options & 0x01) == 0,
            Reverse = (options & 0x04) != 0,
        };
    }
}
