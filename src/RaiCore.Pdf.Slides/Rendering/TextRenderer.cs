using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Slides.Model;
using LayoutParagraph = RaiCore.Pdf.Layout.Paragraph;

namespace RaiCore.Pdf.Slides.Rendering;

/// <summary>
/// Lays out and draws text frames with the layout engine's paragraphs: insets, vertical anchoring, wrapping, vertical
/// text, bullets and numbers, paragraph spacing and line spacing, and shrinking text that does not fit a frame set to
/// shrink on overflow.
/// </summary>
internal sealed class TextRenderer(SlideFonts fonts)
{
    /// <summary>Single line spacing as a multiple of the font size, as PowerPoint spaces lines.</summary>
    internal const double SingleSpacing = 1.2;

    /// <summary>The smallest font scale shrinking goes down to.</summary>
    private const double MinimumScale = 0.25;

    /// <summary>A paragraph laid out: its lines (or the height of an empty one), bullet and place in the block.</summary>
    internal sealed class LaidParagraph
    {
        public required TextParagraph Source { get; init; }

        public ParagraphLayout? Layout { get; init; }

        public double Height { get; init; }

        public double SpaceBefore { get; init; }

        public double SpaceAfter { get; init; }

        /// <summary>The left edge of the lines, from the block's left edge.</summary>
        public double X { get; init; }

        public ParagraphLayout? Bullet { get; init; }

        /// <summary>The bullet's left edge, from the block's left edge.</summary>
        public double BulletX { get; init; }

        /// <summary>The top, from the block's top (set when the block is stacked).</summary>
        public double Top { get; set; }

        public bool HasText => Source.Runs.Any(r => r.Text.Trim().Length > 0);
    }

    /// <summary>A frame's paragraphs laid out at a width.</summary>
    internal sealed class TextBlock
    {
        public List<LaidParagraph> Paragraphs { get; } = [];

        public double Height { get; set; }

        public double Width { get; init; }

        /// <summary>The font scale the text was laid out at.</summary>
        public double Scale { get; init; } = 1;

        /// <summary>The widest line, from the block's left edge.</summary>
        public double ContentWidth { get; set; }
    }

    /// <summary>
    /// Lays out a frame's paragraphs for an area (inside the insets), shrinking them when the frame shrinks text on
    /// overflow and they are taller than <paramref name="height"/>.
    /// </summary>
    public TextBlock Layout(TextFrame frame, double width, double height)
    {
        var scale = frame.ShrinkOnOverflow ? frame.FontScale : 1;
        var reduction = frame.ShrinkOnOverflow ? frame.LineSpacingReduction : 0;
        var block = Layout(frame, width, scale, reduction);
        for (var i = 0; frame.ShrinkOnOverflow && block.Height > height + 0.5 && scale > MinimumScale && i < 24; i++)
        {
            scale = Math.Max(scale * 0.93, MinimumScale);
            block = Layout(frame, width, scale, reduction);
        }

        return block;
    }

    /// <summary>Lays out a frame's paragraphs at a font scale and line spacing reduction.</summary>
    internal TextBlock Layout(TextFrame frame, double width, double scale, double reduction)
    {
        var block = new TextBlock { Width = width, Scale = scale };
        var y = 0.0;
        for (var i = 0; i < frame.Paragraphs.Count; i++)
        {
            var laid = Layout(frame.Paragraphs[i], width, scale, reduction, frame.Wrap);
            if (i > 0)
            {
                y += laid.SpaceBefore;
            }

            laid.Top = y;
            y += laid.Height;
            if (i < frame.Paragraphs.Count - 1)
            {
                y += laid.SpaceAfter;
            }

            block.Paragraphs.Add(laid);
            block.ContentWidth = Math.Max(block.ContentWidth, laid.X + (laid.Layout?.ContentWidth ?? 0));
        }

        block.Height = y;
        return block;
    }

    /// <summary>The layout engine's style of a run at a scale.</summary>
    internal TextStyle Style(RunStyle run, double scale, LineHeight lineHeight)
    {
        var size = run.Size * scale * (run.Baseline != 0 ? 2.0 / 3 : 1);
        var decorations = new List<TextDecoration>();
        if (run.Underline)
        {
            decorations.Add(new TextDecoration(TextDecorationLines.Underline, run.Color)
            {
                Style = run.DoubleUnderline ? TextDecorationStyle.Double : TextDecorationStyle.Solid,
            });
        }

        if (run.Strike)
        {
            decorations.Add(new TextDecoration(TextDecorationLines.LineThrough, run.Color));
        }

        return new TextStyle(fonts.Get(run.Font, run.EastAsianFont, run.ComplexFont, run.Bold, run.Italic), size)
        {
            Color = run.Color,
            Decorations = decorations,
            LetterSpacing = run.CharacterSpacing * scale,
            LineHeight = lineHeight,
            VerticalAlign = run.Baseline != 0 ? InlineAlignment.Raised(run.Baseline * run.Size * scale) : InlineAlignment.Baseline,
        };
    }

    /// <summary>The text a run shows: capitals for all-caps runs, tabs as spaces.</summary>
    private static string Display(TextRun run)
    {
        var text = run.Text.Replace("\t", "    ", StringComparison.Ordinal);
        return run.Style.AllCaps ? text.ToUpperInvariant() : text;
    }

    /// <summary>Lays out one paragraph at a width.</summary>
    private LaidParagraph Layout(TextParagraph source, double width, double scale, double reduction, bool wrap)
    {
        var runs = source.Runs.Where(r => r.Text.Length > 0).ToList();
        var first = runs.Count > 0 ? runs[0].Style : source.EndStyle;
        var firstSize = first.Size * scale;
        var lineHeight = source.LineSpacing.IsPoints
            ? LineHeight.Points(Math.Max(source.LineSpacing.Value, 0.1))
            : LineHeight.Multiple(SingleSpacing * Math.Max(source.LineSpacing.Value - reduction, 0.05));
        double Space(Spacing spacing) => spacing.IsPoints ? spacing.Value : spacing.Value * SingleSpacing * firstSize;

        var spaceBefore = Space(source.SpaceBefore);
        var spaceAfter = Space(source.SpaceAfter);
        var available = Math.Max(width - source.MarginLeft - source.MarginRight, 1);
        var baseStyle = Style(first, scale, lineHeight);
        if (!runs.Any(r => r.Text.Trim().Length > 0) && !runs.Any(r => r.Text.Contains('\n', StringComparison.Ordinal)))
        {
            // An empty paragraph is one line of its end style.
            return new LaidParagraph
            {
                Source = source,
                Height = lineHeight.Resolve(baseStyle.Fonts[0], baseStyle.FontSize),
                SpaceBefore = spaceBefore,
                SpaceAfter = spaceAfter,
                X = source.MarginLeft,
            };
        }

        // The bullet: before the first line, at the indent; the first line starts after it.
        ParagraphLayout? bullet = null;
        var bulletX = source.MarginLeft + source.Indent;
        var firstLineIndent = Math.Max(source.Indent, -source.MarginLeft);
        if (source.Bullet is { } b)
        {
            var bulletRun = first with
            {
                Font = b.Font ?? first.Font,
                Color = b.Color ?? first.Color,
                Size = b.SizePoints ?? (first.Size * b.SizeFactor),
                Underline = false,
                Strike = false,
                Baseline = 0,
                AllCaps = false,
            };
            var style = Style(bulletRun, b.SizePoints is null ? scale : 1, lineHeight);
            var text = b.Text;
            if (!style.Fonts.Any(f => f.HasGlyphs(text)))
            {
                text = "\u2022";
            }

            var bulletParagraph = new LayoutParagraph(style).Add(text);
            var bulletWidth = bulletParagraph.MeasureIntrinsicWidths().Max;
            bullet = bulletParagraph.Layout(Math.Max(bulletWidth, 1) + 1);
            var start = Math.Max(source.MarginLeft, bulletX + bulletWidth + (source.Indent >= 0 || bulletX + bulletWidth > source.MarginLeft ? firstSize * 0.3 : 0));
            firstLineIndent = start - source.MarginLeft;
        }

        var paragraph = new LayoutParagraph(baseStyle)
        {
            Alignment = source.Alignment switch
            {
                ParagraphAlignment.Center => TextAlignment.Center,
                ParagraphAlignment.Right => TextAlignment.Right,
                ParagraphAlignment.Justify => TextAlignment.Justify,
                _ => TextAlignment.Left,
            },
            FirstLineIndent = firstLineIndent,
            BreakLongWords = true,
        };

        foreach (var run in runs)
        {
            var style = Style(run.Style, scale, lineHeight);
            if (run.Link is { } link)
            {
                paragraph.AddLink(Display(run), LinkTarget.ToUri(link), style);
            }
            else
            {
                paragraph.Add(Display(run), style);
            }
        }

        var x = source.MarginLeft;
        var layoutWidth = available;
        if (!wrap)
        {
            // Lines that do not wrap are as long as they need, placed by the alignment around the frame's width.
            var natural = paragraph.MeasureIntrinsicWidths().Max + Math.Max(firstLineIndent, 0) + 0.01;
            if (natural > available)
            {
                layoutWidth = natural;
                x += paragraph.Alignment switch
                {
                    TextAlignment.Center => (available - natural) / 2,
                    TextAlignment.Right => available - natural,
                    _ => 0,
                };
            }
        }

        var layout = paragraph.Layout(layoutWidth);
        if (bullet is not null && source.Alignment is ParagraphAlignment.Center or ParagraphAlignment.Right && layout.Lines.Count > 0)
        {
            // A centered or right-aligned paragraph's bullet stays just before its first line.
            bulletX = x + layout.Lines[0].X + firstLineIndent - bullet.ContentWidth - (firstSize * 0.3);
        }

        return new LaidParagraph
        {
            Source = source,
            Layout = layout,
            Height = layout.Height,
            SpaceBefore = spaceBefore,
            SpaceAfter = spaceAfter,
            X = x,
            Bullet = bullet,
            BulletX = bulletX,
        };
    }

    /// <summary>
    /// Draws a frame in a rectangle of a shape: <paramref name="toPage"/> maps the shape's own coordinates (y
    /// downwards) to the page. Text is never mirrored: in a mirrored shape it is turned instead, as PowerPoint does.
    /// </summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="frame">The frame.</param>
    /// <param name="toPage">The shape's transformation to the page.</param>
    /// <param name="bounds">The rectangle the frame's insets are inside of, in the shape's coordinates.</param>
    /// <param name="parent">The structure element the paragraphs are tagged under, or <c>null</c> when untagged.</param>
    /// <param name="heading">Whether the paragraphs are headings (a title).</param>
    public void Draw(PdfCanvas canvas, TextFrame frame, Affine toPage, (double X, double Y, double Width, double Height) bounds, PdfStructureElement? parent, bool heading)
    {
        if (!frame.HasText)
        {
            return;
        }

        var left = bounds.X + frame.LeftInset;
        var top = bounds.Y + frame.TopInset;
        var boxWidth = Math.Max(bounds.Width - frame.LeftInset - frame.RightInset, 1);
        var boxHeight = Math.Max(bounds.Height - frame.TopInset - frame.BottomInset, 1);
        var vertical = frame.Flow != TextFlow.Horizontal;
        var (width, height) = vertical ? (boxHeight, boxWidth) : (boxWidth, boxHeight);
        var block = Layout(frame, width, height);

        // The text's own coordinates (y up, origin at the bottom left of its area) to the shape's, then the page's.
        var flow = frame.Flow switch
        {
            TextFlow.Vertical => new Affine(0, 1, -1, 0, left + boxWidth, top),
            TextFlow.Vertical270 => new Affine(0, -1, 1, 0, left, top + boxHeight),
            _ => Affine.Translate(left, top),
        };
        var toText = new Affine(1, 0, 0, -1, 0, height).Then(flow).Then(toPage);
        if (toText.Determinant < 0)
        {
            toText = new Affine(-1, 0, 0, 1, width, 0).Then(toText);
        }

        if (!toText.IsFinite)
        {
            return;
        }

        var offset = frame.Anchor switch
        {
            TextAnchor.Middle => (height - block.Height) / 2,
            TextAnchor.Bottom => height - block.Height,
            _ => 0,
        };
        var shift = frame.AnchorCenter ? (width - block.ContentWidth) / 2 : 0;
        canvas.SaveState();
        canvas.Transform(toText.A, toText.B, toText.C, toText.D, toText.E, toText.F);
        foreach (var paragraph in block.Paragraphs)
        {
            if (paragraph.Layout is not { } layout || layout.Lines.Count == 0)
            {
                continue;
            }

            var element = parent is not null && paragraph.HasText ? parent.AddChild(heading ? "H1" : "P") : null;
            if (element is not null)
            {
                canvas.BeginTag(element);
            }

            var paragraphTop = height - offset - paragraph.Top;
            if (paragraph.Bullet is { Lines.Count: > 0 } bullet)
            {
                var baseline = paragraphTop - layout.Lines[0].Top - layout.Lines[0].Baseline;
                bullet.Draw(canvas, shift + paragraph.BulletX, baseline + bullet.Lines[0].Baseline);
            }

            layout.Draw(canvas, shift + paragraph.X, paragraphTop);
            if (element is not null)
            {
                canvas.EndTag();
            }
        }

        canvas.RestoreState();
    }
}
