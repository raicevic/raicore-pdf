using System.Xml;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// Counts the content a worksheet keeps in the parts it refers to, which the converter does not draw: its comments,
/// pivot tables and controls. Read only when diagnostics are wanted. (What its drawing holds is read by
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
        int comments = 0, threads = 0;
        foreach (var (_, type, target) in Relationships(package, sheetPath))
        {
            checkpoint?.Invoke();
            switch (type)
            {
                case "comments":
                    Parse(package, target, reader => comments += Count(reader, "comment", checkpoint));
                    break;
                case "threadedComment":
                    Parse(package, target, reader => threads += Count(reader, "threadedComment", checkpoint, rootsOnly: true));
                    break;
                case "pivotTable":
                    unsupported.Add(SheetDiagnosticKind.PivotTable);
                    break;
                case "control":
                    unsupported.Add(SheetDiagnosticKind.OleObject); // An ActiveX control.
                    break;
                case "ctrlProp":
                    unsupported.Add(SheetDiagnosticKind.Shape); // A form control.
                    break;
            }
        }

        // Excel keeps a note for each thread of threaded comments beside it, for older readers.
        unsupported.Add(SheetDiagnosticKind.Comment, Math.Max(comments, threads));
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

    private static void Parse(XlsxPackage package, string path, Action<XmlReader> count)
    {
        try
        {
            package.Parse(path, reader =>
            {
                count(reader);
                return true;
            }, false);
        }
        catch (SpreadsheetFormatException)
        {
            // A damaged part: what it holds is not known.
        }
    }

    /// <summary>The elements of a name in a part (with <paramref name="rootsOnly"/>, only those that answer no other).</summary>
    private static int Count(XmlReader reader, string name, Action? checkpoint, bool rootsOnly = false)
    {
        var count = 0;
        var elements = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (++elements % 4096 == 0)
            {
                checkpoint?.Invoke();
            }

            if (reader.LocalName == name && !(rootsOnly && reader.GetAttribute("parentId") is not null))
            {
                count++;
            }
        }

        return count;
    }
}
