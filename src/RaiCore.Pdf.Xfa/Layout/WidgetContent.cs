using System.Xml.Linq;
using RaiCore.Pdf.Xfa.Template;

namespace RaiCore.Pdf.Xfa.Layout;

/// <summary>What a draw or field shows.</summary>
internal enum WidgetKind
{
    None,
    Text,
    Image,
    Line,
    Rectangle,
    Arc,
    CheckButton,
}

/// <summary>A draw's or field's content laid out at its width: caption, value and widget.</summary>
internal sealed record WidgetContent
{
    public Insets Margin { get; init; }

    public Border? Border { get; init; }

    /// <summary><c>left</c>, <c>right</c>, <c>top</c>, <c>bottom</c>; the caption is beside the value.</summary>
    public string CaptionPlacement { get; init; } = "left";

    /// <summary>The caption's size across its placement (its width for left and right).</summary>
    public double CaptionReserve { get; init; }

    public TextBlock? Caption { get; init; }

    public ParaSpec CaptionPara { get; init; } = ParaSpec.Default;

    public Insets CaptionMargin { get; init; }

    public WidgetKind Kind { get; init; }

    public TextBlock? Text { get; init; }

    public ParaSpec Para { get; init; } = ParaSpec.Default;

    /// <summary>The value's text, one character per cell, for a comb field; <c>null</c> otherwise.</summary>
    public string? CombText { get; init; }

    public int CombCells { get; init; }

    /// <summary>The text style of a comb field's characters.</summary>
    public RaiCore.Pdf.Layout.TextStyle? CombStyle { get; init; }

    public FontSpec Font { get; init; } = FontSpec.Default;

    public PdfImage? Image { get; init; }

    /// <summary>How an image fits its box: <c>fit</c>, <c>actual</c>, <c>none</c>, <c>width</c> or <c>height</c>.</summary>
    public string Aspect { get; init; } = "fit";

    /// <summary>A line, rectangle or arc value.</summary>
    public XElement? Shape { get; init; }

    public bool Checked { get; init; }

    /// <summary><c>square</c> or <c>round</c>.</summary>
    public string CheckShape { get; init; } = "square";

    /// <summary><c>check</c>, <c>circle</c>, <c>cross</c>, <c>diamond</c>, <c>square</c> or <c>star</c>.</summary>
    public string Mark { get; init; } = "check";

    public double CheckSize { get; init; } = 10;

    /// <summary>The widget's own border (around the value), if visible.</summary>
    public Border? UiBorder { get; init; }

    public Insets UiMargin { get; init; }

    /// <summary>
    /// Where the text shown starts, from the text block's top: a part of a text split across content areas shows the
    /// lines from here (0 for the first part).
    /// </summary>
    public double TextTop { get; init; }

    /// <summary>Where the text shown ends, from the text block's top, or <c>null</c> for its end.</summary>
    public double? TextBottom { get; init; }

    /// <summary>Whether the text can be split between its lines across content areas (it grows, and is not rotated or combed).</summary>
    public bool SplitsText { get; init; }
}
