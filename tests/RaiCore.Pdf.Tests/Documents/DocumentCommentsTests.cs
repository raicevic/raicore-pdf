using System.Globalization;
using System.Text.Json;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Documents.Rendering;
using RaiCore.Pdf.Documents.Text;
using RaiCore.Pdf.Extraction;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>
/// Comments (<see cref="DocumentConverterOptions.Comments"/>) in DOCX, DOC, RTF and ODT: what the readers find against
/// independent readers (python-docx, odfpy, striprtf and a walk of the .doc structures with olefile, in
/// Fixtures/Documents/Comments/expected.json), the list at the end, the PDF notes at the text they are about, and the
/// diagnostics.
/// </summary>
public class DocumentCommentsTests
{
    private static string ExpectedPath => Fixtures.Path(Path.Combine("Documents", "Comments", "expected.json"));

    /// <summary>A comment as the oracle finds it.</summary>
    private sealed record Expected(string? Author, string? Initials, string? Date, string Text, string? About, int? Parent);

    private static List<Expected> ExpectedOf(string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(ExpectedPath));
        return [.. json.RootElement.GetProperty(name).EnumerateArray().Select(c => new Expected(
            c.GetProperty("author").GetString(), c.GetProperty("initials").GetString(), c.GetProperty("date").GetString(),
            c.GetProperty("text").GetString()!, c.GetProperty("about").GetString(),
            c.GetProperty("parent").ValueKind == JsonValueKind.Null ? null : c.GetProperty("parent").GetInt32()))];
    }

    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        using var json = JsonDocument.Parse(File.ReadAllText(ExpectedPath));
        foreach (var property in json.RootElement.EnumerateObject())
        {
            data.Add(property.Name);
        }

        return data;
    }

    /// <summary>Converts a document of any of the four formats (by its file name) with the comments as asked.</summary>
    internal static PdfDocument Convert(string name, byte[] data, DocumentComments comments, List<DocumentDiagnostic>? diagnostics = null, bool tagged = false)
    {
        Action<DocumentDiagnostic>? report = diagnostics is null ? null : diagnostics.Add;
        return Path.GetExtension(name) switch
        {
            ".rtf" => RtfToPdf.Convert(data, new RtfConverterOptions { Comments = comments, Diagnostics = report, Tagged = tagged }),
            ".odt" or ".fodt" => OdtToPdf.Convert(data, new OdtConverterOptions { Comments = comments, Diagnostics = report, Tagged = tagged }),
            ".doc" => DocToPdf.Convert(data, new DocConverterOptions { Comments = comments, Diagnostics = report, Tagged = tagged }),
            _ => DocxToPdf.Convert(data, new DocxConverterOptions { Comments = comments, Diagnostics = report, Tagged = tagged }),
        };
    }

    private static WordDocument ReadWithComments(byte[] data)
    {
        var context = new ConversionContext(ResourceLimits.Default, null, CancellationToken.None) { Extras = ReadExtras.Comments };
        return DocumentText.Read(data, context);
    }

    /// <summary>A comment's text without list labels (which the .doc oracle, reading the stored text, does not see).</summary>
    private static string TextOf(WordComment comment) =>
        PlainTextWriter.TextOf(comment.Blocks, new ConversionContext(ResourceLimits.Default, null, CancellationToken.None), new DocumentTextOptions { ListLabels = false });

    /// <summary>The comments the oracle lists at the top level (not replies), in the order of their first references.</summary>
    private static List<(Expected Comment, List<Expected> Replies)> TopLevel(List<Expected> expected)
    {
        var result = new List<(Expected, List<Expected>)>();
        foreach (var comment in expected)
        {
            var root = comment.Parent is { } parent ? expected[parent] : comment;
            if (!result.Any(r => ReferenceEquals(r.Item1, root)))
            {
                result.Add((root, [.. expected.Where(e => e.Parent is { } p && ReferenceEquals(expected[p], root))]));
            }
        }

        return result;
    }

    private static string Label(Expected comment, int number) => "[" + comment.Initials + number.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>The readers find each comment's author, initials, date, text and the comment it answers as the oracles do.</summary>
    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Comments_match_the_oracles(string name)
    {
        var expected = ExpectedOf(name);
        var document = ReadWithComments(Fixtures.Read(name));
        Assert.Equal(expected.Count, document.Comments.Count);
        foreach (var comment in expected)
        {
            var ours = Assert.Single(document.Comments.Values, c => TextOf(c) == comment.Text);
            Assert.Equal(comment.Author, ours.Author);
            Assert.Equal(comment.Initials, ours.Initials);
            Assert.Equal(comment.Date, ours.Date?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            var parent = comment.Parent is { } index ? expected[index].Text : null;
            Assert.Equal(parent, ours.ParentId is { } id ? TextOf(document.Comments[id]) : null);
        }
    }

    /// <summary>
    /// At the end: a "Comments" page lists each comment in the order of the references with its label, author, date,
    /// the text it is about and its text, replies after it; the body marks each anchor with the label.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void At_end_lists_the_comments_as_the_oracles_find_them(string name)
    {
        var expected = TopLevel(ExpectedOf(name));
        var bytes = Convert(name, Fixtures.Read(name), DocumentComments.AtEnd).ToArray();
        var pages = Docx.Pages(bytes);
        var start = Array.FindLastIndex(pages, p => p.TrimStart().StartsWith("Comments", StringComparison.Ordinal));
        Assert.True(start > 0, "No page of comments.");
        var body = string.Join("\n", pages[..start]);
        var list = string.Join("\n", pages[start..]);
        var at = 0;
        void Next(string text)
        {
            var found = list.IndexOf(text, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"{name}: '{text}' is missing after position {at} of:\n{list}");
            at = found + text.Length;
        }

        for (var i = 0; i < expected.Count; i++)
        {
            var (comment, replies) = expected[i];
            var label = Label(comment, i + 1);
            Assert.Contains(label, body, StringComparison.Ordinal);
            Next(label);
            Next(comment.Author ?? string.Empty);
            Next(comment.Date ?? string.Empty);
            if (comment.About is { } about)
            {
                Next("On \u201C" + DocumentRenderer.Shortened(about) + "\u201D");
            }

            foreach (var line in comment.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Next(line.Trim());
            }

            foreach (var reply in replies)
            {
                Next("Reply");
                Next(reply.Author ?? string.Empty);
                Next(reply.Text.Split('\n')[0].Trim());
            }
        }

        Assert.DoesNotContain("[" + (expected.Count + 1).ToString(CultureInfo.InvariantCulture) + "]", list, StringComparison.Ordinal);
        AssertValid(bytes);
    }

    /// <summary>
    /// As notes: one per comment (replies in its text), by its author, its icon's top left corner where the text it is
    /// about starts (found in the page's text), and no destinations left over from placing them.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Annotations_put_a_note_at_the_text_each_comment_is_about(string name)
    {
        var expected = TopLevel(ExpectedOf(name));
        var pdf = Convert(name, Fixtures.Read(name), DocumentComments.Annotations);
        var notes = pdf.Pages.SelectMany((p, i) => p.Notes.Select(n => (Page: i, Note: n))).ToList();
        Assert.Equal(expected.Count, notes.Count);
        Assert.DoesNotContain(pdf.NamedDestinations.Keys, k => k.StartsWith("_comment", StringComparison.Ordinal));
        var bytes = pdf.ToArray();
        var extractor = new TextExtractor(PdfReader.Open(bytes));
        foreach (var (comment, replies) in expected)
        {
            var (page, note) = Assert.Single(notes, n => n.Note.Text.StartsWith(comment.Text.Split('\n')[0], StringComparison.Ordinal));
            Assert.Equal(comment.Author, note.Author);
            Assert.All(comment.Text.Split('\n'), line => Assert.Contains(line, note.Text, StringComparison.Ordinal));
            foreach (var reply in replies)
            {
                Assert.Contains("Reply from " + reply.Author + ":\n" + reply.Text, note.Text, StringComparison.Ordinal);
            }

            if (comment.About is { Length: > 0 } about)
            {
                // The first word of the text the comment is about, on the note's line.
                var word = about.Split(' ')[0];
                var matches = extractor.FindOnPage(page, word);
                Assert.Contains(matches, m => Math.Abs(m.Bounds.X - note.X) < 1.5 && note.Y + note.Height >= m.Bounds.Y && note.Y + note.Height <= m.Bounds.Top + 6);
            }
        }

        AssertValid(bytes);
    }

    /// <summary>Without comments (the default), the output is as before: no notes, no list, and the diagnostics say comments are left out.</summary>
    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void None_leaves_comments_out_and_reports_them(string name)
    {
        var data = Fixtures.Read(name);
        var diagnostics = new List<DocumentDiagnostic>();
        var pdf = Convert(name, data, DocumentComments.None, diagnostics);
        Assert.All(pdf.Pages, p => Assert.Empty(p.Notes));
        Assert.DoesNotContain(pdf.NamedDestinations.Keys, k => k.StartsWith("_cm", StringComparison.Ordinal));
        var text = Docx.AllText(pdf.ToArray());
        Assert.All(ExpectedOf(name), c => Assert.DoesNotContain(c.Text.Split('\n')[0], text, StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Kind == DocumentDiagnosticKind.UnsupportedContent && d.Message.Contains("Comments (annotations) are left out", StringComparison.Ordinal));

        var shown = new List<DocumentDiagnostic>();
        Convert(name, data, DocumentComments.AtEnd, shown);
        Assert.DoesNotContain(shown, d => d.Message.Contains("Comments", StringComparison.Ordinal));
    }

    /// <summary>The text comments are about is highlighted (in both modes); without comments nothing is.</summary>
    [Fact]
    public void Commented_text_is_highlighted()
    {
        const string name = "Documents/Comments/comments.docx";
        var data = Fixtures.Read(name);
        static string Content(PdfDocument pdf)
        {
            pdf.CompressContent = false;
            return System.Text.Encoding.Latin1.GetString(pdf.ToArray());
        }

        const string highlight = "1 0.94902 0.67059 rg";
        Assert.DoesNotContain(highlight, Content(Convert(name, data, DocumentComments.None)), StringComparison.Ordinal);
        Assert.Contains(highlight, Content(Convert(name, data, DocumentComments.AtEnd)), StringComparison.Ordinal);
        Assert.Contains(highlight, Content(Convert(name, data, DocumentComments.Annotations)), StringComparison.Ordinal);
    }

    /// <summary>The marks link to their comments, and the list's heading is a bookmark.</summary>
    [Fact]
    public void Marks_link_to_the_list_and_its_heading_is_a_bookmark()
    {
        var pdf = Convert("comments.odt", Fixtures.Read("Documents/Comments/comments.odt"), DocumentComments.AtEnd);
        Assert.Equal(["_cm1", "_cm2", "_cm3", "_cm4"], pdf.NamedDestinations.Keys.Where(k => k.StartsWith("_cm", StringComparison.Ordinal)).Order());
        Assert.All(pdf.NamedDestinations.Where(d => d.Key.StartsWith("_cm", StringComparison.Ordinal)), d => Assert.Same(pdf.Pages[^1], d.Value.Page));
        Assert.Equal(4, pdf.Pages[0].Links.Count(l => l.DestinationName?.StartsWith("_cm", StringComparison.Ordinal) == true));
        Assert.Equal("Comments", Assert.Single(pdf.Outline).Title);
    }

    /// <summary>In a tagged document each note is an Annot structure element, and the list is tagged text.</summary>
    [Fact]
    public void Tagged_notes_are_annotation_elements()
    {
        var tagged = Convert("comments.rtf", Fixtures.Read("Documents/Comments/comments.rtf"), DocumentComments.Annotations, tagged: true).ToArray();
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(tagged), "/S /Annot").Count);
        AssertValid(tagged);
        AssertValid(Convert("comments.rtf", Fixtures.Read("Documents/Comments/comments.rtf"), DocumentComments.AtEnd, tagged: true).ToArray());
    }

    /// <summary>Replies that answer each other, or themselves, are listed as comments of their own (no endless loop).</summary>
    [Fact]
    public void Circular_replies_are_comments_of_their_own()
    {
        const string ns = "http://schemas.microsoft.com/office/word/2012/wordml";
        const string w14 = "http://schemas.microsoft.com/office/word/2010/wordml";
        var comments = Docx.Part("comments",
            $"<w:comment w:id=\"1\" w:author=\"A\"><w:p xmlns:w14=\"{w14}\" w14:paraId=\"1\"><w:r><w:t>One</w:t></w:r></w:p></w:comment>"
            + $"<w:comment w:id=\"2\" w:author=\"B\"><w:p xmlns:w14=\"{w14}\" w14:paraId=\"2\"><w:r><w:t>Two</w:t></w:r></w:p></w:comment>"
            + $"<w:comment w:id=\"3\" w:author=\"C\"><w:p xmlns:w14=\"{w14}\" w14:paraId=\"3\"><w:r><w:t>Three</w:t></w:r></w:p></w:comment>");
        var extended = $"<w15:commentsEx xmlns:w15=\"{ns}\"><w15:commentEx w15:paraId=\"1\" w15:paraIdParent=\"2\"/>"
            + "<w15:commentEx w15:paraId=\"2\" w15:paraIdParent=\"1\"/><w15:commentEx w15:paraId=\"3\" w15:paraIdParent=\"3\"/></w15:commentsEx>";
        var data = Docx.Document(
            Docx.P("x") + "<w:p><w:r><w:t>Body</w:t></w:r><w:r><w:commentReference w:id=\"1\"/></w:r><w:r><w:commentReference w:id=\"2\"/></w:r><w:r><w:commentReference w:id=\"3\"/></w:r></w:p>",
            parts: [("word/comments.xml", comments), ("word/commentsExtended.xml", extended)],
            relationships: [("rC", "comments", "comments.xml", false), ("rE", "commentsExtended", "commentsExtended.xml", false)]);
        var text = Docx.AllText(DocxToPdf.ConvertToBytes(data, new DocxConverterOptions { Comments = DocumentComments.AtEnd }));
        Assert.Contains("Body[1][2][3]", text, StringComparison.Ordinal);
        Assert.Equal(3, DocxToPdf.Convert(data, new DocxConverterOptions { Comments = DocumentComments.Annotations }).Pages[0].Notes.Count);
    }

    /// <summary>A range that never ends highlights to the end of the body only; headers and footers show no comments.</summary>
    [Fact]
    public void Open_ranges_and_comments_in_headers_stay_in_the_body()
    {
        var comments = Docx.Part("comments", "<w:comment w:id=\"1\" w:author=\"A\"><w:p><w:r><w:t>Note</w:t></w:r></w:p></w:comment>"
            + "<w:comment w:id=\"2\" w:author=\"B\"><w:p><w:r><w:t>In header</w:t></w:r></w:p></w:comment>");
        var header = Docx.Part("hdr", "<w:p><w:commentRangeStart w:id=\"2\"/><w:r><w:t>Head</w:t></w:r><w:r><w:commentReference w:id=\"2\"/></w:r></w:p>");
        var data = Docx.Document(
            "<w:p><w:commentRangeStart w:id=\"1\"/><w:r><w:t>Open</w:t></w:r><w:r><w:commentReference w:id=\"1\"/></w:r></w:p>"
            + "<w:p><w:r><w:t>More</w:t></w:r></w:p><w:sectPr><w:headerReference w:type=\"default\" r:id=\"rH\"/></w:sectPr>",
            parts: [("word/comments.xml", comments), ("word/header1.xml", header)],
            relationships: [("rC", "comments", "comments.xml", false), ("rH", "header", "header1.xml", false)]);
        var pdf = DocxToPdf.Convert(data, new DocxConverterOptions { Comments = DocumentComments.Annotations });
        Assert.Equal("Note", Assert.Single(pdf.Pages[0].Notes).Text);
        var text = Docx.AllText(DocxToPdf.ConvertToBytes(data, new DocxConverterOptions { Comments = DocumentComments.AtEnd }));
        Assert.Contains("Open[1]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("In header", text, StringComparison.Ordinal);
        Assert.Contains("Head", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Comment tables of a .doc file pointing anywhere, with any size (in the FIB of Tika's testComment.doc, at 512), and
    /// damaged bytes, convert in both modes or throw the documented exceptions only.
    /// </summary>
    [Fact]
    public void Damaged_doc_comment_tables_convert_or_throw_format_errors()
    {
        var original = Fixtures.Read("Tika/testComment.doc");
        Assert.Equal(0xA5EC, BitConverter.ToUInt16(original, 512));
        var cases = new List<byte[]>();
        foreach (var index in new[] { 4, 5, 36, 37, 42, 43, 112 })
        {
            foreach (var (fc, lcb) in new (uint, uint)[] { (0, 1), (0, 5), (0, 33), (0, 35), (0, 69), (0, 4096), (100, 0xFFFF), (0xFFFF_FF00, 64), (8, 0xFFFF_FFFF) })
            {
                var data = (byte[])original.Clone();
                BitConverter.TryWriteBytes(data.AsSpan(512 + 154 + (8 * index)), fc);
                BitConverter.TryWriteBytes(data.AsSpan(512 + 158 + (8 * index)), lcb);
                cases.Add(data);
            }
        }

        var random = new Random(292);
        for (var round = 0; round < 60; round++)
        {
            var data = (byte[])original.Clone();
            for (var i = 0; i < 1 + (round % 6); i++)
            {
                data[512 + random.Next(data.Length - 512)] = (byte)random.Next(256);
            }

            cases.Add(data);
        }

        var limits = ResourceLimits.Default with { MaxPages = 100, Timeout = TimeSpan.FromSeconds(20) };
        foreach (var (data, i) in cases.Select((d, i) => (d, i)))
        {
            foreach (var mode in new[] { DocumentComments.AtEnd, DocumentComments.Annotations })
            {
                var exception = Record.Exception(() => DocToPdf.ConvertToBytes(data, new DocConverterOptions { Comments = mode, Limits = limits }));
                Assert.True(exception is null or DocumentFormatException or NotSupportedException or ResourceLimitExceededException, $"case {i} {mode}: {exception}");
            }
        }
    }

    /// <summary>
    /// RTF comment details in odd places: a range no comment names, a tag named twice, an unknown parent, a date that is
    /// not a number, a comment in a footnote.
    /// </summary>
    [Fact]
    public void Odd_rtf_comments_are_read_as_far_as_they_make_sense()
    {
        var rtf = @"{\rtf1\ansi{\*\atrfstart 7}lonely{\*\atrfend 7} {\*\atrfstart 8}named{\*\atrfend 8}"
            + @"{\*\atnid A}{\*\atnauthor Ann}\chatn{\*\annotation{\*\atnref 8}{\*\atnref 8}{\*\atndate 0}{\*\atnparent 99}\pard One}"
            + @"{\*\atnid B}\chatn{\*\annotation{\*\atnref 8}{\*\atnparent 8}{\*\atndate x}\pard Two}"
            + @"{\footnote\pard In a note {\*\atrfstart 9}x{\*\atrfend 9}\chatn{\*\annotation{\*\atnref 9}\pard Three}}\par}";
        var pdf = RtfToPdf.Convert(System.Text.Encoding.ASCII.GetBytes(rtf), new RtfConverterOptions { Comments = DocumentComments.AtEnd });
        var text = Docx.AllText(pdf.ToArray());
        Assert.Contains("named[A1]", text, StringComparison.Ordinal);
        Assert.Contains("On \u201Cnamed\u201D", text, StringComparison.Ordinal);
        Assert.Contains("Reply", text, StringComparison.Ordinal);
        Assert.Contains("In a note x[2]", text, StringComparison.Ordinal); // Comments in notes are numbered too.
        Assert.Contains("Three", text, StringComparison.Ordinal);
        Assert.DoesNotContain("On \u201Clonely", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-01-02T09:30:00Z", "2026-01-02 09:30")]
    [InlineData("2026-01-02T09:30:00+05:00", "2026-01-02 09:30")]
    [InlineData("2026-01-02T09:30:00.123456789", "2026-01-02 09:30")]
    [InlineData("2026-01-02", "2026-01-02 00:00")]
    [InlineData("not a date", null)]
    [InlineData("", null)]
    public void Dates_are_read_as_their_clock_shows(string value, string? expected) =>
        Assert.Equal(expected, RaiCore.Pdf.Documents.Docx.DocxReader.ParseDate(value)?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(-956724704, "2011-09-17 08:32")] // Tika's testComment.rtf.
    [InlineData(0, null)]
    [InlineData(0x0000_003C, null)] // 60 minutes.
    [InlineData(0x07D0_0800, null)] // Month 0.
    [InlineData(0x07D2_F800, null)] // February 31.
    public void Dttm_values_are_dates_or_none(long value, string? expected) =>
        Assert.Equal(expected, RaiCore.Pdf.Documents.Rtf.RtfReader.DateTimeOfDttm(unchecked((uint)value))?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("short  text\n here", "short text here")]
    [InlineData("This sentence is the long text a comment is about, longer than the eighty characters that the list quotes",
        "This sentence is the long text a comment is about, longer than the eighty\u2026")]
    [InlineData("Averyveryveryveryveryveryveryveryveryveryveryveryveryveryveryveryveryveryveryverylongword and more",
        "Averyveryveryveryveryveryveryveryveryveryveryveryveryveryveryveryveryveryveryver\u2026")]
    public void Quoted_text_is_one_shortened_line(string text, string expected) => Assert.Equal(expected, DocumentRenderer.Shortened(text));

    private static void AssertValid(byte[] pdf)
    {
        if (ExternalTools.Find("qpdf") is not null)
        {
            ExternalTools.AssertQpdfClean(pdf);
        }
    }
}
