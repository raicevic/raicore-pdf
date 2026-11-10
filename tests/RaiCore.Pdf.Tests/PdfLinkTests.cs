using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Tests;

/// <summary>Link annotations and named destinations, checked in the uncompressed file syntax.</summary>
public partial class PdfLinkTests
{
    [Fact]
    public void A_uri_link_is_a_borderless_link_annotation_with_a_uri_action()
    {
        var doc = NewDocument(out var page);
        page.AddLink(72, 700, 100.5, 14, new Uri("https://example.com/docs"));

        var pdf = Parse(doc);
        var annotation = pdf.Object(Assert.Single(pdf.Annotations(0)));

        Assert.Contains("/Type /Annot", annotation, StringComparison.Ordinal);
        Assert.Contains("/Subtype /Link", annotation, StringComparison.Ordinal);
        Assert.Contains("/Rect [72 700 172.5 714]", annotation, StringComparison.Ordinal);
        Assert.Contains("/Border [0 0 0]", annotation, StringComparison.Ordinal);
        Assert.Contains("/A <</S /URI/URI (https://example.com/docs)>>", annotation, StringComparison.Ordinal);
        Assert.DoesNotContain("/Dest", annotation, StringComparison.Ordinal);
    }

    [Fact]
    public void Uris_are_written_as_seven_bit_ascii_with_string_delimiters_escaped()
    {
        var doc = NewDocument(out var page);
        page.AddLink(0, 0, 10, 10, new Uri("https://example.com/a b(c)?q=\u00E6"));
        page.AddLink(0, 0, 10, 10, new Uri("docs/r\u00E9sum\u00E9 1.html", UriKind.Relative));
        page.AddLink(0, 0, 10, 10, new Uri("mailto:info@example.com?subject=Hello%20there"));

        var pdf = Parse(doc);
        var uris = pdf.Annotations(0).Select(n => Regex.Match(pdf.Object(n), @"/URI (\(.*?[^\\]\))>>").Groups[1].Value).ToList();

        Assert.Equal(
            [@"(https://example.com/a%20b\(c\)?q=%C3%A6)", "(docs/r%C3%A9sum%C3%A9%201.html)", "(mailto:info@example.com?subject=Hello%20there)"],
            uris);
    }

    [Fact]
    public void Links_to_named_destinations_refer_to_the_name_tree_in_the_catalog()
    {
        var doc = NewDocument(out var first);
        var second = doc.AddPage();
        first.AddLink(72, 700, 50, 12, "chapter-2");
        doc.NamedDestinations["chapter-2"] = new PdfDestination(second, 750);
        doc.NamedDestinations["appendix"] = new PdfDestination(first, 100.25, 36);
        doc.NamedDestinations["\u00C6"] = new PdfDestination(second);

        var pdf = Parse(doc);

        Assert.Contains("/Dest (chapter-2)", pdf.Object(Assert.Single(pdf.Annotations(0))), StringComparison.Ordinal);
        var names = pdf.Object(pdf.Ref(Value(pdf.Catalog, "Names"), "Dests"));

        // One leaf with the keys sorted by their bytes: ASCII before the UTF-16 string (which starts with FE FF).
        Assert.Equal(
            $"<</Names [(appendix) [{pdf.Pages[0]} 0 R /XYZ 36 100.25 null] (chapter-2) [{pdf.Pages[1]} 0 R /XYZ null 750 null] (\\376\\377\\000\\306) [{pdf.Pages[1]} 0 R /XYZ null null null]]>>",
            names);
    }

    [Fact]
    public void Links_may_be_added_before_their_destinations_and_unknown_names_are_left_out()
    {
        var doc = NewDocument(out var page);
        page.AddLink(0, 0, 10, 10, "later");
        page.AddLink(0, 20, 10, 10, "missing");
        doc.NamedDestinations["later"] = new PdfDestination(page, 500);

        var pdf = Parse(doc);

        Assert.Equal(2, page.Links.Count);
        Assert.Contains("/Dest (later)", pdf.Object(Assert.Single(pdf.Annotations(0))), StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_destinations_point_at_page_objects()
    {
        var doc = NewDocument(out var first);
        var second = doc.AddPage();
        second.AddLink(0, 0, 10, 10, new PdfDestination(first, 400));

        var pdf = Parse(doc);

        Assert.Empty(pdf.Annotations(0));
        Assert.Contains($"/Dest [{pdf.Pages[0]} 0 R /XYZ null 400 null]", pdf.Object(Assert.Single(pdf.Annotations(1))), StringComparison.Ordinal);
    }

    [Fact]
    public void Destinations_outside_the_document_are_rejected()
    {
        var elsewhere = new PdfDocument().AddPage();

        var withLink = NewDocument(out var page);
        page.AddLink(0, 0, 10, 10, new PdfDestination(elsewhere));
        Assert.Throws<InvalidOperationException>(withLink.ToArray);

        var withName = NewDocument(out _);
        withName.NamedDestinations["x"] = new PdfDestination(elsewhere);
        Assert.Throws<InvalidOperationException>(withName.ToArray);
    }

    [Fact]
    public void Without_links_or_destinations_nothing_is_written()
    {
        var text = Encoding.Latin1.GetString(NewDocument(out _).ToArray());

        Assert.DoesNotContain("/Annots", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Names", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rectangles_are_normalized_and_must_be_finite()
    {
        var doc = NewDocument(out var page);

        var link = page.AddLink(100, 200, -30, -10, new Uri("https://example.com"));

        Assert.Equal((70.0, 190.0, 30.0, 10.0), (link.X, link.Y, link.Width, link.Height));
        Assert.Throws<ArgumentException>(() => page.AddLink(double.NaN, 0, 1, 1, "x"));
        Assert.Throws<ArgumentException>(() => page.AddLink(0, 0, double.PositiveInfinity, 1, "x"));
        Assert.Throws<ArgumentException>(() => page.AddLink(0, 0, 1, 1, string.Empty));
    }

    [Fact]
    public void Canvas_links_follow_the_current_transformation()
    {
        var doc = NewDocument(out var page);
        var canvas = page.Canvas;

        canvas.SaveState().Transform(1, 0, 0, 1, 100, 50);
        var moved = canvas.AddLink(10, 20, 30, 40, "a");
        canvas.SaveState().Transform(2, 0, 0, 2, 0, 0);
        var scaled = canvas.AddLink(10, 20, 30, 40, "b");
        canvas.RestoreState();
        canvas.Transform(0, 1, -1, 0, 0, 0); // 90 degrees counterclockwise: the bounding box of the turned rectangle.
        var rotated = canvas.AddLink(10, 20, 30, 40, "c");
        canvas.RestoreState();
        var plain = canvas.AddLink(10, 20, 30, 40, "d");

        Assert.Equal((110.0, 70.0, 30.0, 40.0), Rect(moved));
        Assert.Equal((120.0, 90.0, 60.0, 80.0), Rect(scaled));
        Assert.Equal((40.0, 60.0, 40.0, 30.0), Rect(rotated));
        Assert.Equal((10.0, 20.0, 30.0, 40.0), Rect(plain));
    }

    [Fact]
    public void Passes_qpdf_structural_check()
    {
        var doc = new PdfDocument();
        var first = doc.AddPage();
        var second = doc.AddPage();
        first.AddLink(72, 700, 100, 14, new Uri("https://example.com/a b"));
        first.AddLink(72, 680, 100, 14, "two");
        first.AddLink(72, 660, 100, 14, new PdfDestination(second, 300));
        doc.NamedDestinations["two"] = new PdfDestination(second, 800);
        doc.NamedDestinations["\u00C6"] = new PdfDestination(first);

        ExternalTools.AssertQpdfClean(doc.ToArray());
    }

    private static (double, double, double, double) Rect(PdfLink link) => (link.X, link.Y, link.Width, link.Height);

    private static PdfDocument NewDocument(out PdfPage page)
    {
        var doc = new PdfDocument { CompressContent = false };
        doc.Info.CreationDate = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        page = doc.AddPage(PdfPageSize.A4);
        return doc;
    }

    private static ParsedPdf Parse(PdfDocument doc)
    {
        var text = Encoding.Latin1.GetString(doc.ToArray());
        var objects = ObjectRegex().Matches(text).ToDictionary(
            m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m => m.Groups[2].Value);
        var catalog = objects.Values.Single(o => o.StartsWith("<</Type /Catalog", StringComparison.Ordinal));
        var pageTree = objects.Values.Single(o => o.StartsWith("<</Type /Pages", StringComparison.Ordinal));
        var pages = ReferenceRegex().Matches(Value(pageTree, "Kids")).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        return new ParsedPdf(objects, catalog, pages);
    }

    /// <summary>The raw value of a dictionary entry: an array, a dictionary or a reference.</summary>
    private static string Value(string dictionary, string key)
    {
        var start = dictionary.IndexOf($"/{key} ", StringComparison.Ordinal);
        Assert.True(start >= 0, $"/{key} not found in {dictionary}");
        start += key.Length + 2;
        var (open, close) = dictionary[start] switch
        {
            '[' => ('[', ']'),
            '<' => ('<', '>'),
            _ => ('\0', '\0'),
        };
        if (open == '\0')
        {
            return ReferenceRegex().Match(dictionary, start).Value;
        }

        var depth = 0;
        for (var end = start; ; end++)
        {
            if (dictionary[end] == open)
            {
                depth++;
            }
            else if (dictionary[end] == close && --depth == 0)
            {
                return dictionary[start..(end + 1)];
            }
        }
    }

    [GeneratedRegex(@"(\d+) 0 obj\n(.*?)\nendobj", RegexOptions.Singleline)]
    private static partial Regex ObjectRegex();

    [GeneratedRegex(@"(\d+) 0 R")]
    private static partial Regex ReferenceRegex();

    private sealed record ParsedPdf(Dictionary<int, string> Objects, string Catalog, List<int> Pages)
    {
        public string Object(int number) => Objects[number];

        public int Ref(string dictionary, string key) =>
            int.Parse(ReferenceRegex().Match(Value(dictionary, key)).Groups[1].Value, CultureInfo.InvariantCulture);

        /// <summary>The object numbers of a page's annotations, in order.</summary>
        public List<int> Annotations(int page)
        {
            var dictionary = Object(Pages[page]);
            return dictionary.Contains("/Annots ", StringComparison.Ordinal)
                ? [.. ReferenceRegex().Matches(Value(dictionary, "Annots")).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))]
                : [];
        }
    }
}
