using System.Text;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Charts;

/// <summary>Chart text: short labels in lines, measured and drawn with the fonts the caller resolves, turned if need be.</summary>
internal sealed partial class ChartRenderer
{
    /// <summary>The most characters of a label that are drawn.</summary>
    private const int MaxLabelLength = 1000;

    private readonly Dictionary<(string?, bool, bool), IReadOnlyList<PdfFont>> _fonts = [];

    /// <summary>Text laid out in lines: its format resolved, its size unturned.</summary>
    private sealed class TextBlock
    {
        public required string[] Lines { get; init; }

        public required IReadOnlyList<PdfFont> Fonts { get; init; }

        public required double Size { get; init; }

        public required PdfColor Color { get; init; }

        public required double[] LineWidths { get; init; }

        /// <summary>The rotation in degrees, counterclockwise.</summary>
        public double Rotation { get; set; }

        public double Width => LineWidths.Length == 0 ? 0 : LineWidths.Max();

        public double LineHeight => Size * 1.2;

        public double Height => Lines.Length * LineHeight;

        /// <summary>The width of the box the turned text covers.</summary>
        public double TurnedWidth
        {
            get
            {
                var r = Rotation * Math.PI / 180;
                return (Math.Abs(Math.Cos(r)) * Width) + (Math.Abs(Math.Sin(r)) * Height);
            }
        }

        /// <summary>The height of the box the turned text covers.</summary>
        public double TurnedHeight
        {
            get
            {
                var r = Rotation * Math.PI / 180;
                return (Math.Abs(Math.Sin(r)) * Width) + (Math.Abs(Math.Cos(r)) * Height);
            }
        }
    }

    /// <summary>The resolved format of text: a format over its parents, over the chart's text and the defaults.</summary>
    private ChartTextFormat Resolve(ChartTextFormat? format, double sizeFactor = 1, bool? bold = null)
    {
        var defaults = _baseText with { Size = _baseText.Size * sizeFactor, Bold = bold ?? _baseText.Bold };
        return (format ?? new ChartTextFormat()).Over(defaults);
    }

    /// <summary>Lays out text (lines split at new lines) in a resolved format.</summary>
    private TextBlock Text(string text, ChartTextFormat format)
    {
        var size = Math.Clamp(format.Size ?? 10, 1, 400);
        var fonts = Fonts(format.Typeface, format.Bold ?? false, format.Italic ?? false);
        var lines = Clean(text).Split('\n');
        return new TextBlock
        {
            Lines = lines,
            Fonts = fonts,
            Size = size,
            Color = format.Color ?? _chart.TextColor,
            LineWidths = [.. lines.Select(l => Measure(l, fonts, size))],
            Rotation = format.Rotation ?? 0,
        };
    }

    /// <summary>Text without control characters, at most <see cref="MaxLabelLength"/> characters.</summary>
    private static string Clean(string text)
    {
        if (text.Length > MaxLabelLength)
        {
            text = text[..MaxLabelLength];
        }

        var clean = new StringBuilder(text.Length);
        foreach (var c in text.ReplaceLineEndings("\n"))
        {
            clean.Append(c == '\t' ? ' ' : c == '\n' || !char.IsControl(c) ? c : ' ');
        }

        return clean.ToString();
    }

    private IReadOnlyList<PdfFont> Fonts(string? typeface, bool bold, bool italic)
    {
        if (!_fonts.TryGetValue((typeface, bold, italic), out var fonts))
        {
            fonts = _options.Fonts(typeface, bold, italic) is { Count: > 0 } found ? found : [PdfFont.Standard(StandardFont.Helvetica)];
            _fonts[(typeface, bold, italic)] = fonts;
        }

        return fonts;
    }

    /// <summary>The runs of a line, each in the first font that has its characters.</summary>
    private static List<(string Text, PdfFont Font)> Runs(string line, IReadOnlyList<PdfFont> fonts)
    {
        var runs = new List<(string, PdfFont)>();
        var current = new StringBuilder();
        PdfFont? currentFont = null;
        foreach (var rune in line.EnumerateRunes())
        {
            var font = fonts[0];
            foreach (var candidate in fonts)
            {
                if (candidate.HasGlyph(rune.Value))
                {
                    font = candidate;
                    break;
                }
            }

            if (!ReferenceEquals(font, currentFont) && current.Length > 0)
            {
                runs.Add((current.ToString(), currentFont!));
                current.Clear();
            }

            currentFont = font;
            current.Append(rune.ToString());
        }

        if (current.Length > 0)
        {
            runs.Add((current.ToString(), currentFont!));
        }

        return runs;
    }

    private static double Measure(string line, IReadOnlyList<PdfFont> fonts, double size) =>
        Runs(line, fonts).Sum(r => r.Font.CanMeasure ? r.Font.MeasureText(r.Text, size) : r.Text.Length * size * 0.5);

    /// <summary>
    /// Draws text so that its turned box is placed at an anchor: <paramref name="alignX"/> and
    /// <paramref name="alignY"/> (0 to 1) pick the point of the box at the anchor (0, 0 its top-left corner). Lines are
    /// aligned by <paramref name="alignX"/> too.
    /// </summary>
    private void DrawText(TextBlock block, double x, double y, double alignX, double alignY)
    {
        if (block.Lines.All(l => l.Length == 0))
        {
            return;
        }

        var rotation = block.Rotation;
        var (w, h) = (block.Width, block.Height);
        _canvas.SetFillColor(block.Color);
        if (Math.Abs(rotation) < 0.01)
        {
            var top = y - (alignY * h);
            var left = x - (alignX * w);
            for (var i = 0; i < block.Lines.Length; i++)
            {
                var lineLeft = left + ((w - block.LineWidths[i]) * alignX);
                var baseline = top + (i * block.LineHeight) + (block.Size * 0.95);
                DrawLine(block.Lines[i], block.Fonts, block.Size, X(lineLeft), Y(baseline));
            }

            return;
        }

        // Turned: the box's center goes where the anchor puts the center of the turned box.
        var (tw, th) = (block.TurnedWidth, block.TurnedHeight);
        var centerX = x - (alignX * tw) + (tw / 2);
        var centerY = y - (alignY * th) + (th / 2);
        var r = rotation * Math.PI / 180;
        var (cos, sin) = (Math.Cos(r), Math.Sin(r));
        _canvas.SaveState();
        _canvas.Transform(cos, sin, -sin, cos, X(centerX), Y(centerY));
        for (var i = 0; i < block.Lines.Length; i++)
        {
            // In the turned space, y upwards from the center of the box.
            var lineLeft = (-w / 2) + ((w - block.LineWidths[i]) * alignX);
            var baseline = (h / 2) - (i * block.LineHeight) - (block.Size * 0.95);
            DrawLine(block.Lines[i], block.Fonts, block.Size, lineLeft, baseline);
        }

        _canvas.RestoreState();
    }

    /// <summary>Draws a line of text at a baseline in canvas coordinates, run by run.</summary>
    private void DrawLine(string line, IReadOnlyList<PdfFont> fonts, double size, double x, double baseline)
    {
        foreach (var (text, font) in Runs(line, fonts))
        {
            _canvas.DrawText(text, font, size, x, baseline);
            x += font.CanMeasure ? font.MeasureText(text, size) : text.Length * size * 0.5;
        }
    }

    /// <summary>Text wrapped at spaces into lines no wider than a width (words wider than it stay whole).</summary>
    private TextBlock Wrap(string text, ChartTextFormat format, double width)
    {
        var block = Text(text, format);
        if (block.Width <= width)
        {
            return block;
        }

        var lines = new List<string>();
        foreach (var paragraph in Clean(text).Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(candidate, block.Fonts, block.Size) > width)
                {
                    lines.Add(line.ToString());
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }

            lines.Add(line.ToString());
        }

        return Text(string.Join('\n', lines), format);
    }
}
