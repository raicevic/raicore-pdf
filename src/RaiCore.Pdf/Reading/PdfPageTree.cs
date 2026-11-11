using System.Text;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Reading;

/// <summary>
/// Reads the page tree (ISO 32000-2, section 7.7.3): the pages in order, with the attributes they inherit from their
/// ancestors (<c>/Resources</c>, <c>/MediaBox</c>, <c>/CropBox</c> and <c>/Rotate</c>). The walk uses its own stack and
/// visits every node once, so loops and deep trees cannot hang it or exhaust the stack. When the tree yields no pages
/// (a damaged file), every object of type <c>/Page</c> is a page, in object order.
/// </summary>
internal static class PdfPageTree
{
    /// <summary>The deepest page tree accepted.</summary>
    private const int MaxDepth = 256;

    /// <summary>The size of a page with no valid media box: US Letter, as viewers assume.</summary>
    private static readonly PdfRectangle DefaultMediaBox = new(0, 0, 612, 792);

    public static List<PdfReaderPage> Read(PdfReader reader, PdfDictionary catalog, HashSet<int> nodes)
    {
        var pages = new List<PdfReaderPage>();
        var visited = new HashSet<int>();
        var stack = new Stack<(PdfObject Node, Inherited Attributes, int Depth)>();
        if (catalog["Pages"] is { } root)
        {
            stack.Push((root, default, 0));
        }

        while (stack.Count > 0)
        {
            var (node, inherited, depth) = stack.Pop();
            var reference = node as PdfReference;
            if ((reference is not null && !visited.Add(reference.ObjectNumber)) || depth > MaxDepth
                || reader.Resolve(node) is not PdfDictionary dictionary)
            {
                continue;
            }

            var attributes = inherited.With(dictionary);
            var type = (reader.Resolve(dictionary["Type"]) as PdfName)?.Value;
            var kids = reader.Resolve(dictionary["Kids"]) as PdfArray;
            if (type == "Pages" || (type != "Page" && kids is not null))
            {
                if (reference is not null)
                {
                    nodes.Add(reference.ObjectNumber);
                }

                for (var i = (kids?.Items.Count ?? 0) - 1; i >= 0; i--)
                {
                    stack.Push((kids!.Items[i], attributes, depth + 1));
                }
            }
            else
            {
                pages.Add(CreatePage(reader, pages.Count, reference, dictionary, attributes));
            }
        }

        if (pages.Count == 0)
        {
            foreach (var number in reader.Store.ObjectNumbers)
            {
                var reference = new PdfReference(number);
                if (reader.Resolve(reference) is PdfDictionary dictionary && reader.Resolve(dictionary["Type"]) is PdfName { Value: "Page" })
                {
                    pages.Add(CreatePage(reader, pages.Count, reference, dictionary, default(Inherited).With(dictionary)));
                }
            }
        }

        return pages;
    }

    private static PdfReaderPage CreatePage(PdfReader reader, int index, PdfReference? reference, PdfDictionary dictionary, Inherited attributes)
    {
        var mediaBox = Rectangle(reader, attributes.MediaBox) is { Width: > 0, Height: > 0 } box ? box : DefaultMediaBox;
        var cropBox = Rectangle(reader, attributes.CropBox) is { } crop && crop.Intersect(mediaBox) is { } visible ? visible : mediaBox;
        var rotate = reader.Resolve(attributes.Rotate) switch
        {
            PdfInteger integer when integer.Value % 90 == 0 => (int)(((integer.Value % 360) + 360) % 360),
            _ => 0,
        };

        return new PdfReaderPage(reader, index, reference, dictionary, attributes.Resources, mediaBox, cropBox, rotate);
    }

    /// <summary>A rectangle array <c>[x1 y1 x2 y2]</c>, or <c>null</c> when it is not one.</summary>
    internal static PdfRectangle? Rectangle(PdfReader reader, PdfObject? value)
    {
        if (reader.Resolve(value) is not PdfArray { Items.Count: 4 } array)
        {
            return null;
        }

        var numbers = new double[4];
        for (var i = 0; i < 4; i++)
        {
            switch (reader.Resolve(array.Items[i]))
            {
                case PdfInteger integer:
                    numbers[i] = integer.Value;
                    break;
                case PdfReal real:
                    numbers[i] = real.Value;
                    break;
                default:
                    return null;
            }
        }

        return numbers.All(n => Math.Abs(n) < 1e7) ? PdfRectangle.FromCorners(numbers[0], numbers[1], numbers[2], numbers[3]) : null;
    }

    /// <summary>The inheritable attributes, as found on the nearest node that has them.</summary>
    private readonly record struct Inherited(PdfObject? Resources, PdfObject? MediaBox, PdfObject? CropBox, PdfObject? Rotate)
    {
        public Inherited With(PdfDictionary node) => new(
            node["Resources"] ?? Resources, node["MediaBox"] ?? MediaBox, node["CropBox"] ?? CropBox, node["Rotate"] ?? Rotate);
    }
}

/// <summary>Reads name trees (ISO 32000-2, section 7.9.6), such as the named destinations, into a dictionary.</summary>
internal static class PdfNameTree
{
    private const int MaxDepth = 64;

    /// <summary>The most entries read from one tree.</summary>
    private const int MaxEntries = 1_000_000;

    /// <summary>Adds the tree's entries to <paramref name="names"/>, keyed by the bytes of each key as Latin-1.</summary>
    public static void Read(PdfReader reader, PdfObject? root, Dictionary<string, PdfObject> names)
    {
        var visited = new HashSet<int>();
        var stack = new Stack<(PdfObject Node, int Depth)>();
        if (root is not null)
        {
            stack.Push((root, 0));
        }

        while (stack.Count > 0 && names.Count < MaxEntries)
        {
            var (node, depth) = stack.Pop();
            if ((node is PdfReference reference && !visited.Add(reference.ObjectNumber)) || depth > MaxDepth
                || reader.Resolve(node) is not PdfDictionary dictionary)
            {
                continue;
            }

            if (reader.Resolve(dictionary["Names"]) is PdfArray pairs)
            {
                for (var i = 0; i + 1 < pairs.Items.Count; i += 2)
                {
                    if (reader.Resolve(pairs.Items[i]) is PdfString key)
                    {
                        names.TryAdd(Encoding.Latin1.GetString(key.Bytes), pairs.Items[i + 1]);
                    }
                }
            }

            if (reader.Resolve(dictionary["Kids"]) is PdfArray kids)
            {
                for (var i = kids.Items.Count - 1; i >= 0; i--)
                {
                    stack.Push((kids.Items[i], depth + 1));
                }
            }
        }
    }
}

/// <summary>Decodes PDF text strings (ISO 32000-2, 7.9.2.2): UTF-16BE or UTF-8 with a byte order mark, else PDFDocEncoding.</summary>
internal static class PdfText
{
    /// <summary>PDFDocEncoding where it differs from Latin-1: 0x18-0x1F and 0x80-0xA0 (0x9F is undefined).</summary>
    private static readonly char[] Low = ['\u02D8', '\u02C7', '\u02C6', '\u02D9', '\u02DD', '\u02DB', '\u02DA', '\u02DC'];

    private static readonly char[] High =
    [
        '\u2022', '\u2020', '\u2021', '\u2026', '\u2014', '\u2013', '\u0192', '\u2044', '\u2039', '\u203A', '\u2212', '\u2030',
        '\u201E', '\u201C', '\u201D', '\u2018', '\u2019', '\u201A', '\u2122', '\uFB01', '\uFB02', '\u0141', '\u0152', '\u0160',
        '\u0178', '\u017D', '\u0131', '\u0142', '\u0153', '\u0161', '\u017E', '\uFFFD', '\u20AC',
    ];

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        var text = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            text.Append(b switch
            {
                >= 0x18 and <= 0x1F => Low[b - 0x18],
                >= 0x80 and <= 0xA0 => High[b - 0x80],
                _ => (char)b,
            });
        }

        return text.ToString();
    }
}
