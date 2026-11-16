namespace RaiCore.Pdf.Layout;

/// <summary>Horizontal position of a block narrower than the width it is given (an image or a table).</summary>
public enum BlockAlignment
{
    /// <summary>At the left edge (the default).</summary>
    Left,

    /// <summary>Centered (CSS <c>margin: 0 auto</c>).</summary>
    Center,

    /// <summary>At the right edge.</summary>
    Right,
}
