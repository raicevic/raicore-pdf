namespace RaiCore.Pdf.Slides.Model;

/// <summary>Where the text of a frame sits vertically.</summary>
internal enum TextAnchor
{
    /// <summary>At the top.</summary>
    Top,

    /// <summary>Centered.</summary>
    Middle,

    /// <summary>At the bottom.</summary>
    Bottom,
}

/// <summary>The direction of a frame's lines.</summary>
internal enum TextFlow
{
    /// <summary>Horizontal lines.</summary>
    Horizontal,

    /// <summary>Lines turned 90 degrees clockwise (reading top to bottom).</summary>
    Vertical,

    /// <summary>Lines turned 90 degrees counter-clockwise (reading bottom to top).</summary>
    Vertical270,
}

/// <summary>The text of a shape or table cell: its inner margins, anchoring, wrapping, autofit and paragraphs.</summary>
internal sealed class TextFrame
{
    /// <summary>The left inset in points.</summary>
    public double LeftInset { get; set; } = 7.2;

    /// <summary>The top inset in points.</summary>
    public double TopInset { get; set; } = 3.6;

    /// <summary>The right inset in points.</summary>
    public double RightInset { get; set; } = 7.2;

    /// <summary>The bottom inset in points.</summary>
    public double BottomInset { get; set; } = 3.6;

    /// <summary>The vertical anchoring.</summary>
    public TextAnchor Anchor { get; set; }

    /// <summary>Whether the text block is centered horizontally as a whole (its lines keep their alignment).</summary>
    public bool AnchorCenter { get; set; }

    /// <summary>Whether lines wrap at the frame's width (otherwise each paragraph is one line, wider if need be).</summary>
    public bool Wrap { get; set; } = true;

    /// <summary>The direction of the lines.</summary>
    public TextFlow Flow { get; set; }

    /// <summary>Whether text that does not fit is shrunk (PowerPoint's "shrink text on overflow").</summary>
    public bool ShrinkOnOverflow { get; set; }

    /// <summary>The font scale the file stores for shrunk text (1 for none).</summary>
    public double FontScale { get; set; } = 1;

    /// <summary>The reduction of line spacing the file stores for shrunk text (0 for none), as a fraction.</summary>
    public double LineSpacingReduction { get; set; }

    /// <summary>The paragraphs.</summary>
    public List<TextParagraph> Paragraphs { get; } = [];

    /// <summary>Whether the frame has any text to draw (not only empty paragraphs).</summary>
    public bool HasText => Paragraphs.Any(p => p.Runs.Any(r => r.Text.Length > 0));
}

/// <summary>A paragraph's horizontal alignment.</summary>
internal enum ParagraphAlignment
{
    /// <summary>Left.</summary>
    Left,

    /// <summary>Centered.</summary>
    Center,

    /// <summary>Right.</summary>
    Right,

    /// <summary>Justified (the last line left-aligned).</summary>
    Justify,
}

/// <summary>A spacing that is a multiple of the font size (percent) or a fixed number of points.</summary>
/// <param name="Value">The multiple (1 = single spacing for line spacing, 1 = one line for paragraph spacing), or points.</param>
/// <param name="IsPoints">Whether <paramref name="Value"/> is in points.</param>
internal readonly record struct Spacing(double Value, bool IsPoints)
{
    /// <summary>No space.</summary>
    public static Spacing None => new(0, true);

    /// <summary>Single line spacing.</summary>
    public static Spacing Single => new(1, false);
}

/// <summary>A paragraph: alignment, indents, spacing, bullet and runs, all resolved.</summary>
internal sealed class TextParagraph
{
    /// <summary>The alignment.</summary>
    public ParagraphAlignment Alignment { get; init; }

    /// <summary>The outline level, from 0.</summary>
    public int Level { get; init; }

    /// <summary>The left margin of the lines in points.</summary>
    public double MarginLeft { get; init; }

    /// <summary>The right margin of the lines in points.</summary>
    public double MarginRight { get; init; }

    /// <summary>The first line's indent relative to <see cref="MarginLeft"/> (negative: a hanging indent for the bullet).</summary>
    public double Indent { get; init; }

    /// <summary>The space above the paragraph.</summary>
    public Spacing SpaceBefore { get; init; } = Spacing.None;

    /// <summary>The space below the paragraph.</summary>
    public Spacing SpaceAfter { get; init; } = Spacing.None;

    /// <summary>The line spacing.</summary>
    public Spacing LineSpacing { get; init; } = Spacing.Single;

    /// <summary>The bullet, or <c>null</c> for none.</summary>
    public Bullet? Bullet { get; init; }

    /// <summary>
    /// The style of the paragraph's end (its font size sets the height of an empty paragraph, and the bullet's size and
    /// color follow the first run's, or this when there is none).
    /// </summary>
    public RunStyle EndStyle { get; init; } = new();

    /// <summary>The runs.</summary>
    public List<TextRun> Runs { get; } = [];
}

/// <summary>A bullet before the first line of a paragraph: a character, or a number already counted.</summary>
internal sealed record Bullet
{
    /// <summary>The bullet's text: a character, or a number with its punctuation (<c>3.</c>, <c>(c)</c>).</summary>
    public string Text { get; init; } = "\u2022";

    /// <summary>The font family of the bullet, or <c>null</c> for the first run's.</summary>
    public string? Font { get; init; }

    /// <summary>The bullet's color, or <c>null</c> for the first run's.</summary>
    public PdfColor? Color { get; init; }

    /// <summary>The bullet's size as a multiple of the first run's font size.</summary>
    public double SizeFactor { get; init; } = 1;

    /// <summary>The bullet's size in points, which wins over <see cref="SizeFactor"/>, or <c>null</c>.</summary>
    public double? SizePoints { get; init; }
}

/// <summary>The character formatting of a run, resolved.</summary>
internal sealed record RunStyle
{
    /// <summary>The Latin font family.</summary>
    public string Font { get; init; } = "Calibri";

    /// <summary>The East Asian font family, if any.</summary>
    public string? EastAsianFont { get; init; }

    /// <summary>The complex script font family, if any.</summary>
    public string? ComplexFont { get; init; }

    /// <summary>The size in points.</summary>
    public double Size { get; init; } = 18;

    /// <summary>Whether the text is bold.</summary>
    public bool Bold { get; init; }

    /// <summary>Whether the text is italic.</summary>
    public bool Italic { get; init; }

    /// <summary>Whether the text is underlined.</summary>
    public bool Underline { get; init; }

    /// <summary>Whether the underline is double.</summary>
    public bool DoubleUnderline { get; init; }

    /// <summary>Whether the text is struck through.</summary>
    public bool Strike { get; init; }

    /// <summary>The text color (with alpha).</summary>
    public PdfColor Color { get; init; } = PdfColor.Black;

    /// <summary>The baseline shift as a fraction of the size (positive: superscript).</summary>
    public double Baseline { get; init; }

    /// <summary>Whether lowercase letters are shown as capitals.</summary>
    public bool AllCaps { get; init; }

    /// <summary>The extra space between characters in points.</summary>
    public double CharacterSpacing { get; init; }
}

/// <summary>A run of text in one style (a line break is a run whose text is a line feed).</summary>
internal sealed class TextRun
{
    /// <summary>The text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The style.</summary>
    public RunStyle Style { get; init; } = new();

    /// <summary>The link the run opens, if any.</summary>
    public Uri? Link { get; init; }
}
