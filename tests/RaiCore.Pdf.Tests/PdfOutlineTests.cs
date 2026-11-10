using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Tests;

/// <summary>The document outline (bookmarks), checked in the uncompressed file syntax.</summary>
public partial class PdfOutlineTests
{
    /// <summary>
    /// Three pages and the outline:
    /// Chapter 1 (open) > [1.1, 1.2 (closed) > [1.2.1, 1.2.2]]; Unicode chapter (no children).
    /// </summary>
    private static PdfDocument CreateSample()
    {
        var doc = new PdfDocument { CompressContent = false };
        doc.Info.CreationDate = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var pages = Enumerable.Range(0, 3).Select(_ => doc.AddPage(PdfPageSize.A4)).ToList();

        var chapter = new PdfOutlineItem("Chapter 1", pages[0], 800);
        chapter.Children.Add(new PdfOutlineItem("Section 1.1", pages[0], 500.5));
        var closed = new PdfOutlineItem("Section 1.2 (closed)", pages[1], 700) { IsOpen = false };
        closed.Children.Add(new PdfOutlineItem("1.2.1", pages[1], 600));
        closed.Children.Add(new PdfOutlineItem("1.2.2", pages[2], 300) { Left = 72 });
        chapter.Children.Add(closed);
        doc.Outline.Add(chapter);
        doc.Outline.Add(new PdfOutlineItem("\u00C6bler \u2013 \u65E5\u672C", pages[2]));
        return doc;
    }

    [Fact]
    public void Catalog_points_at_the_outline_and_opens_the_panel()
    {
        var pdf = Parse(CreateSample());

        var root = pdf.Object(pdf.Ref(pdf.Catalog, "Outlines"));
        Assert.Equal("/Outlines", Value(root, "Type"));
        Assert.Equal("/UseOutlines", Value(pdf.Catalog, "PageMode"));
    }

    [Fact]
    public void Items_are_linked_as_a_tree()
    {
        var pdf = Parse(CreateSample());
        var rootNumber = pdf.Ref(pdf.Catalog, "Outlines");
        var root = pdf.Object(rootNumber);

        var top = pdf.Children(root);
        Assert.Equal(["Chapter 1", "\u00C6bler \u2013 \u65E5\u672C"], top.Select(n => pdf.Title(n)));
        Assert.All(top, n => Assert.Equal(rootNumber, pdf.Ref(pdf.Object(n), "Parent")));

        var sections = pdf.Children(pdf.Object(top[0]));
        Assert.Equal(["Section 1.1", "Section 1.2 (closed)"], sections.Select(n => pdf.Title(n)));
        Assert.All(sections, n => Assert.Equal(top[0], pdf.Ref(pdf.Object(n), "Parent")));

        var subsections = pdf.Children(pdf.Object(sections[1]));
        Assert.Equal(["1.2.1", "1.2.2"], subsections.Select(n => pdf.Title(n)));

        // Prev links mirror the Next links; leaves have no First/Last/Count.
        Assert.Equal(sections[0], pdf.Ref(pdf.Object(sections[1]), "Prev"));
        Assert.DoesNotContain("/Prev", pdf.Object(sections[0]), StringComparison.Ordinal);
        Assert.DoesNotContain("/Next", pdf.Object(sections[1]), StringComparison.Ordinal);
        Assert.DoesNotContain("/First", pdf.Object(subsections[0]), StringComparison.Ordinal);
        Assert.DoesNotContain("/Count", pdf.Object(subsections[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_are_visible_descendants_and_negative_for_closed_items()
    {
        var pdf = Parse(CreateSample());
        var root = pdf.Object(pdf.Ref(pdf.Catalog, "Outlines"));
        var top = pdf.Children(root);
        var sections = pdf.Children(pdf.Object(top[0]));

        // Visible when opened: Chapter 1, its two sections (1.2 is closed, so not its children), the Unicode chapter.
        Assert.Equal("4", Value(root, "Count"));
        Assert.Equal("2", Value(pdf.Object(top[0]), "Count"));
        Assert.Equal("-2", Value(pdf.Object(sections[1]), "Count"));
        Assert.DoesNotContain("/Count", pdf.Object(top[1]), StringComparison.Ordinal);
    }

    [Fact]
    public void Destinations_point_at_the_page_objects_and_positions()
    {
        var pdf = Parse(CreateSample());
        var top = pdf.Children(pdf.Object(pdf.Ref(pdf.Catalog, "Outlines")));
        var sections = pdf.Children(pdf.Object(top[0]));
        var subsections = pdf.Children(pdf.Object(sections[1]));

        Assert.Equal($"[{pdf.Pages[0]} 0 R /XYZ null 800 null]", Value(pdf.Object(top[0]), "Dest"));
        Assert.Equal($"[{pdf.Pages[0]} 0 R /XYZ null 500.5 null]", Value(pdf.Object(sections[0]), "Dest"));
        Assert.Equal($"[{pdf.Pages[1]} 0 R /XYZ null 700 null]", Value(pdf.Object(sections[1]), "Dest"));
        Assert.Equal($"[{pdf.Pages[2]} 0 R /XYZ 72 300 null]", Value(pdf.Object(subsections[1]), "Dest"));
        Assert.Equal($"[{pdf.Pages[2]} 0 R /XYZ null null null]", Value(pdf.Object(top[1]), "Dest"));
    }

    [Fact]
    public void Titles_are_text_strings()
    {
        var pdf = Parse(CreateSample());
        var top = pdf.Children(pdf.Object(pdf.Ref(pdf.Catalog, "Outlines")));
        var sections = pdf.Children(pdf.Object(top[0]));

        // ASCII as is (parentheses escaped); anything else UTF-16BE with a byte order mark.
        Assert.Equal(@"(Section 1.2 \(closed\))", Value(pdf.Object(sections[1]), "Title"));
        var unicode = LiteralBytes(Value(pdf.Object(top[1]), "Title"));
        Assert.Equal([0xFE, 0xFF], unicode[..2]);
        Assert.Equal("\u00C6bler \u2013 \u65E5\u672C", Encoding.BigEndianUnicode.GetString(unicode[2..]));
    }

    [Fact]
    public void Without_an_outline_neither_outlines_nor_page_mode_are_written()
    {
        var doc = new PdfDocument { CompressContent = false };
        doc.AddPage();
        var text = Encoding.Latin1.GetString(doc.ToArray());

        Assert.DoesNotContain("/Outlines", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/PageMode", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_mode_can_be_set_explicitly()
    {
        var withOutline = CreateSample();
        withOutline.PageMode = PdfPageMode.UseNone;
        Assert.DoesNotContain("/PageMode", Parse(withOutline).Catalog, StringComparison.Ordinal);

        var withoutOutline = new PdfDocument { CompressContent = false, PageMode = PdfPageMode.UseOutlines };
        withoutOutline.AddPage();
        Assert.Contains("/PageMode /UseOutlines", Encoding.Latin1.GetString(withoutOutline.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Items_pointing_outside_the_document_or_repeated_are_rejected()
    {
        var other = new PdfDocument().AddPage();
        var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.Outline.Add(new PdfOutlineItem("Elsewhere", other));
        Assert.Throws<InvalidOperationException>(doc.ToArray);

        doc.Outline.Clear();
        var item = new PdfOutlineItem("Loop", page);
        item.Children.Add(item);
        doc.Outline.Add(item);
        Assert.Throws<InvalidOperationException>(doc.ToArray);
    }

    [Fact]
    public void Passes_qpdf_structural_check()
    {
        var doc = CreateSample();
        doc.CompressContent = true;
        ExternalTools.AssertQpdfClean(doc.ToArray());
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

    /// <summary>The raw value of a dictionary entry: a literal string, an array, a name, a reference or a number.</summary>
    private static string Value(string dictionary, string key)
    {
        var match = Regex.Match(dictionary, $@"/{key}[ \[(]");
        Assert.True(match.Success, $"/{key} not found in {dictionary}");
        var start = match.Index + key.Length + 1;
        while (dictionary[start] == ' ')
        {
            start++;
        }

        var end = start;
        switch (dictionary[start])
        {
            case '(':
                for (var depth = 0; ; end++)
                {
                    if (dictionary[end] == '\\')
                    {
                        end++;
                    }
                    else if (dictionary[end] == '(')
                    {
                        depth++;
                    }
                    else if (dictionary[end] == ')' && --depth == 0)
                    {
                        break;
                    }
                }

                return dictionary[start..(end + 1)];
            case '[':
                return dictionary[start..(dictionary.IndexOf(']', start) + 1)];
            case '/':
                end = start + 1;
                while (char.IsLetterOrDigit(dictionary[end]))
                {
                    end++;
                }

                return dictionary[start..end];
            default:
                while (dictionary[end] is not ('/' or '>'))
                {
                    end++;
                }

                return dictionary[start..end].Trim();
        }
    }

    /// <summary>Decodes a literal string such as <c>(\376\377...)</c> to its bytes.</summary>
    private static byte[] LiteralBytes(string literal)
    {
        var bytes = new List<byte>();
        for (var i = 1; i < literal.Length - 1; i++)
        {
            if (literal[i] != '\\')
            {
                bytes.Add((byte)literal[i]);
            }
            else if (char.IsAsciiDigit(literal[i + 1]))
            {
                bytes.Add(Convert.ToByte(literal.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                bytes.Add((byte)literal[++i]);
            }
        }

        return [.. bytes];
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

        public string Title(int number)
        {
            var bytes = LiteralBytes(Value(Object(number), "Title"));
            return bytes is [0xFE, 0xFF, ..] ? Encoding.BigEndianUnicode.GetString(bytes[2..]) : Encoding.ASCII.GetString(bytes);
        }

        /// <summary>An outline node's children, following First and Next.</summary>
        public List<int> Children(string node)
        {
            var children = new List<int>();
            var next = Ref(node, "First");
            while (true)
            {
                children.Add(next);
                if (!Object(next).Contains("/Next ", StringComparison.Ordinal))
                {
                    break;
                }

                next = Ref(Object(next), "Next");
            }

            Assert.Equal(children[^1], Ref(node, "Last"));
            return children;
        }
    }
}
