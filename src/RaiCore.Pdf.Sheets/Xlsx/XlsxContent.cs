using System.Xml;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// Counts the content a worksheet keeps in the parts it refers to, which the converter does not draw: the pictures,
/// shapes and charts of its drawing, its comments, pivot tables and controls. Read only when diagnostics are wanted.
/// </summary>
internal static class XlsxContent
{
    /// <summary>The namespace of markup compatibility (<c>mc:AlternateContent</c>, <c>mc:Choice</c>, <c>mc:Fallback</c>).</summary>
    public const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>The kinds of drawing objects counted, in a drawing anchor.</summary>
    private enum DrawingObject
    {
        Picture,
        Shape,
        Chart,
    }

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
                case "drawing":
                    Parse(package, target, reader => CountDrawing(reader, unsupported, checkpoint));
                    break;
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

    /// <summary>
    /// Counts the objects of a drawing part (<c>xdr:wsDr</c>): in each anchor, its pictures, shapes (also connectors,
    /// text boxes, ink, SmartArt and other graphic frames) and charts, those in groups too. Anchors that do not print
    /// (<c>clientData/@fPrintsWithSheet="0"</c>) or whose object is hidden are left out, and so is the fallback of
    /// markup compatibility, which repeats its choice.
    /// </summary>
    internal static void CountDrawing(XmlReader reader, UnsupportedContent unsupported, Action? checkpoint)
    {
        var pending = new int[3];
        var counted = new int[3];
        var anchorDepth = -1;
        var objectDepth = -1;
        var fallbackDepth = -1;
        var skip = false;
        var elements = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == anchorDepth)
            {
                for (var i = 0; i < counted.Length && !skip; i++)
                {
                    counted[i] += pending[i];
                }

                anchorDepth = -1;
                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (++elements % 4096 == 0)
            {
                checkpoint?.Invoke();
            }

            var depth = reader.Depth;
            if (fallbackDepth >= 0 && depth > fallbackDepth)
            {
                continue;
            }

            fallbackDepth = -1;
            var name = reader.LocalName;
            if (name == "Fallback" && reader.NamespaceURI == MarkupCompatibility)
            {
                fallbackDepth = depth;
                continue;
            }

            if (name is "twoCellAnchor" or "oneCellAnchor" or "absoluteAnchor")
            {
                if (!reader.IsEmptyElement)
                {
                    anchorDepth = depth;
                    objectDepth = -1;
                    skip = false;
                    Array.Clear(pending);
                }

                continue;
            }

            if (anchorDepth < 0)
            {
                continue;
            }

            switch (name)
            {
                case "pic":
                    pending[(int)DrawingObject.Picture]++;
                    break;
                case "sp" or "cxnSp" or "contentPart":
                    pending[(int)DrawingObject.Shape]++;
                    break;
                case "graphicData":
                    var uri = reader.GetAttribute("uri") ?? string.Empty;
                    pending[(int)(uri.EndsWith("/chart", StringComparison.OrdinalIgnoreCase) || uri.EndsWith("/chartex", StringComparison.OrdinalIgnoreCase)
                        ? DrawingObject.Chart : DrawingObject.Shape)]++;
                    break;
                case "clientData":
                    skip |= reader.GetAttribute("fPrintsWithSheet") is "0" or "false";
                    break;
                case "cNvPr":
                    // The anchor's object is hidden (as Excel hides the stand-ins of controls and embedded objects).
                    skip |= depth == objectDepth + 2 && reader.GetAttribute("hidden") is "1" or "true";
                    break;
            }

            if (depth == anchorDepth + 1 && name is "pic" or "sp" or "cxnSp" or "graphicFrame" or "grpSp" or "contentPart")
            {
                objectDepth = depth;
            }
        }

        // Counted once the whole part is read: a part that turns out to be damaged counts nothing.
        unsupported.Add(SheetDiagnosticKind.Picture, counted[(int)DrawingObject.Picture]);
        unsupported.Add(SheetDiagnosticKind.Shape, counted[(int)DrawingObject.Shape]);
        unsupported.Add(SheetDiagnosticKind.Chart, counted[(int)DrawingObject.Chart]);
    }
}
