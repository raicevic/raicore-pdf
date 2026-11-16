namespace RaiCore.Pdf.Layout;

/// <summary>Writing direction: of a run of text, or the inline direction of a block (CSS <c>direction</c>).</summary>
public enum TextDirection
{
    /// <summary>Left to right (Latin, Greek, Cyrillic, CJK, ...).</summary>
    LeftToRight,

    /// <summary>Right to left (Arabic, Hebrew, ...).</summary>
    RightToLeft,
}
