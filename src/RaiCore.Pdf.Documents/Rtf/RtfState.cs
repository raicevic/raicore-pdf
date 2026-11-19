using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Rtf;

/// <summary>
/// Character formatting while RTF is read, in RTF's own units (half points, twips, table indexes), scoped by groups.
/// A value type, so that a group's copy is independent; equal states give the same <see cref="RunFormat"/>.
/// </summary>
internal record struct RtfCharState
{
    /// <summary>The state <c>\plain</c> resets to when the document sets no defaults: 12 points in the default font.</summary>
    public static RtfCharState Plain => new() { Font = -1, FontSize = 24 };

    /// <summary>The font's number in the font table, or -1 for the default font.</summary>
    public int Font;

    /// <summary>The size in half points.</summary>
    public int FontSize;

    public bool Bold;
    public bool Italic;
    public bool Strike;
    public bool Caps;
    public bool SmallCaps;
    public bool Hidden;

    /// <summary>Deleted text of a revision (left out: revisions are accepted).</summary>
    public bool Deleted;

    public UnderlineStyle Underline;

    /// <summary>The text color's index in the color table (0 for automatic).</summary>
    public int Color;

    /// <summary>The highlight's index in the color table (0 for none).</summary>
    public int Highlight;

    /// <summary>The character shading's index in the color table (0 for none).</summary>
    public int Background;

    public VerticalPosition Position;

    /// <summary>A raise (positive) or lowering of the baseline in half points.</summary>
    public int Raise;

    /// <summary>Extra space between characters in twips.</summary>
    public int Spacing;

    public bool RightToLeft;
}

/// <summary>Which border the next border control words (<c>\brdrs</c>, <c>\brdrw</c>) describe.</summary>
internal enum RtfBorderTarget
{
    None,
    ParagraphTop,
    ParagraphBottom,
    ParagraphLeft,
    ParagraphRight,
    ParagraphBox,
    ParagraphBetween,
    RowTop,
    RowBottom,
    RowLeft,
    RowRight,
    RowHorizontal,
    RowVertical,
    CellTop,
    CellBottom,
    CellLeft,
    CellRight,
}

/// <summary>Paragraph formatting while RTF is read, in twips, scoped by groups (a value type, like <see cref="RtfCharState"/>).</summary>
internal record struct RtfParagraphState
{
    public WordAlignment Alignment;
    public int LeftIndent;
    public int RightIndent;
    public int FirstIndent;

    /// <summary>Whether the paragraph sets its indents itself (else a list level's apply).</summary>
    public bool HasIndent;

    public int SpaceBefore;
    public int SpaceAfter;

    /// <summary>The line spacing (<c>\sl</c>): 0 for single, positive for at least (or a multiple of 240ths), negative for exact.</summary>
    public int LineSpacing;

    /// <summary>Whether <see cref="LineSpacing"/> is a multiple of single spacing (<c>\slmult1</c>).</summary>
    public bool LineMultiple;

    public bool KeepLines;
    public bool KeepWithNext;
    public bool PageBreakBefore;
    public bool WidowControl;
    public bool RightToLeft;
    public bool ContextualSpacing;
    public bool InTable;

    /// <summary>The table nesting level (<c>\itap</c>); 1 for a plain table, with <see cref="InTable"/>.</summary>
    public int NestingLevel;

    /// <summary>The paragraph style's number.</summary>
    public int Style;

    /// <summary>The outline level, or -1 for body text.</summary>
    public int Outline;

    /// <summary>The tab stops (shared and replaced, never changed in place).</summary>
    public TabStop[]? Tabs;

    /// <summary>The alignment of the next tab stop (<c>\tqr</c>, <c>\tqc</c>, <c>\tqdec</c>).</summary>
    public TabAlignment PendingTabAlignment;

    /// <summary>The leader of the next tab stop (<c>\tldot</c>), or <c>\0</c>.</summary>
    public char PendingTabLeader;

    public BoxSides Borders;
    public BorderLine? Between;

    /// <summary>The background color's index (<c>\cbpat</c>), or 0.</summary>
    public int BackgroundColor;

    /// <summary>The pattern color's index (<c>\cfpat</c>), or 0 (black).</summary>
    public int PatternColor;

    /// <summary>The shading in hundredths of a percent (<c>\shading</c>).</summary>
    public int Shading;

    /// <summary>The list override (<c>\ls</c>), or 0.</summary>
    public int List;

    /// <summary>The list level (<c>\ilvl</c>).</summary>
    public int Level;

    /// <summary>The old-style paragraph numbering (<c>\pn</c>), or <c>null</c>.</summary>
    public RtfParagraphNumbering? Numbering;

    /// <summary>The state <c>\pard</c> resets to when the document sets no defaults.</summary>
    public static RtfParagraphState Plain => new() { Outline = -1 };
}

/// <summary>Old-style paragraph numbering (<c>\pn</c>, RTF before lists): a format, a start and text around the number.</summary>
internal sealed class RtfParagraphNumbering
{
    /// <summary>The level: 0 for body text (<c>\pnlvlbody</c>), 1 to 9 for outline levels, 10 for a bullet (<c>\pnlvlblt</c>), 11 for a continuation (no number).</summary>
    public int Level { get; set; }

    public NumberFormat Format { get; set; } = NumberFormat.Decimal;

    public int Start { get; set; } = 1;

    /// <summary>Whether the format was set (else the section-level definition of the level gives it).</summary>
    public bool HasFormat { get; set; }

    public string Before { get; set; } = string.Empty;

    public string After { get; set; } = string.Empty;

    /// <summary>The font of the number or bullet, or -1.</summary>
    public int Font { get; set; } = -1;

    /// <summary>The indent of the text (<c>\pnindent</c>), in twips.</summary>
    public int Indent { get; set; }

    /// <summary>Whether the number hangs in the indent (<c>\pnhang</c>).</summary>
    public bool Hang { get; set; }

    public RtfParagraphNumbering Clone() => (RtfParagraphNumbering)MemberwiseClone();
}

/// <summary>An entry of the font table.</summary>
/// <param name="Name">The family name.</param>
/// <param name="CodePage">The code page of its bytes (from <c>\fcharset</c> or <c>\cpg</c>), or 0 for the document's.</param>
/// <param name="Symbol">Whether it is a symbol font (<c>\fcharset2</c>, or named Symbol, Wingdings or Webdings).</param>
internal sealed record RtfFont(string Name, int CodePage, bool Symbol);

/// <summary>A style of the style sheet: its kind, name, base style and control words (applied when it is used).</summary>
internal sealed class RtfStyle
{
    /// <summary>Paragraph (<c>\s</c>), character (<c>\cs</c>) or table (<c>\ts</c>) style.</summary>
    public char Kind { get; init; } = 's';

    public int Number { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The number of the style it is based on, or -1.</summary>
    public int BasedOn { get; set; } = -1;

    /// <summary>Its control words with their parameters, in order.</summary>
    public List<(string Word, int Parameter, bool HasParameter)> Words { get; } = [];
}

/// <summary>A level of a list definition (<c>\listlevel</c>).</summary>
internal sealed class RtfListLevel
{
    public NumberFormat Format { get; set; } = NumberFormat.Decimal;

    public int Start { get; set; } = 1;

    /// <summary>The level text: the first character is its length, characters below 9 are the levels' numbers.</summary>
    public string Text { get; set; } = string.Empty;

    public LabelSuffix Suffix { get; set; }

    public bool Legal { get; set; }

    public bool NoRestart { get; set; }

    /// <summary>The control words of the label's character format and the level's indents.</summary>
    public List<(string Word, int Parameter, bool HasParameter)> Words { get; } = [];

    public RtfListLevel Clone()
    {
        var clone = (RtfListLevel)MemberwiseClone();
        return clone;
    }
}

/// <summary>A list definition (<c>\list</c>) with up to nine levels.</summary>
internal sealed class RtfList
{
    public int Id { get; set; }

    public List<RtfListLevel> Levels { get; } = [];
}

/// <summary>A list override (<c>\listoverride</c>): the list a paragraph's <c>\ls</c> uses, with start overrides.</summary>
internal sealed class RtfListOverride
{
    public int ListId { get; set; }

    public int Number { get; set; }

    /// <summary>Start values by level, or <c>null</c>.</summary>
    public int?[] Starts { get; } = new int?[9];

    /// <summary>How many <c>\lfolevel</c> groups were read (the next one's level).</summary>
    public int LevelCount { get; set; }

    /// <summary>Whole level definitions by level, or <c>null</c>.</summary>
    public RtfListLevel?[] Levels { get; } = new RtfListLevel?[9];
}

/// <summary>The definition of a table cell (<c>\cellx</c> and the cell control words before it).</summary>
internal sealed class RtfCellDefinition
{
    /// <summary>The right edge in twips from the left margin.</summary>
    public int Right { get; set; }

    public bool MergeFirst { get; set; }

    public bool Merged { get; set; }

    public bool VerticalMergeFirst { get; set; }

    public bool VerticalMerged { get; set; }

    public CellVerticalAlignment VerticalAlignment { get; set; }

    public BoxSides Borders { get; set; }

    public int BackgroundColor { get; set; }

    public int PatternColor { get; set; }

    public int Shading { get; set; }

    public RtfCellDefinition Clone() => (RtfCellDefinition)MemberwiseClone();
}

/// <summary>The definition of a table row (<c>\trowd</c> up to the next one): its cells and row-level formatting.</summary>
internal sealed class RtfRowDefinition
{
    /// <summary>The left edge of the row in twips from the left margin (<c>\trleft</c>).</summary>
    public int Left { get; set; }

    /// <summary>Half the space between cells (<c>\trgaph</c>), the cells' left and right padding.</summary>
    public int Gap { get; set; } = 108;

    public int? PaddingLeft { get; set; }

    public int? PaddingRight { get; set; }

    public int? PaddingTop { get; set; }

    public int? PaddingBottom { get; set; }

    public WordAlignment Alignment { get; set; }

    /// <summary>The row height in twips: positive for at least, negative for exact.</summary>
    public int Height { get; set; }

    public bool Header { get; set; }

    public bool Keep { get; set; }

    public bool RightToLeft { get; set; }

    public BoxSides Borders { get; set; }

    public BorderLine? InsideHorizontal { get; set; }

    public BorderLine? InsideVertical { get; set; }

    public List<RtfCellDefinition> Cells { get; } = [];

    /// <summary>The cell definition the next <c>\cellx</c> completes.</summary>
    public RtfCellDefinition Pending { get; set; } = new();
}
