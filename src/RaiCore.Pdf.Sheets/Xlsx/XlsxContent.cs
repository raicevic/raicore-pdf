namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// Counts the content a worksheet keeps in the parts it refers to, which the converter does not draw: its controls. Read only when diagnostics are wanted. (What its drawing holds is read by
/// <see cref="XlsxDrawing"/>, which counts what it cannot draw.)
/// </summary>
internal static class XlsxContent
{
    /// <summary>The namespace of markup compatibility (<c>mc:AlternateContent</c>, <c>mc:Choice</c>, <c>mc:Fallback</c>).</summary>
    public const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>
    /// Counts what the parts a worksheet refers to hold into <paramref name="unsupported"/>. A part that cannot be read
    /// (missing, or not well-formed XML) counts nothing; the limits still apply to reading it.
    /// </summary>
    public static void Collect(XlsxPackage package, string sheetPath, UnsupportedContent unsupported, Action? checkpoint)
    {
        foreach (var (_, type, target) in Relationships(package, sheetPath))
        {
            checkpoint?.Invoke();
            switch (type)
            {
                case "control":
                    unsupported.Add(SheetDiagnosticKind.OleObject); // An ActiveX control.
                    break;
                case "ctrlProp":
                    unsupported.Add(SheetDiagnosticKind.Shape); // A form control.
                    break;
            }
        }
    }

    private static List<(string Id, string Type, string Target)> Relationships(XlsxPackage package, string path)
    {
        try
        {
            return package.Relationships(path);
        }
        catch (SpreadsheetFormatException)
        {
            return [];
        }
    }
}
