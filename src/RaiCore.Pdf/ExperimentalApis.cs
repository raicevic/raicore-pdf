namespace RaiCore.Pdf;

/// <summary>
/// The diagnostic IDs of APIs marked <see cref="System.Diagnostics.CodeAnalysis.ExperimentalAttribute"/>: public, but
/// free to change in minor versions after 1.0. See <c>docs/experimental.md</c>.
/// </summary>
internal static class ExperimentalApis
{
    /// <summary>The low-level PDF object model: <c>RaiCore.Pdf.Objects</c> and what <see cref="PdfReader"/> exposes of it.</summary>
    public const string PdfObjects = "RCPDF001";

    /// <summary>Text shaping, line breaking and positioned glyphs.</summary>
    public const string TextShaping = "RCPDF002";

    /// <summary>Where each diagnostic is explained.</summary>
    public const string UrlFormat = "https://github.com/raicevic/raicore-pdf/blob/main/docs/experimental.md#{0}";
}
