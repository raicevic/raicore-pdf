namespace RaiCore.Pdf.Svg;

/// <summary>The <c>preserveAspectRatio</c> attribute: an alignment (0, 0.5 or 1 per axis) and meet or slice.</summary>
internal readonly record struct SvgPreserveAspectRatio(bool None, double AlignX, double AlignY, bool Slice)
{
    /// <summary>The default, <c>xMidYMid meet</c>.</summary>
    public static SvgPreserveAspectRatio Default { get; } = new(false, 0.5, 0.5, false);

    public static SvgPreserveAspectRatio Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Default;
        }

        var parts = text.Split([' ', '\t', '\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0] == "defer")
        {
            parts.RemoveAt(0);
        }

        if (parts.Count == 0)
        {
            return Default;
        }

        var slice = parts.Count > 1 && parts[1] == "slice";
        if (parts[0] == "none")
        {
            return new SvgPreserveAspectRatio(true, 0, 0, false);
        }

        var align = parts[0];
        if (align.Length != 8 || align[0] != 'x' || align[4] != 'Y')
        {
            return Default;
        }

        static double? Align(string value) => value switch
        {
            "Min" => 0,
            "Mid" => 0.5,
            "Max" => 1,
            _ => null,
        };

        return Align(align[1..4]) is { } x && Align(align[5..8]) is { } y ? new SvgPreserveAspectRatio(false, x, y, slice) : Default;
    }
}

/// <summary>The <c>viewBox</c> attribute: the user-space rectangle shown in a viewport.</summary>
internal readonly record struct SvgViewBox(double X, double Y, double Width, double Height)
{
    /// <summary>Parses four numbers; null when invalid or when the width or height is not positive.</summary>
    public static SvgViewBox? Parse(string? text)
    {
        var numbers = SvgScanner.ReadNumbers(text);
        return numbers.Count == 4 && numbers[2] > 0 && numbers[3] > 0 ? new SvgViewBox(numbers[0], numbers[1], numbers[2], numbers[3]) : null;
    }

    /// <summary>The transformation that maps this view box into a viewport of the given size at the origin.</summary>
    public SvgMatrix Transform(SvgPreserveAspectRatio aspect, double width, double height)
    {
        var sx = width / Width;
        var sy = height / Height;
        if (aspect.None)
        {
            return new SvgMatrix(sx, 0, 0, sy, -X * sx, -Y * sy);
        }

        var scale = aspect.Slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
        var tx = (-X * scale) + ((width - (Width * scale)) * aspect.AlignX);
        var ty = (-Y * scale) + ((height - (Height * scale)) * aspect.AlignY);
        return new SvgMatrix(scale, 0, 0, scale, tx, ty);
    }
}
