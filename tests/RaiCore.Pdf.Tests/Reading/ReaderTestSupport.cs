using System.Globalization;
using System.Text;
using PDFtoImage;
using RaiCore.Pdf.Objects;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Reading;

/// <summary>Builds small PDF files by hand, with a correct classic cross-reference table.</summary>
internal static class TestPdf
{
    /// <summary>
    /// A PDF whose objects are numbered from 1 in the order given; object 1 is the catalog unless the trailer says
    /// otherwise. Bodies are Latin-1 text.
    /// </summary>
    public static byte[] Build(IReadOnlyList<string> objects, string trailer = "/Root 1 0 R")
    {
        var data = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(data.Length);
            data.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = data.Length;
        data.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f\r\n");
        foreach (var offset in offsets)
        {
            data.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n");
        }

        data.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} {trailer} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(data.ToString());
    }

    /// <summary>A stream object body with the right <c>/Length</c>.</summary>
    public static string Stream(string content, string entries = "") =>
        $"<< /Length {Encoding.Latin1.GetByteCount(content)} {entries} >>\nstream\n{content}\nendstream";

    /// <summary>A one-level page tree: catalog (1), pages (2), then one page object per content string, then the contents.</summary>
    public static byte[] Pages(string pagesEntries, params (string Entries, string Content)[] pages)
    {
        var kids = string.Join(' ', Enumerable.Range(0, pages.Length).Select(i => $"{3 + i} 0 R"));
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", $"<< /Type /Pages /Kids [{kids}] /Count {pages.Length} {pagesEntries} >>" };
        for (var i = 0; i < pages.Length; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R {pages[i].Entries} /Contents {3 + pages.Length + i} 0 R >>");
        }

        objects.AddRange(pages.Select(p => Stream(p.Content)));
        return Build(objects);
    }

    public static string Text(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}

/// <summary>Renders pages with PDFium and compares them.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal static class Pixels
{
    public static SKBitmap Render(byte[] pdf, int page, int dpi = 72) =>
        Conversion.ToImage(pdf, page: page, options: new RenderOptions(Dpi: dpi));

    /// <summary>How many pixels differ by more than <paramref name="tolerance"/> in any channel (the images must be the same size).</summary>
    public static int CountDifferences(SKBitmap a, SKBitmap b, int tolerance = 8)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        var count = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                if (Math.Abs(p.Red - q.Red) > tolerance || Math.Abs(p.Green - q.Green) > tolerance || Math.Abs(p.Blue - q.Blue) > tolerance)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Asserts that two renderings are the same, but for a few anti-aliasing pixels.</summary>
    public static void AssertSame(SKBitmap expected, SKBitmap actual, double maxDifferentFraction = 0.001)
    {
        var differences = CountDifferences(expected, actual);
        Assert.True(
            differences <= expected.Width * expected.Height * maxDifferentFraction,
            $"{differences} of {expected.Width * expected.Height} pixels differ.");
    }

    /// <summary>The color at (x, y) measured from the bottom-left corner, as PDF coordinates are (72 dpi).</summary>
    public static SKColor At(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, bitmap.Height - 1 - y);

    public static void AssertColor(SKColor expected, SKColor actual, int tolerance = 16) =>
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance
                && Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"Expected {expected}, got {actual}.");
}

/// <summary>Reads back a PDF the library wrote, with the library's own reader.</summary>
internal static class Output
{
    /// <summary>All indirect objects of a file, resolved.</summary>
    public static List<PdfObject> Objects(PdfReader reader) =>
        [.. reader.Store.ObjectNumbers.Select(n => reader.Resolve(new PdfReference(n))).OfType<PdfObject>()];

    /// <summary>The dictionaries of all objects (streams' dictionaries included) with this <c>/Type</c> (or <c>/Subtype</c> when given).</summary>
    public static List<PdfDictionary> OfType(PdfReader reader, string type, string? subtype = null) =>
        [.. Objects(reader)
            .Select(o => o switch { PdfStreamObject s => s.Dictionary, PdfDictionary d => d, _ => null })
            .OfType<PdfDictionary>()
            .Where(d => (d["Type"] as PdfName)?.Value == type && (subtype is null || (d["Subtype"] as PdfName)?.Value == subtype))];
}
