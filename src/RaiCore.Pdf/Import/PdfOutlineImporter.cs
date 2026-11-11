using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf.Import;

/// <summary>
/// Reads the outline (bookmarks) of a file as <see cref="PdfOutlineItem"/>s pointing at imported pages. Items whose
/// destination is not an imported page are left out, and their children take their place. Loops in the
/// <c>/First</c>/<c>/Next</c> links are cut, and the nesting depth and number of items are limited.
/// </summary>
internal static class PdfOutlineImporter
{
    private const int MaxDepth = 64;

    private const int MaxItems = 100_000;

    public static List<PdfOutlineItem> Read(PdfReader reader, IReadOnlyDictionary<PdfReaderPage, PdfPage> pages)
    {
        if (reader.Resolve(reader.Catalog["Outlines"]) is not PdfDictionary outlines)
        {
            return [];
        }

        var count = 0;
        return ReadItems(reader, outlines["First"], pages, [], 0, ref count);
    }

    private static List<PdfOutlineItem> ReadItems(
        PdfReader reader, PdfObject? first, IReadOnlyDictionary<PdfReaderPage, PdfPage> pages, HashSet<int> visited, int depth, ref int count)
    {
        var items = new List<PdfOutlineItem>();
        if (depth > MaxDepth)
        {
            return items;
        }

        for (var node = first; node is not null && count < MaxItems; count++)
        {
            if ((node is PdfReference reference && !visited.Add(reference.ObjectNumber)) || reader.Resolve(node) is not PdfDictionary item)
            {
                break;
            }

            var children = ReadItems(reader, item["First"], pages, visited, depth + 1, ref count);
            var destination = reader.ResolveDestination(reader.DestinationOf(item));
            if (reader.PageOf(destination?.Items[0]) is { } sourcePage && pages.TryGetValue(sourcePage, out var page))
            {
                var title = reader.Resolve(item["Title"]) is PdfString text ? PdfText.Decode(text.Bytes) : string.Empty;
                var (left, top) = Position(reader, destination!);
                var outlineItem = new PdfOutlineItem(title, page, top - page.Origin.Y)
                {
                    Left = left - page.Origin.X,
                    IsOpen = reader.Resolve(item["Count"]) is not PdfInteger { Value: < 0 },
                };

                foreach (var child in children)
                {
                    outlineItem.Children.Add(child);
                }

                items.Add(outlineItem);
            }
            else
            {
                items.AddRange(children);
            }

            node = item["Next"];
        }

        return items;
    }

    /// <summary>The left and top of an explicit destination (ISO 32000-2, table 149), where it has them.</summary>
    private static (double? Left, double? Top) Position(PdfReader reader, PdfArray destination)
    {
        double? Number(int index) => index < destination.Items.Count
            ? reader.Resolve(destination.Items[index]) switch
            {
                PdfInteger integer => integer.Value,
                PdfReal real => real.Value,
                _ => null,
            }
            : null;

        return (reader.Resolve(destination.Items.ElementAtOrDefault(1)) as PdfName)?.Value switch
        {
            "XYZ" => (Number(2), Number(3)),
            "FitH" or "FitBH" => (null, Number(2)),
            "FitV" or "FitBV" => (Number(2), null),
            "FitR" => (Number(2), Number(5)),
            _ => (null, null),
        };
    }
}
