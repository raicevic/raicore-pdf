using RaiCore.Pdf.Fonts.OpenType.Cff;
using static RaiCore.Pdf.Fonts.OpenType.Cff.CharStringInterpreter;

namespace RaiCore.Pdf.Tests.Fonts;

/// <summary>
/// Decodes a glyph's Type 2 / CFF2 charstring into absolute path segments ("M x y", "L x y", "C x1 y1 x2 y2 x3 y3",
/// coordinates in 16.16 fixed point so comparisons are exact) and the width argument, if any.
/// </summary>
internal static class CffOutline
{
    public static (List<string> Path, int? Width) Decode(CffFont font, int glyphId)
    {
        var commands = CharStringFlattener.Flatten(new CharStringInterpreter(font), glyphId);
        var path = new List<string>();
        int? width = null;
        long x = 0, y = 0;
        var first = true;

        void MoveTo(long dx, long dy)
        {
            x += dx;
            y += dy;
            path.Add($"M {x} {y}");
        }

        void LineTo(long dx, long dy)
        {
            x += dx;
            y += dy;
            path.Add($"L {x} {y}");
        }

        void CurveTo(long dx1, long dy1, long dx2, long dy2, long dx3, long dy3)
        {
            var x1 = x + dx1;
            var y1 = y + dy1;
            var x2 = x1 + dx2;
            var y2 = y1 + dy2;
            x = x2 + dx3;
            y = y2 + dy3;
            path.Add($"C {x1} {y1} {x2} {y2} {x} {y}");
        }

        foreach (var command in commands)
        {
            var a = command.Args;
            var op = command.Op;
            if (first && op is HStem or VStem or HStemHm or VStemHm or HintMask or CntrMask or RMoveTo or HMoveTo or VMoveTo or EndChar)
            {
                first = false;
                var hasWidth = op switch
                {
                    RMoveTo => a.Length == 3,
                    HMoveTo or VMoveTo => a.Length == 2,
                    EndChar => a.Length == 1,
                    _ => a.Length % 2 == 1,
                };
                if (hasWidth)
                {
                    width = a[0] >> 16;
                    a = a[1..];
                }
            }

            switch (op)
            {
                case RMoveTo:
                    MoveTo(a[0], a[1]);
                    break;
                case HMoveTo:
                    MoveTo(a[0], 0);
                    break;
                case VMoveTo:
                    MoveTo(0, a[0]);
                    break;
                case RLineTo:
                    for (var i = 0; i < a.Length; i += 2)
                    {
                        LineTo(a[i], a[i + 1]);
                    }

                    break;
                case HLineTo:
                case VLineTo:
                    for (var i = 0; i < a.Length; i++)
                    {
                        var horizontal = (i % 2 == 0) == (op == HLineTo);
                        LineTo(horizontal ? a[i] : 0, horizontal ? 0 : a[i]);
                    }

                    break;
                case RRCurveTo:
                    for (var i = 0; i < a.Length; i += 6)
                    {
                        CurveTo(a[i], a[i + 1], a[i + 2], a[i + 3], a[i + 4], a[i + 5]);
                    }

                    break;
                case RCurveLine:
                    for (var i = 0; i < a.Length - 2; i += 6)
                    {
                        CurveTo(a[i], a[i + 1], a[i + 2], a[i + 3], a[i + 4], a[i + 5]);
                    }

                    LineTo(a[^2], a[^1]);
                    break;
                case RLineCurve:
                    for (var i = 0; i < a.Length - 6; i += 2)
                    {
                        LineTo(a[i], a[i + 1]);
                    }

                    CurveTo(a[^6], a[^5], a[^4], a[^3], a[^2], a[^1]);
                    break;
                case HHCurveTo:
                {
                    var i = a.Length % 4 == 1 ? 1 : 0;
                    var dy1 = i == 1 ? a[0] : 0;
                    for (; i < a.Length; i += 4)
                    {
                        CurveTo(a[i], dy1, a[i + 1], a[i + 2], a[i + 3], 0);
                        dy1 = 0;
                    }

                    break;
                }

                case VVCurveTo:
                {
                    var i = a.Length % 4 == 1 ? 1 : 0;
                    var dx1 = i == 1 ? a[0] : 0;
                    for (; i < a.Length; i += 4)
                    {
                        CurveTo(dx1, a[i], a[i + 1], a[i + 2], 0, a[i + 3]);
                        dx1 = 0;
                    }

                    break;
                }

                case HVCurveTo:
                case VHCurveTo:
                {
                    var horizontal = op == HVCurveTo;
                    for (var i = 0; i + 4 <= a.Length; i += 4)
                    {
                        var last = i + 5 == a.Length ? a[^1] : 0;
                        if (horizontal)
                        {
                            CurveTo(a[i], 0, a[i + 1], a[i + 2], last, a[i + 3]);
                        }
                        else
                        {
                            CurveTo(0, a[i], a[i + 1], a[i + 2], a[i + 3], last);
                        }

                        horizontal = !horizontal;
                    }

                    break;
                }

                case Flex:
                    CurveTo(a[0], a[1], a[2], a[3], a[4], a[5]);
                    CurveTo(a[6], a[7], a[8], a[9], a[10], a[11]);
                    break;
                case HFlex:
                    CurveTo(a[0], 0, a[1], a[2], a[3], 0);
                    CurveTo(a[4], 0, a[5], -a[2], a[6], 0);
                    break;
                case HFlex1:
                    CurveTo(a[0], a[1], a[2], a[3], a[4], 0);
                    CurveTo(a[5], 0, a[6], a[7], a[8], -(a[1] + a[3] + a[7]));
                    break;
                case Flex1:
                {
                    long dx = a[0] + a[2] + a[4] + a[6] + a[8];
                    long dy = a[1] + a[3] + a[5] + a[7] + a[9];
                    CurveTo(a[0], a[1], a[2], a[3], a[4], a[5]);
                    if (Math.Abs(dx) > Math.Abs(dy))
                    {
                        CurveTo(a[6], a[7], a[8], a[9], a[10], -dy);
                    }
                    else
                    {
                        CurveTo(a[6], a[7], a[8], a[9], -dx, a[10]);
                    }

                    break;
                }
            }
        }

        return (path, width);
    }
}
