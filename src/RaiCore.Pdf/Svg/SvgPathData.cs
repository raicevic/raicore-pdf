namespace RaiCore.Pdf.Svg;

/// <summary>
/// Parses SVG path data (the <c>d</c> attribute): absolute and relative M L H V C S Q T A Z, with implicit command
/// repetition. As the SVG specification requires, an error ends the path but what came before it is kept.
/// </summary>
internal static class SvgPathData
{
    public static SvgPath Parse(string? data)
    {
        var path = new SvgPath();
        if (string.IsNullOrEmpty(data))
        {
            return path;
        }

        var scanner = new SvgScanner(data);
        var command = '\0';

        // The reflected control point of the previous curve, for S and T.
        var (lastCubicX, lastCubicY, lastQuadX, lastQuadY) = (0.0, 0.0, 0.0, 0.0);
        var previous = '\0';
        Span<double> n = stackalloc double[7];

        while (!scanner.AtEnd)
        {
            var next = scanner.Peek();
            if (char.IsAsciiLetter(next))
            {
                command = scanner.Read();
            }
            else if (command == '\0' || !scanner.NextIsNumber())
            {
                break; // Data must start with a command; anything else is an error.
            }

            var relative = char.IsAsciiLetterLower(command);
            var (x0, y0) = (path.CurrentX, path.CurrentY);
            var (ox, oy) = relative ? (x0, y0) : (0.0, 0.0);
            var upper = char.ToUpperInvariant(command);
            if (upper != 'M' && path.Segments.Count == 0)
            {
                break; // Path data must start with a moveto.
            }

            if (upper == 'Z')
            {
                path.Close();
                previous = 'Z';
                command = '\0'; // Z takes no arguments; numbers after it are an error.
                continue;
            }

            var count = upper switch
            {
                'M' or 'L' or 'T' => 2,
                'H' or 'V' => 1,
                'C' => 6,
                'S' or 'Q' => 4,
                'A' => 7,
                _ => -1,
            };
            if (count < 0 || !ReadArguments(ref scanner, upper, n[..count]))
            {
                break;
            }

            switch (upper)
            {
                case 'M':
                    path.MoveTo(ox + n[0], oy + n[1]);
                    command = relative ? 'l' : 'L'; // Further pairs are implicit lines.
                    break;
                case 'L':
                    path.LineTo(ox + n[0], oy + n[1]);
                    break;
                case 'H':
                    path.LineTo(ox + n[0], y0);
                    break;
                case 'V':
                    path.LineTo(x0, oy + n[0]);
                    break;
                case 'C':
                    path.CubicTo(ox + n[0], oy + n[1], ox + n[2], oy + n[3], ox + n[4], oy + n[5]);
                    (lastCubicX, lastCubicY) = (ox + n[2], oy + n[3]);
                    break;
                case 'S':
                    {
                        var (x1, y1) = previous is 'C' or 'S' ? ((2 * x0) - lastCubicX, (2 * y0) - lastCubicY) : (x0, y0);
                        path.CubicTo(x1, y1, ox + n[0], oy + n[1], ox + n[2], oy + n[3]);
                        (lastCubicX, lastCubicY) = (ox + n[0], oy + n[1]);
                        break;
                    }

                case 'Q':
                    path.QuadTo(ox + n[0], oy + n[1], ox + n[2], oy + n[3]);
                    (lastQuadX, lastQuadY) = (ox + n[0], oy + n[1]);
                    break;
                case 'T':
                    {
                        var (x1, y1) = previous is 'Q' or 'T' ? ((2 * x0) - lastQuadX, (2 * y0) - lastQuadY) : (x0, y0);
                        path.QuadTo(x1, y1, ox + n[0], oy + n[1]);
                        (lastQuadX, lastQuadY) = (x1, y1);
                        break;
                    }

                default: // 'A'
                    path.ArcTo(n[0], n[1], n[2], n[3] != 0, n[4] != 0, ox + n[5], oy + n[6]);
                    break;
            }

            previous = upper;
        }

        return path;
    }

    private static bool ReadArguments(ref SvgScanner scanner, char command, scoped Span<double> values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            // The arc flags are single digits and may run into the next number ("a1 1 0 00 1 1").
            if (command == 'A' && i is 3 or 4)
            {
                if (!scanner.TryReadFlag(out var flag))
                {
                    return false;
                }

                values[i] = flag ? 1 : 0;
            }
            else if (!scanner.TryReadNumber(out values[i]))
            {
                return false;
            }
        }

        return true;
    }
}
