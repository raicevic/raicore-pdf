namespace RaiCore.Pdf.Sheets;

/// <summary>
/// The icons of Excel's icon sets (conditional formatting), drawn as vector shapes in a square: arrows, traffic
/// lights, flags, signs, symbols, stars, triangles, ratings, quarters and boxes. Index 0 is the icon of the lowest
/// values.
/// </summary>
internal static class SheetIcons
{
    private static readonly PdfColor Red = PdfColor.FromRgb(0xF8, 0x69, 0x6B);
    private static readonly PdfColor Yellow = PdfColor.FromRgb(0xFF, 0xC0, 0x00);
    private static readonly PdfColor Green = PdfColor.FromRgb(0x63, 0xBE, 0x7B);
    private static readonly PdfColor Gray = PdfColor.FromRgb(0x80, 0x80, 0x80);
    private static readonly PdfColor Light = PdfColor.FromRgb(0xD9, 0xD9, 0xD9);
    private static readonly PdfColor Blue = PdfColor.FromRgb(0x44, 0x72, 0xC4);
    private static readonly PdfColor Black = PdfColor.FromRgb(0x40, 0x40, 0x40);
    private static readonly PdfColor Pink = PdfColor.FromRgb(0xF4, 0xB1, 0x83);
    private static readonly PdfColor Gold = PdfColor.FromRgb(0xFF, 0xC0, 0x00);

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "3Arrows", "3ArrowsGray", "3Flags", "3TrafficLights1", "3TrafficLights2", "3Signs", "3Symbols", "3Symbols2", "3Stars",
        "3Triangles", "4Arrows", "4ArrowsGray", "4RedToBlack", "4Rating", "4TrafficLights", "5Arrows", "5ArrowsGray", "5Rating",
        "5Quarters", "5Boxes",
    };

    /// <summary>Whether an icon set is one Excel has (and is drawn).</summary>
    public static bool IsKnown(string set) => Known.Contains(set);

    /// <summary>Draws an icon of a set in a square of a size, its bottom left corner at x and y.</summary>
    public static void Draw(PdfCanvas canvas, CellIcon icon, double x, double y, double size)
    {
        var (set, index) = (icon.Set, icon.Index);
        canvas.SaveState();
        canvas.Transform(size / 16, 0, 0, size / 16, x, y); // Drawn in a square of 16 units.
        PdfColor[] traffic = [Red, Yellow, Green];
        switch (set)
        {
            case "3Arrows" or "3ArrowsGray":
                Arrow(canvas, (index - 1) * 90, set == "3Arrows" ? traffic[index] : Gray);
                break;
            case "4Arrows" or "4ArrowsGray":
                Arrow(canvas, (index - 2 + (index >= 2 ? 1 : 0)) * 45, set == "4ArrowsGray" ? Gray : index == 0 ? Red : index == 3 ? Green : Yellow);
                break;
            case "5Arrows" or "5ArrowsGray":
                Arrow(canvas, (index - 2) * 45, set == "5ArrowsGray" ? Gray : index == 0 ? Red : index == 4 ? Green : Yellow);
                break;
            case "3Flags":
                Flag(canvas, traffic[index]);
                break;
            case "3TrafficLights1":
                Circle(canvas, 8, 8, 7, traffic[index]);
                break;
            case "3TrafficLights2":
                canvas.SetFillColor(Black);
                RoundedSquare(canvas);
                canvas.Fill();
                Circle(canvas, 8, 8, 5.5, traffic[index]);
                break;
            case "4TrafficLights":
                Circle(canvas, 8, 8, 7, index == 0 ? Black : traffic[index - 1]);
                break;
            case "4RedToBlack":
                Circle(canvas, 8, 8, 7, (PdfColor[])[Black, Gray, Pink, PdfColor.FromRgb(0xC0, 0, 0)] is var shades ? shades[index] : Black);
                break;
            case "3Signs":
                Sign(canvas, index);
                break;
            case "3Symbols" or "3Symbols2":
                Symbol(canvas, index, set == "3Symbols");
                break;
            case "3Stars":
                Star(canvas, index / 2.0);
                break;
            case "3Triangles":
                Triangle(canvas, index);
                break;
            case "4Rating" or "5Rating":
                Bars(canvas, set == "4Rating" ? index + 1 : index);
                break;
            case "5Quarters":
                Quarters(canvas, index);
                break;
            case "5Boxes":
                Boxes(canvas, index);
                break;
        }

        canvas.RestoreState();
    }

    /// <summary>An arrow pointing right turned by an angle (counterclockwise; 90 points up).</summary>
    private static void Arrow(PdfCanvas canvas, double angle, PdfColor color)
    {
        var radians = angle * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        (double, double) P(double px, double py) => (8 + (px * cos) - (py * sin), 8 + (px * sin) + (py * cos));
        (double X, double Y)[] points = [P(-7, -2.2), P(1, -2.2), P(1, -6), P(7.5, 0), P(1, 6), P(1, 2.2), P(-7, 2.2)];
        Polygon(canvas, points, color);
    }

    private static void Flag(PdfCanvas canvas, PdfColor color)
    {
        canvas.SetFillColor(Black).Rectangle(3, 1, 1.4, 14).Fill();
        Polygon(canvas, [(4.4, 15), (14, 11.5), (4.4, 8)], color);
    }

    private static void Sign(PdfCanvas canvas, int index)
    {
        switch (index)
        {
            case 0:
                Polygon(canvas, [(8, 1), (15, 8), (8, 15), (1, 8)], Red); // A diamond.
                break;
            case 1:
                Polygon(canvas, [(1, 2), (15, 2), (8, 14.5)], Yellow); // A triangle.
                break;
            default:
                Circle(canvas, 8, 8, 7, Green);
                break;
        }
    }

    private static void Symbol(PdfCanvas canvas, int index, bool circled)
    {
        var color = (PdfColor[])[Red, Yellow, Green] is var colors ? colors[index] : Red;
        var mark = PdfColor.FromRgb(255, 255, 255);
        if (circled)
        {
            Circle(canvas, 8, 8, 7.5, color);
        }
        else
        {
            mark = color;
        }

        canvas.SetStrokeColor(mark).SetLineWidth(2.2).SetLineCap(LineCap.Round);
        switch (index)
        {
            case 0:
                canvas.MoveTo(4.5, 4.5).LineTo(11.5, 11.5).MoveTo(4.5, 11.5).LineTo(11.5, 4.5).Stroke();
                break;
            case 1:
                canvas.MoveTo(8, 6.5).LineTo(8, 12.5).Stroke();
                canvas.SetFillColor(mark);
                Circle(canvas, 8, 3.8, 1.2, mark);
                break;
            default:
                canvas.MoveTo(4, 8.5).LineTo(7, 5).LineTo(12, 11.5).Stroke();
                break;
        }
    }

    /// <summary>A star, filled to a fraction (0, a half, whole) in gold.</summary>
    private static void Star(PdfCanvas canvas, double filled)
    {
        var points = new (double X, double Y)[10];
        for (var i = 0; i < 10; i++)
        {
            var angle = (Math.PI / 2) + (i * Math.PI / 5);
            var r = i % 2 == 0 ? 7.5 : 3.2;
            points[i] = (8 + (r * Math.Cos(angle)), 8 + (r * Math.Sin(angle)));
        }

        Path(canvas, points);
        canvas.SetFillColor(Light).Fill();
        if (filled > 0)
        {
            canvas.SaveState();
            canvas.Rectangle(0, 0, 16 * filled, 16).Clip().EndPath();
            Path(canvas, points);
            canvas.SetFillColor(Gold).Fill();
            canvas.RestoreState();
        }
    }

    private static void Triangle(PdfCanvas canvas, int index)
    {
        switch (index)
        {
            case 0:
                Polygon(canvas, [(2, 12), (14, 12), (8, 3)], Red);
                break;
            case 1:
                canvas.SetFillColor(Yellow).Rectangle(3, 6.5, 10, 3).Fill();
                break;
            default:
                Polygon(canvas, [(2, 4), (14, 4), (8, 13)], Green);
                break;
        }
    }

    /// <summary>Four bars of rising height, the first <paramref name="filled"/> of them blue.</summary>
    private static void Bars(PdfCanvas canvas, int filled)
    {
        for (var i = 0; i < 4; i++)
        {
            canvas.SetFillColor(i < filled ? Blue : Light).Rectangle(1.5 + (i * 3.5), 2, 2.5, 3 + (i * 3.5)).Fill();
        }
    }

    /// <summary>A circle filled by quarters, clockwise from the top: 0 to 4.</summary>
    private static void Quarters(PdfCanvas canvas, int quarters)
    {
        Circle(canvas, 8, 8, 7, Black);
        Circle(canvas, 8, 8, 6, PdfColor.FromRgb(255, 255, 255));
        if (quarters >= 4)
        {
            Circle(canvas, 8, 8, 6, Black);
        }
        else if (quarters > 0)
        {
            // The pie from the top clockwise, a quarter at a time.
            canvas.SetFillColor(Black).MoveTo(8, 8).LineTo(8, 14);
            for (var q = 0; q < quarters; q++)
            {
                double a0 = (Math.PI / 2) - (q * Math.PI / 2), a1 = a0 - (Math.PI / 2);
                var k = 4.0 / 3 * Math.Tan((a0 - a1) / 4) * 6;
                canvas.CurveTo(8 + (6 * Math.Cos(a0)) + (k * Math.Sin(a0)), 8 + (6 * Math.Sin(a0)) - (k * Math.Cos(a0)),
                    8 + (6 * Math.Cos(a1)) - (k * Math.Sin(a1)), 8 + (6 * Math.Sin(a1)) + (k * Math.Cos(a1)), 8 + (6 * Math.Cos(a1)), 8 + (6 * Math.Sin(a1)));
            }

            canvas.ClosePath().Fill();
        }
    }

    /// <summary>Four boxes in a square, the first <paramref name="filled"/> of them (left to right, top to bottom) blue.</summary>
    private static void Boxes(PdfCanvas canvas, int filled)
    {
        (double X, double Y)[] boxes = [(1.5, 8.5), (8.5, 8.5), (1.5, 1.5), (8.5, 1.5)];
        for (var i = 0; i < 4; i++)
        {
            canvas.SetFillColor(i < filled ? Blue : Light).Rectangle(boxes[i].X, boxes[i].Y, 6, 6).Fill();
        }
    }

    private static void RoundedSquare(PdfCanvas canvas)
    {
        const double r = 3, k = r * 0.5523;
        canvas.MoveTo(1 + r, 0.5).LineTo(15 - r, 0.5).CurveTo(15 - r + k, 0.5, 15, 0.5 + r - k, 15, 0.5 + r).LineTo(15, 15.5 - r)
            .CurveTo(15, 15.5 - r + k, 15 - r + k, 15.5, 15 - r, 15.5).LineTo(1 + r, 15.5).CurveTo(1 + r - k, 15.5, 1, 15.5 - r + k, 1, 15.5 - r)
            .LineTo(1, 0.5 + r).CurveTo(1, 0.5 + r - k, 1 + r - k, 0.5, 1 + r, 0.5).ClosePath();
    }

    private static void Circle(PdfCanvas canvas, double cx, double cy, double r, PdfColor color)
    {
        var k = r * 0.5523;
        canvas.SetFillColor(color).MoveTo(cx + r, cy).CurveTo(cx + r, cy + k, cx + k, cy + r, cx, cy + r).CurveTo(cx - k, cy + r, cx - r, cy + k, cx - r, cy)
            .CurveTo(cx - r, cy - k, cx - k, cy - r, cx, cy - r).CurveTo(cx + k, cy - r, cx + r, cy - k, cx + r, cy).ClosePath().Fill();
    }

    private static void Polygon(PdfCanvas canvas, (double X, double Y)[] points, PdfColor color)
    {
        Path(canvas, points);
        canvas.SetFillColor(color).Fill();
    }

    private static void Path(PdfCanvas canvas, (double X, double Y)[] points)
    {
        canvas.MoveTo(points[0].X, points[0].Y);
        foreach (var (px, py) in points.Skip(1))
        {
            canvas.LineTo(px, py);
        }

        canvas.ClosePath();
    }
}
