using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RaiCore.Pdf.Charts;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Tests.Charts;

/// <summary>Builds chart parts (<c>c:chartSpace</c>) for tests, draws them, and reads back what was drawn.</summary>
internal static partial class ChartXml
{
    public const string C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    public const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>A chart space with a plot area (inner XML) and optional title, legend and other chart elements.</summary>
    public static string Space(string plot, string title = "", string legend = "", string chart = "<c:autoTitleDeleted val=\"1\"/>", string space = "") =>
        $"<c:chartSpace xmlns:c=\"{C}\" xmlns:a=\"{A}\">{space}<c:chart>{title}{chart}<c:plotArea><c:layout/>{plot}</c:plotArea>{legend}</c:chart></c:chartSpace>";

    /// <summary>A text title.</summary>
    public static string Title(string text) => $"<c:title><c:tx><c:rich><a:bodyPr/><a:p><a:r><a:t>{text}</a:t></a:r></a:p></c:rich></c:tx><c:overlay val=\"0\"/></c:title>";

    /// <summary>A legend at a position.</summary>
    public static string Legend(string position = "r", string extra = "") => $"<c:legend><c:legendPos val=\"{position}\"/>{extra}<c:overlay val=\"0\"/></c:legend>";

    /// <summary>A series with string categories and numeric values (<c>null</c> for blanks).</summary>
    public static string Series(int index, string? name, string[]? categories, double?[] values, string extra = "", string format = "General", string valueElement = "val", string categoryElement = "cat")
    {
        var builder = new StringBuilder($"<c:ser><c:idx val=\"{index}\"/><c:order val=\"{index}\"/>");
        if (name is not null)
        {
            builder.Append($"<c:tx><c:strRef><c:f>Sheet1!$B$1</c:f><c:strCache><c:ptCount val=\"1\"/><c:pt idx=\"0\"><c:v>{name}</c:v></c:pt></c:strCache></c:strRef></c:tx>");
        }

        builder.Append(extra);
        if (categories is not null)
        {
            builder.Append($"<c:{categoryElement}><c:strRef><c:f>Sheet1!$A$2</c:f><c:strCache><c:ptCount val=\"{categories.Length}\"/>");
            for (var i = 0; i < categories.Length; i++)
            {
                builder.Append($"<c:pt idx=\"{i}\"><c:v>{categories[i]}</c:v></c:pt>");
            }

            builder.Append($"</c:strCache></c:strRef></c:{categoryElement}>");
        }

        builder.Append(Numbers(valueElement, values, format));
        builder.Append("</c:ser>");
        return builder.ToString();
    }

    /// <summary>A numeric data element (<c>c:val</c>, <c>c:xVal</c>...) with a number cache.</summary>
    public static string Numbers(string element, double?[] values, string format = "General")
    {
        var builder = new StringBuilder($"<c:{element}><c:numRef><c:f>Sheet1!$B$2</c:f><c:numCache><c:formatCode>{format}</c:formatCode><c:ptCount val=\"{values.Length}\"/>");
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is { } v)
            {
                builder.Append($"<c:pt idx=\"{i}\"><c:v>{v.ToString("R", CultureInfo.InvariantCulture)}</c:v></c:pt>");
            }
        }

        return builder.Append($"</c:numCache></c:numRef></c:{element}>").ToString();
    }

    /// <summary>A category axis.</summary>
    public static string CatAx(int id, int cross, string position = "b", string extra = "") =>
        $"<c:catAx><c:axId val=\"{id}\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"0\"/><c:axPos val=\"{position}\"/>"
        + $"<c:majorTickMark val=\"out\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>{extra}<c:crossAx val=\"{cross}\"/></c:catAx>";

    /// <summary>A value axis with major gridlines.</summary>
    public static string ValAx(int id, int cross, string position = "l", string extra = "", string scaling = "<c:orientation val=\"minMax\"/>", string crosses = "<c:crosses val=\"autoZero\"/>") =>
        $"<c:valAx><c:axId val=\"{id}\"/><c:scaling>{scaling}</c:scaling><c:delete val=\"0\"/><c:axPos val=\"{position}\"/><c:majorGridlines/>"
        + $"<c:majorTickMark val=\"out\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>{extra}<c:crossAx val=\"{cross}\"/>{crosses}<c:crossBetween val=\"between\"/></c:valAx>";

    /// <summary>A chart group (<c>c:barChart</c>...) with settings, series and the axis ids 1 and 2.</summary>
    public static string Group(string kind, string settings, string series, int axis1 = 1, int axis2 = 2) =>
        $"<c:{kind}>{settings}{series}<c:axId val=\"{axis1}\"/><c:axId val=\"{axis2}\"/></c:{kind}>";

    /// <summary>Clustered or stacked columns (or bars) with a category and a value axis.</summary>
    public static string Bars(string series, string direction = "col", string grouping = "clustered", string legend = "", string title = "", string extra = "") => Space(
        Group("barChart", $"<c:barDir val=\"{direction}\"/><c:grouping val=\"{grouping}\"/><c:varyColors val=\"0\"/>", series, 1, 2)
        + CatAx(1, 2, direction == "col" ? "b" : "l") + ValAx(2, 1, direction == "col" ? "l" : "b") + extra, title, legend);

    /// <summary>Reads a chart part.</summary>
    public static Chart Read(string xml, Func<string, PdfColor?>? scheme = null, ResourceLimits? limits = null, List<string>? unsupported = null, Func<string, ChartReference?>? references = null) =>
        ChartReader.Read(XElement.Parse(xml), scheme, limits ?? ResourceLimits.Default, unsupported, references)
        ?? throw new InvalidOperationException("Not a chart.");

    /// <summary>Options that draw text in Helvetica.</summary>
    public static ChartRenderOptions Options(double fontSize = 10, bool opaque = false) => new()
    {
        Fonts = (_, bold, _) => [PdfFont.Standard(bold ? StandardFont.HelveticaBold : StandardFont.Helvetica)],
        DefaultFontSize = fontSize,
        OpaqueChartArea = opaque,
    };

    /// <summary>Draws a chart on a page of its size; returns the PDF and the page's content stream.</summary>
    public static Drawing Draw(string xml, double width = 480, double height = 300, Func<string, PdfColor?>? scheme = null, double fontSize = 10, bool opaque = false)
    {
        var chart = Read(xml, scheme);
        var document = new PdfDocument();
        var page = document.AddPage(new PdfPageSize(width, height));
        ChartRenderer.Draw(page.Canvas, chart, 0, 0, width, height, Options(fontSize, opaque));
        var content = Encoding.Latin1.GetString(page.Canvas.ToBytes());
        return new Drawing(document.ToArray(), content);
    }

    /// <summary>A filled rectangle drawn (<c>re</c> then a fill) and its fill color.</summary>
    public readonly record struct FilledRect(double X, double Y, double Width, double Height, string Color);

    /// <summary>A painted path: its segments' points and operators, fill color (or null) and stroke color (or null).</summary>
    public sealed record PaintedPath(List<(string Op, double[] Args)> Segments, string? Fill, string? Stroke, string Paint);

    /// <summary>What was drawn on a page.</summary>
    public sealed partial class Drawing(byte[] pdf, string content)
    {
        public byte[] Pdf { get; } = pdf;

        public string Content { get; } = content;

        /// <summary>The centers of the places a text is drawn (found by the text extractor), in reading order.</summary>
        public List<(double X, double Y)> Find(string text) =>
        [
            .. new RaiCore.Pdf.Extraction.TextExtractor(PdfReader.Open(Pdf)).FindOnPage(0, text)
                .Select(m => (m.Bounds.X + (m.Bounds.Width / 2), m.Bounds.Y + (m.Bounds.Height / 2))),
        ];

        /// <summary>The page's text in reading order.</summary>
        public string Text() => new RaiCore.Pdf.Extraction.TextExtractor(PdfReader.Open(Pdf)).ExtractPages().First().Text;

        /// <summary>The paths painted, with their colors (text blocks skipped).</summary>
        public List<PaintedPath> Paths()
        {
            var paths = new List<PaintedPath>();
            var stack = new Stack<(string, string)>();
            string fill = "0 0 0", stroke = "0 0 0";
            var segments = new List<(string, double[])>();
            var numbers = new List<double>();
            var content = TextBlock().Replace(Content, " ");
            foreach (var token in content.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    numbers.Add(number);
                    continue;
                }

                switch (token)
                {
                    case "rg":
                        fill = string.Join(' ', numbers.TakeLast(3).Select(n => n.ToString("0.###", CultureInfo.InvariantCulture)));
                        break;
                    case "RG":
                        stroke = string.Join(' ', numbers.TakeLast(3).Select(n => n.ToString("0.###", CultureInfo.InvariantCulture)));
                        break;
                    case "q":
                        stack.Push((fill, stroke));
                        break;
                    case "Q":
                        if (stack.Count > 0)
                        {
                            (fill, stroke) = stack.Pop();
                        }

                        break;
                    case "m" or "l" or "c" or "re" or "h":
                        segments.Add((token, [.. numbers]));
                        break;
                    case "f" or "f*" or "B" or "B*" or "S":
                        paths.Add(new PaintedPath(segments, token.StartsWith('S') ? null : fill, token.StartsWith('f') ? null : stroke, token));
                        segments = [];
                        break;
                    case "n" or "W" or "W*":
                        if (token == "n")
                        {
                            segments = [];
                        }

                        break;
                }

                numbers.Clear();
            }

            return paths;
        }

        /// <summary>The filled rectangles drawn.</summary>
        public List<FilledRect> Rects() =>
        [
            .. Paths().Where(p => p.Fill is not null).SelectMany(p => p.Segments.Where(s => s.Op == "re").Select(s => new FilledRect(s.Args[0], s.Args[1], s.Args[2], s.Args[3], p.Fill!))),
        ];

        [GeneratedRegex(@"BT .*? ET", RegexOptions.Singleline)]
        private static partial Regex TextBlock();
    }

    /// <summary>An RGB color as the content stream writes it.</summary>
    public static string Rgb(int r, int g, int b) =>
        string.Join(' ', new[] { r, g, b }.Select(c => Math.Round(c / 255.0, 5).ToString("0.###", CultureInfo.InvariantCulture)));
}
