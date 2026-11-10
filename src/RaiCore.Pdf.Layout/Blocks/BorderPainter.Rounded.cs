namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>Borders of boxes with rounded corners (CSS <c>border-radius</c>).</summary>
internal static partial class BorderPainter
{
    /// <summary>Points sampled along a corner's curve to measure it.</summary>
    private const int ArcSamples = 32;

    /// <summary>
    /// Draws the borders of a box with rounded corners. The border is the ring between the outer edge (the box with its
    /// <paramref name="corners"/>) and the padding edge, whose radii are the outer ones minus the border widths (at least
    /// 0). Each side gets its share of the ring, split on the line from each outer corner through the inner rectangle's
    /// corner, as with square corners. Filled bands (solid, double, the 3D styles) are rings between rounded rectangles
    /// inset by the same fractions of each side's width, clipped to the sides of one color (one fill per color, so
    /// sides of the same color join without seams). Dashed and dotted sides are dashed strokes along the ring's center
    /// line, around the curves, clipped to the side's share of the ring.
    /// </summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="sides">The sides to draw, top, right, bottom, left; a side of width 0 is left out.</param>
    /// <param name="x">The left edge (PDF coordinates).</param>
    /// <param name="y">The bottom edge.</param>
    /// <param name="width">The border box's width.</param>
    /// <param name="height">The border box's height.</param>
    /// <param name="corners">The outer radii, fitted to the box.</param>
    private static void PaintRounded(PdfCanvas canvas, BorderSide[] sides, double x, double y, double width, double height, RoundedCorners corners)
    {
        var box = new Ring(x, y, width, height, [sides[0].Width, sides[1].Width, sides[2].Width, sides[3].Width], corners);
        var fills = new List<(PdfColor Color, int Side, double From, double To)>();
        var strokes = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var border = sides[i];
            if (border.Width <= 0)
            {
                continue;
            }

            if (border.Style is BorderStyle.Dashed or BorderStyle.Dotted)
            {
                strokes.Add(i);
            }

            // Depths as fractions of the side's width, so that adjacent sides' bands meet on the corner diagonals.
            fills.AddRange(Bands(border, (Side)i).Select(band => (band.Color, i, band.From / border.Width, band.To / border.Width)));
        }

        var drawn = Enumerable.Range(0, 4).Count(i => sides[i].Width > 0);
        foreach (var group in fills.GroupBy(f => (f.Color, f.From, f.To)))
        {
            var (color, from, to) = group.Key;
            canvas.SetFillColor(color);
            var whole = group.Count() == drawn; // Every side: no need to split the ring.
            if (!whole)
            {
                canvas.SaveState();
                foreach (var band in group)
                {
                    box.AddShare(canvas, band.Side, squareStart: false, squareEnd: false);
                }

                canvas.Clip();
            }

            if (box.AddRing(canvas, from, to))
            {
                canvas.Fill();
            }
            else
            {
                canvas.EndPath();
            }

            if (!whole)
            {
                canvas.RestoreState();
            }
        }

        // Where identical sides meet, both draw the corner's dash or dot; a translucent color is painted once there, as
        // a group (drawn opaque inside it).
        foreach (var group in strokes.GroupBy(side => sides[side].Color))
        {
            var translucent = !group.Key.IsOpaque;
            var drawing = sides;
            if (translucent)
            {
                canvas.BeginGroup(group.Key.Alpha);
                drawing = [.. sides.Select(s => s.Color == group.Key ? s with { Color = group.Key.WithAlpha(1) } : s)];
            }

            foreach (var side in group)
            {
                StrokeRounded(canvas, box, drawing, side);
            }

            if (translucent)
            {
                canvas.EndGroup();
            }
        }
    }

    /// <summary>Strokes a dashed or dotted side of a rounded box along the ring's center line, clipped to its share of the ring.</summary>
    private static void StrokeRounded(PdfCanvas canvas, Ring box, BorderSide[] sides, int side)
    {
        var border = sides[side];
        var width = border.Width;
        var (previous, next) = (sides[(side + 3) % 4], sides[(side + 1) % 4]);
        var frame = box.Frame(side);

        // At square corners shared with an identical side, dashes may cover the whole corner square, as without radii.
        var squareStart = !box.Corners.Rounded(side) && (previous.Width == 0 || previous == border);
        var squareEnd = !box.Corners.Rounded(side + 1) && (next.Width == 0 || next == border);

        // The center line: the rounded rectangle inset by half of each side's width. Square corners extend it straight
        // past the box's edge (clipped), so that dots at the ends are not lost.
        var center = box.Center();
        var startArc = center is { } c0 ? Arc.Corner(c0.Corners, side, c0.X, c0.Y, c0.Width, c0.Height) : null;
        var endArc = center is { } c1 ? Arc.Corner(c1.Corners, side + 1, c1.X, c1.Y, c1.Width, c1.Height) : null;
        var extension = 3 * width;

        // Where an identical side meets at a rounded corner, the corner's dash (or dot) is centered on the diagonal and
        // both sides draw it a little past the diagonal, so that its halves join without a seam. The diagonal of equal
        // widths is at 45 degrees: moved by the width, it clears a dot's radius and stays within half a dash.
        var joinStart = startArc is not null && previous == border;
        var joinEnd = endArc is not null && next == border;
        var overlap = width;

        canvas.SaveState();
        box.AddShare(canvas, side, squareStart, squareEnd, joinStart ? overlap : 0, joinEnd ? overlap : 0);
        canvas.Clip();
        if (!box.AddRing(canvas, 0, 1))
        {
            canvas.EndPath();
            canvas.RestoreState();
            return;
        }

        canvas.Clip();

        // The side's visible length runs from the start corner's diagonal to the end corner's (from the outer edge at
        // square corners); `before` is the path drawn ahead of it.
        double before, length;
        (double X, double Y) lineStart, lineEnd;
        if (startArc is { } start)
        {
            before = previous.Width > 0 ? start.LengthTo(box.Diagonal(side)) : 0;
            length = start.Length - before;
            lineStart = start.End;
        }
        else
        {
            before = extension;
            length = 0;
            lineStart = frame.Point(0, width / 2);
        }

        lineEnd = endArc is { } end ? end.Start : frame.Point(frame.Length, width / 2);
        length += Math.Sqrt(Math.Pow(lineEnd.X - lineStart.X, 2) + Math.Pow(lineEnd.Y - lineStart.Y, 2));
        if (endArc is { } endCorner)
        {
            length += next.Width > 0 ? endCorner.LengthTo(box.Diagonal(side + 1)) : endCorner.Length;
        }

        // Round dots are centered on the diagonals (a dot's radius in where no side meets), or the corner square's center.
        var startDot = previous.Width > 0 ? (startArc is null ? previous.Width / 2 : 0) : width / 2;
        var endDot = next.Width > 0 ? (endArc is null ? next.Width / 2 : 0) : width / 2;
        var roundDots = border.Style == BorderStyle.Dotted && width >= (3 * Pixel) - Epsilon;
        var half = roundDots ? 0 : (border.Style == BorderStyle.Dotted ? width : 3 * width) / 2;
        var (shiftStart, shiftEnd) = (joinStart ? half : 0, joinEnd ? half : 0);
        var stroke = Dashes(border, length + shiftStart + shiftEnd, startDot, endDot);
        var round = stroke?.Cap == LineCap.Round;

        canvas.SetStrokeColor(border.Color).SetLineWidth(round ? width : 2 * Math.Max(width, Math.Max(previous.Width, next.Width)));
        if (round)
        {
            // Round dots are placed here, not by a dash pattern: viewers measure curves slightly differently, which would
            // move the dot shared by two sides apart. Each is a zero-length line with round caps.
            canvas.SetLineCap(LineCap.Round);
            var dots = stroke!.Value;
            var period = dots.Pattern.Sum();
            var path = new Polyline(startArc, frame.Point(-extension, width / 2), lineStart, lineEnd, endArc);
            for (var u = dots.From + ((period - dots.Phase) % period); u <= dots.To + Epsilon; u += period)
            {
                var (dx, dy) = path.PointAt(u + before);
                canvas.MoveTo(dx, dy).LineTo(dx, dy);
            }

            canvas.Stroke();
            canvas.RestoreState();
            return;
        }

        if (stroke is { Pattern.Length: > 0 } dashes)
        {
            var period = dashes.Pattern.Sum();
            var phase = (dashes.Phase + shiftStart - before - dashes.From) % period;
            canvas.SetDash(dashes.Pattern, phase < 0 ? phase + period : phase);
        }

        if (startArc is { } startCurve)
        {
            canvas.MoveTo(startCurve.Start.X, startCurve.Start.Y);
            startCurve.AddTo(canvas);
        }
        else
        {
            var (sx, sy) = frame.Point(-extension, width / 2);
            canvas.MoveTo(sx, sy).LineTo(lineStart.X, lineStart.Y);
        }

        canvas.LineTo(lineEnd.X, lineEnd.Y);
        if (endArc is { } endCurve)
        {
            endCurve.AddTo(canvas);
        }
        else
        {
            var (ex, ey) = frame.Point(frame.Length + extension, width / 2);
            canvas.LineTo(ex, ey);
        }

        canvas.Stroke();
        canvas.RestoreState();
    }

    /// <summary>
    /// A side's outer edge as a frame: from (<paramref name="X"/>, <paramref name="Y"/>) in the unit direction
    /// (<paramref name="DirectionX"/>, <paramref name="DirectionY"/>) for <paramref name="Length"/>, clockwise, with the
    /// box <paramref name="Depth"/> deep to the right of it.
    /// </summary>
    private readonly record struct Frame(double X, double Y, double DirectionX, double DirectionY, double Length, double Depth)
    {
        /// <summary>The point <paramref name="along"/> the edge and <paramref name="depth"/> inward from it.</summary>
        public (double X, double Y) Point(double along, double depth) =>
            (X + (along * DirectionX) + (depth * DirectionY), Y + (along * DirectionY) - (depth * DirectionX));
    }

    /// <summary>The border of a rounded box: its rectangle (PDF coordinates), the side widths (top, right, bottom, left) and the outer radii.</summary>
    private sealed record Ring(double X, double Y, double Width, double Height, double[] Widths, RoundedCorners Corners)
    {
        /// <summary>Side <paramref name="side"/>'s outer edge (0 top, clockwise).</summary>
        public Frame Frame(int side) => side switch
        {
            0 => new(X, Y + Height, 1, 0, Width, Height),
            1 => new(X + Width, Y + Height, 0, -1, Height, Width),
            2 => new(X + Width, Y, -1, 0, Width, Height),
            _ => new(X, Y, 0, 1, Height, Width),
        };

        /// <summary>
        /// The line that splits corner <paramref name="corner"/> between its sides: from the outer corner through the
        /// corner of the rectangle inset by the border widths, as a point and a direction.
        /// </summary>
        public (double X, double Y, double DirectionX, double DirectionY) Diagonal(int corner)
        {
            corner &= 3;
            var frame = Frame(corner);
            var (ox, oy) = frame.Point(0, 0);
            var (ix, iy) = frame.Point(Widths[(corner + 3) % 4], Widths[corner]);
            return (ox, oy, ix - ox, iy - oy);
        }

        /// <summary>
        /// Adds side <paramref name="side"/>'s share of the box as a closed quadrilateral (or triangle): its outer edge,
        /// and the corner diagonals inward until they meet, deep enough for the curves of the corners but not into the
        /// opposite side. <paramref name="squareStart"/>/<paramref name="squareEnd"/> take the whole corner square instead;
        /// <paramref name="overlapStart"/>/<paramref name="overlapEnd"/> move the diagonals that far into the adjacent sides.
        /// </summary>
        public void AddShare(PdfCanvas canvas, int side, bool squareStart, bool squareEnd, double overlapStart = 0, double overlapEnd = 0)
        {
            var frame = Frame(side);
            var width = Widths[side];
            var startJoin = squareStart ? 0 : Widths[(side + 3) % 4];
            var endJoin = squareEnd ? 0 : Widths[(side + 1) % 4];

            // How deep the corner curves reach into the box from this side: their radius across the side.
            double Reach(int corner) => side % 2 == 0 ? Corners[corner].Y : Corners[corner].X;
            var limit = Math.Max(width, frame.Depth - Widths[(side + 2) % 4]);
            var startDepth = Math.Min(Math.Max(width, Reach(side)), limit);
            var endDepth = Math.Min(Math.Max(width, Reach(side + 1)), limit);
            if (startJoin + endJoin > 0)
            {
                // The diagonals meet this deep.
                var meet = frame.Length * width / (startJoin + endJoin);
                startDepth = Math.Min(startDepth, meet);
                endDepth = Math.Min(endDepth, meet);
            }

            var (ax, ay) = frame.Point(-overlapStart, 0);
            var (bx, by) = frame.Point(frame.Length + overlapEnd, 0);
            var (cx, cy) = frame.Point(frame.Length - (endDepth * endJoin / width) + overlapEnd, endDepth);
            var (dx, dy) = frame.Point((startDepth * startJoin / width) - overlapStart, startDepth);
            canvas.MoveTo(ax, ay).LineTo(bx, by).LineTo(cx, cy).LineTo(dx, dy).ClosePath();
        }

        /// <summary>
        /// Adds the ring between the rounded rectangles inset by the fractions <paramref name="from"/> and
        /// <paramref name="to"/> of each side's width, for filling or clipping with the nonzero rule: the inner edge runs
        /// the other way round, so that where a side has width 0 the coincident edges cancel out. False when the ring is
        /// empty (nothing was added).
        /// </summary>
        public bool AddRing(PdfCanvas canvas, double from, double to)
        {
            if (!AddInset(canvas, from, counterclockwise: false))
            {
                return false;
            }

            AddInset(canvas, to, counterclockwise: true); // Nothing when the inner edge is empty: the whole shape is border.
            return true;
        }

        /// <summary>The ring's center line: the rectangle inset by half of each side's width, with its corners; <c>null</c> when empty.</summary>
        public (double X, double Y, double Width, double Height, RoundedCorners Corners)? Center()
        {
            var (top, right, bottom, left) = (Widths[0] / 2, Widths[1] / 2, Widths[2] / 2, Widths[3] / 2);
            var (width, height) = (Width - left - right, Height - top - bottom);
            return width > 0 && height > 0
                ? (X + left, Y + bottom, width, height, Corners.Inset(top, right, bottom, left).Fit(width, height))
                : null;
        }

        private bool AddInset(PdfCanvas canvas, double fraction, bool counterclockwise)
        {
            var (top, right, bottom, left) = (Widths[0] * fraction, Widths[1] * fraction, Widths[2] * fraction, Widths[3] * fraction);
            var (width, height) = (Width - left - right, Height - top - bottom);
            if (width <= Epsilon || height <= Epsilon)
            {
                return false;
            }

            Corners.Inset(top, right, bottom, left).AddPath(canvas, X + left, Y + bottom, width, height, counterclockwise);
            return true;
        }
    }

    /// <summary>
    /// A side's center line as line segments, for finding points along it: the start corner's curve (or a straight run
    /// in from <paramref name="before"/>, past the edge, at a square corner), the straight part from
    /// <paramref name="lineStart"/> to <paramref name="lineEnd"/>, and the end corner's curve (or nothing).
    /// </summary>
    private sealed class Polyline(Arc? start, (double X, double Y) before, (double X, double Y) lineStart, (double X, double Y) lineEnd, Arc? end)
    {
        private readonly List<((double X, double Y) Point, double Length)> _points = Build(start, before, lineStart, lineEnd, end);

        /// <summary>The point <paramref name="distance"/> along the line (clamped to its ends).</summary>
        public (double X, double Y) PointAt(double distance)
        {
            for (var i = 1; i < _points.Count; i++)
            {
                var (from, to) = (_points[i - 1], _points[i]);
                if (distance <= to.Length || i == _points.Count - 1)
                {
                    var t = to.Length > from.Length ? Math.Clamp((distance - from.Length) / (to.Length - from.Length), 0, 1) : 0;
                    return (from.Point.X + (t * (to.Point.X - from.Point.X)), from.Point.Y + (t * (to.Point.Y - from.Point.Y)));
                }
            }

            return _points[0].Point;
        }

        private static List<((double X, double Y) Point, double Length)> Build(Arc? start, (double X, double Y) before, (double X, double Y) lineStart, (double X, double Y) lineEnd, Arc? end)
        {
            var points = new List<((double X, double Y) Point, double Length)>();
            void Add((double X, double Y) point)
            {
                var length = points.Count == 0 ? 0 : points[^1].Length + Math.Sqrt(Math.Pow(point.X - points[^1].Point.X, 2) + Math.Pow(point.Y - points[^1].Point.Y, 2));
                points.Add((point, length));
            }

            foreach (var (point, _) in start?.Measure() ?? [(before, 0)])
            {
                Add(point);
            }

            Add(lineStart);
            Add(lineEnd);
            foreach (var (point, _) in end?.Measure() ?? [])
            {
                Add(point);
            }

            return points;
        }
    }

    /// <summary>A corner's quarter ellipse as a cubic Bezier curve, clockwise around the box.</summary>
    private sealed record Arc((double X, double Y) Start, (double X, double Y) Control1, (double X, double Y) Control2, (double X, double Y) End)
    {
        /// <summary>The directions from a corner's center to its curve's start, clockwise from the top left: left, up, right, down.</summary>
        private static readonly (double X, double Y)[] Directions = [(-1, 0), (0, 1), (1, 0), (0, -1)];

        /// <summary>The curve's length.</summary>
        public double Length => Measure().Last().Length;

        /// <summary>
        /// The curve of corner <paramref name="corner"/> (0 top left, clockwise) of the rounded rectangle with its
        /// bottom-left corner at (<paramref name="x"/>, <paramref name="y"/>); <c>null</c> for a square corner.
        /// </summary>
        public static Arc? Corner(RoundedCorners corners, int corner, double x, double y, double width, double height)
        {
            corner &= 3;
            var (rx, ry) = corners[corner];
            if (!corners.Rounded(corner))
            {
                return null;
            }

            var (cx, cy) = corner switch
            {
                0 => (x + rx, y + height - ry),
                1 => (x + width - rx, y + height - ry),
                2 => (x + width - rx, y + ry),
                _ => (x + rx, y + ry),
            };
            var (sx, sy) = Directions[corner];
            var (ex, ey) = Directions[(corner + 1) % 4];
            var k = RoundedCorners.Kappa;

            // Clockwise, the tangent at the start points the way the end lies from the center, and the reverse at the end.
            return new Arc(
                (cx + (rx * sx), cy + (ry * sy)),
                (cx + (rx * sx) + (k * rx * ex), cy + (ry * sy) + (k * ry * ey)),
                (cx + (rx * ex) + (k * rx * sx), cy + (ry * ey) + (k * ry * sy)),
                (cx + (rx * ex), cy + (ry * ey)));
        }

        /// <summary>Adds the curve to the current path (which must end at <see cref="Start"/>).</summary>
        public void AddTo(PdfCanvas canvas) => canvas.CurveTo(Control1.X, Control1.Y, Control2.X, Control2.Y, End.X, End.Y);

        /// <summary>The length along the curve to where it crosses a line (a point and a direction); half the length if it does not.</summary>
        public double LengthTo((double X, double Y, double DirectionX, double DirectionY) line)
        {
            double SideOf((double X, double Y) p) => (line.DirectionX * (p.Y - line.Y)) - (line.DirectionY * (p.X - line.X));
            var samples = Measure();
            var first = SideOf(samples[0].Point);
            for (var i = 1; i < samples.Count; i++)
            {
                var side = SideOf(samples[i].Point);
                if (side == 0 || Math.Sign(side) != Math.Sign(first))
                {
                    var previous = SideOf(samples[i - 1].Point);
                    var t = previous == side ? 0 : previous / (previous - side);
                    return samples[i - 1].Length + (t * (samples[i].Length - samples[i - 1].Length));
                }
            }

            return samples[^1].Length / 2;
        }

        /// <summary>The curve as line segments: points and their distances along it.</summary>
        public List<((double X, double Y) Point, double Length)> Measure()
        {
            var samples = new List<((double X, double Y) Point, double Length)>(ArcSamples + 1) { (Start, 0) };
            for (var i = 1; i <= ArcSamples; i++)
            {
                var t = (double)i / ArcSamples;
                var u = 1 - t;
                var (a, b, c, d) = (u * u * u, 3 * u * u * t, 3 * u * t * t, t * t * t);
                var point = ((a * Start.X) + (b * Control1.X) + (c * Control2.X) + (d * End.X), (a * Start.Y) + (b * Control1.Y) + (c * Control2.Y) + (d * End.Y));
                var (px, py) = samples[^1].Point;
                samples.Add((point, samples[^1].Length + Math.Sqrt(Math.Pow(point.Item1 - px, 2) + Math.Pow(point.Item2 - py, 2))));
            }

            return samples;
        }
    }
}
