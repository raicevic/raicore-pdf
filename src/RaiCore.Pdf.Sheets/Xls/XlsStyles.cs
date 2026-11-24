using System.Globalization;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets.Xls;

/// <summary>A FONT record: its family, size in points, style and color (a palette index, 0x7FFF for automatic).</summary>
internal sealed record XlsFont(string? Name, double? Size, bool Bold, bool Italic, CellUnderline Underline, bool Strikethrough, int Color);

/// <summary>
/// An XF record (a cell or style format): its font and number format indexes, alignment, borders (line styles and
/// palette indexes) and fill (pattern and palette indexes).
/// </summary>
internal sealed record XlsXf
{
    public int Font { get; init; }

    public int NumberFormat { get; init; }

    public int Horizontal { get; init; }

    public int Vertical { get; init; } = 2;

    public bool Wrap { get; init; }

    public int Indent { get; init; }

    /// <summary>The text's rotation in degrees, counterclockwise (-90 to 90).</summary>
    public int Rotation { get; init; }

    /// <summary>Whether the text is shrunk to fit its cell (not supported, only reported).</summary>
    public bool ShrinkToFit { get; init; }

    /// <summary>Whether the text is stacked vertically, one character under the other.</summary>
    public bool Stacked { get; init; }

    /// <summary>The line styles of the left, right, top and bottom borders ([MS-XLS] 2.5.4: 0 none, 1 thin...).</summary>
    public (int Left, int Right, int Top, int Bottom) Lines { get; init; }

    /// <summary>The palette indexes of the left, right, top and bottom borders.</summary>
    public (int Left, int Right, int Top, int Bottom) LineColors { get; init; }

    /// <summary>The fill pattern ([MS-XLS] 2.5.12: 0 none, 1 solid, 2 50% gray...).</summary>
    public int Pattern { get; init; }

    public int Foreground { get; init; } = 64;

    public int Background { get; init; } = 65;
}

/// <summary>
/// The formats of a binary workbook: its fonts, number formats, palette and XF records, resolved into the cell styles
/// worksheets use. Indexes out of range fall back to the first entry, or to nothing.
/// </summary>
internal sealed class XlsStyles
{
    /// <summary>The border line styles by their index in an XF record, by the names styles use.</summary>
    private static readonly string[] LineStyles =
    [
        "none", "thin", "medium", "dashed", "dotted", "thick", "double", "hair", "mediumDashed", "dashDot", "mediumDashDot",
        "dashDotDot", "mediumDashDotDot", "slantDashDot",
    ];

    /// <summary>The fill patterns by their index in an XF record, by the names styles use.</summary>
    private static readonly string[] Patterns =
    [
        "none", "solid", "mediumGray", "darkGray", "lightGray", "darkHorizontal", "darkVertical", "darkDown", "darkUp",
        "darkGrid", "darkTrellis", "lightHorizontal", "lightVertical", "lightDown", "lightUp", "lightGrid", "lightTrellis",
        "gray125", "gray0625",
    ];

    private readonly Dictionary<int, XlsxCellStyle> _resolved = [];
    private readonly Dictionary<int, NumberFormat> _formats = [];
    private XlsxColors _colors = new();

    public List<XlsFont> Fonts { get; } = [];

    public List<XlsXf> Xfs { get; } = [];

    /// <summary>The number formats of FORMAT records, by their index.</summary>
    public Dictionary<int, string> NumberFormats { get; } = [];

    /// <summary>The colors of a PALETTE record (from index 8 on), or <c>null</c> for the default palette.</summary>
    public List<uint>? Palette { get; set; }

    public FormatSettings Settings { get; set; } = new(System.Globalization.CultureInfo.InvariantCulture, date1904: false);

    /// <summary>The font size of the workbook's default font (its first), in points.</summary>
    public double DefaultFontSize => Fonts.Count > 0 && Fonts[0].Size is { } size ? size : 10;

    /// <summary>The family of the workbook's default font, or <c>null</c>.</summary>
    public string? DefaultFontName => Fonts.Count > 0 ? Fonts[0].Name : null;

    /// <summary>A color of the workbook's palette by index, or <c>null</c>.</summary>
    public PdfColor? PaletteColor(int index) => _colors.PaletteColor(index);

    /// <summary>Prepares the palette once every record is read.</summary>
    public void Complete()
    {
        if (Palette is { Count: > 0 } custom)
        {
            var palette = new uint[64];
            for (var i = 0; i < palette.Length; i++)
            {
                palette[i] = i >= 8 && i - 8 < custom.Count ? custom[i - 8] : XlsxColors.DefaultPalette[i];
            }

            _colors = new XlsxColors(palette);
        }
    }

    /// <summary>The number format of an index: a FORMAT record's, or a built-in one.</summary>
    public NumberFormat NumberFormatOf(int id)
    {
        if (_formats.TryGetValue(id, out var format))
        {
            return format;
        }

        var code = NumberFormats.TryGetValue(id, out var custom) ? custom : NumberFormat.BuiltInCode(id, Settings);
        format = code is null ? NumberFormat.General : NumberFormat.Parse(code);
        _formats[id] = format;
        return format;
    }

    /// <summary>The cell style of an XF index (the first one for an index out of range).</summary>
    public XlsxCellStyle CellStyle(int index)
    {
        if (index < 0 || index >= Xfs.Count)
        {
            index = 0;
        }

        if (_resolved.TryGetValue(index, out var style))
        {
            return style;
        }

        var xf = index < Xfs.Count ? Xfs[index] : new XlsXf();

        // Font index 4 does not exist: the fonts after the first four are numbered from 5.
        var fontIndex = xf.Font >= 4 ? xf.Font - 1 : xf.Font;
        var font = xf.Font != 4 && fontIndex >= 0 && fontIndex < Fonts.Count ? Fonts[fontIndex]
            : Fonts.Count > 0 ? Fonts[0] : new XlsFont(null, null, false, false, CellUnderline.None, false, 0x7FFF);
        var format = new CellFormat
        {
            FontName = font.Name,
            FontSize = font.Size,
            Bold = font.Bold,
            Italic = font.Italic,
            Underline = font.Underline,
            Strikethrough = font.Strikethrough,
            Color = font.Color == 0x7FFF ? null : _colors.PaletteColor(font.Color),
            Fill = xf.Pattern > 0 && xf.Pattern < Patterns.Length
                ? XlsxStyles.PatternFill(Patterns[xf.Pattern], _colors.PaletteColor(xf.Foreground), _colors.PaletteColor(xf.Background))
                : null,
            Left = Border(xf.Lines.Left, xf.LineColors.Left),
            Right = Border(xf.Lines.Right, xf.LineColors.Right),
            Top = Border(xf.Lines.Top, xf.LineColors.Top),
            Bottom = Border(xf.Lines.Bottom, xf.LineColors.Bottom),
            Vertical = xf.Vertical switch
            {
                0 => VerticalAlignment.Top,
                1 or 3 or 4 => VerticalAlignment.Middle,
                _ => VerticalAlignment.Bottom,
            },
            Wrap = xf.Wrap,
            Indent = xf.Indent,
            ShrinkToFit = xf.ShrinkToFit,
            Stacked = xf.Stacked,
            Rotation = xf.Rotation,
        };
        var alignment = xf.Horizontal switch
        {
            1 or 4 or 5 or 7 => CellAlignment.Left,
            2 or 6 => CellAlignment.Center,
            3 => CellAlignment.Right,
            _ => CellAlignment.General,
        };
        style = new XlsxCellStyle(format, NumberFormatOf(xf.NumberFormat), alignment);
        _resolved[index] = style;
        return style;
    }

    /// <summary>
    /// The runs of rich text: the text split where each formatting run starts, the text before the first in the cell's
    /// font, the rest in the run's font (index 4 does not exist; an unknown index is the cell's font).
    /// </summary>
    public IReadOnlyList<CellRun> Runs(string text, IReadOnlyList<(int First, int Font)> formatting)
    {
        var runs = new List<CellRun>();
        var at = 0;
        CellFont? font = null;
        foreach (var (first, index) in formatting.Where(r => r.First < text.Length).OrderBy(r => r.First))
        {
            if (first > at)
            {
                runs.Add(new CellRun(text[at..first], font));
                at = first;
            }

            var fontIndex = index >= 4 ? index - 1 : index;
            font = index != 4 && fontIndex >= 0 && fontIndex < Fonts.Count && Fonts[fontIndex] is var f
                ? new CellFont(f.Name, f.Size, f.Bold, f.Italic, f.Underline, f.Strikethrough, f.Color == 0x7FFF ? null : _colors.PaletteColor(f.Color))
                : null;
        }

        runs.Add(new CellRun(text[at..], font));
        return runs;
    }

    /// <summary>
    /// A color of conditional formatting (an XColor, [MS-XLS] 2.5.287): automatic (<paramref name="type"/> 0), a palette
    /// index (1), an RGB value (2: red, green and blue bytes from the lowest) or a theme color (3), with a tint.
    /// </summary>
    public PdfColor? ExtendedColor(int type, uint value, double tint)
    {
        var shade = double.IsFinite(tint) && tint != 0 ? Math.Clamp(tint, -1, 1).ToString("R", CultureInfo.InvariantCulture) : null;
        return type switch
        {
            1 => _colors.PaletteColor((int)Math.Min(value, 0x7FFF)),
            2 => _colors.Resolve(((value & 0xFF) << 16 | (value & 0xFF00) | ((value >> 16) & 0xFF)).ToString("X6", CultureInfo.InvariantCulture), null, null, shade, null),
            3 => _colors.Resolve(null, null, Math.Min(value, 100).ToString(CultureInfo.InvariantCulture), shade, null),
            _ => null,
        };
    }

    internal CellBorder Border(int line, int color) =>
        line > 0 && line < LineStyles.Length ? XlsxStyles.Border(LineStyles[line], _colors.PaletteColor(color)) : CellBorder.None;
}
