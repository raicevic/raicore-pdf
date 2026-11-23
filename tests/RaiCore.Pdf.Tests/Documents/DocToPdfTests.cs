using System.Text.Json;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Extraction;
using static RaiCore.Pdf.Tests.WordDocuments.Doc;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>
/// Converting Word 97-2003 documents to PDF: Apache POI's and Tika's samples against the text, labels, links and
/// errors their own unit tests expect (Fixtures/Documents/Doc/make-expected.py), and the summary information olefile
/// reads; formatting, lists, tables, sections, headers and footers, notes, fields and pictures in the model; options,
/// limits, and damaged and hostile input.
/// </summary>
public class DocToPdfTests
{
    private static string ExpectedPath => Fixtures.Path(Path.Combine("Documents", "Doc", "expected.json"));

    public static TheoryData<string> SampleNames()
    {
        var data = new TheoryData<string>();
        using var expected = JsonDocument.Parse(File.ReadAllText(ExpectedPath));
        foreach (var property in expected.RootElement.EnumerateObject())
        {
            data.Add(property.Name);
        }

        return data;
    }

    private static void AssertValid(byte[] pdf)
    {
        if (ExternalTools.Find("qpdf") is not null)
        {
            ExternalTools.AssertQpdfClean(pdf);
        }
    }

    /// <summary>
    /// Every sample converts or throws the error its tests expect; the PDF has the expected text (paragraphs in order,
    /// other text anywhere, list labels before their text), not the text that is left out, the expected links, and
    /// the title and author olefile reads.
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Samples_have_the_text_their_tests_expect(string name)
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(ExpectedPath));
        var entry = expected.RootElement.GetProperty(name);
        var data = Sample(name);
        if (entry.GetProperty("error").GetString() is { } error)
        {
            var exception = Record.Exception(() => DocToPdf.Convert(data));
            Assert.IsType(error == "NotSupported" ? typeof(NotSupportedException) : typeof(PdfFormatException), exception);
            return;
        }

        var (pdf, _) = Convert(data);
        var bytes = pdf.ToArray();
        var text = Docx.AllText(bytes);
        var words = Words(text);
        var position = 0;
        foreach (var item in entry.GetProperty("text").EnumerateArray())
        {
            position = FindWords(name, words, item.GetString()!, position);
        }

        foreach (var item in entry.GetProperty("anywhere").EnumerateArray())
        {
            FindWords(name, words, item.GetString()!, 0);
        }

        // Labels with their paragraph's text, from the model (the PDF's text may put a wide label apart from its text).
        var document = Read(data);
        var labelled = Compact(string.Join("\n", AllParagraphs(document.Sections.SelectMany(s => s.Blocks)).Select(p => p.Label?.Text + " " + Text(p))));
        foreach (var item in entry.GetProperty("labels").EnumerateArray())
        {
            Assert.True(labelled.Contains(Compact(item.GetString()!), StringComparison.Ordinal), $"{name}: the label and text '{item.GetString()}' are missing.");
        }

        foreach (var item in entry.GetProperty("absent").EnumerateArray())
        {
            Assert.DoesNotContain(item.GetString()!, text, StringComparison.Ordinal);
        }

        var links = Links(pdf);
        foreach (var item in entry.GetProperty("links").EnumerateArray())
        {
            Assert.Contains(item.GetString()!.TrimEnd('/'), links);
        }

        Assert.Equal(entry.GetProperty("title").GetString(), document.Title);
        Assert.Equal(entry.GetProperty("author").GetString(), document.Author);
        AssertValid(bytes);
    }

    [Fact]
    public void Encrypted_and_older_documents_are_not_supported()
    {
        var encrypted = Assert.Throws<NotSupportedException>(() => DocToPdf.Convert(Sample("testWORD_protected_passtika.doc")));
        Assert.Contains("password-protected", encrypted.Message, StringComparison.Ordinal);
        var word6 = Assert.Throws<NotSupportedException>(() => DocToPdf.Convert(Sample("testWORD6.doc")));
        Assert.Contains("Word 6 or Word 95", word6.Message, StringComparison.Ordinal);

        // Word for Windows 2 files are not compound files: they start with their own FIB.
        byte[] word2 = [0xDB, 0xA5, 0x2D, 0x00, .. new byte[600]];
        Assert.Contains("Word for Windows 1 or 2", Assert.Throws<NotSupportedException>(() => DocToPdf.Convert(word2)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_files_are_format_errors_that_name_the_right_converter()
    {
        Assert.Contains("DocxToPdf", Assert.Throws<PdfFormatException>(() => DocToPdf.Convert(Fixtures.Read(Path.Combine("Documents", "minimal.docx")))).Message, StringComparison.Ordinal);
        Assert.Contains("RtfToPdf", Assert.Throws<PdfFormatException>(() => DocToPdf.Convert(Fixtures.Read(Path.Combine("Documents", "Rtf", "minimal.rtf")))).Message, StringComparison.Ordinal);
        Assert.Throws<PdfFormatException>(() => DocToPdf.Convert("not a document"u8.ToArray()));
        Assert.Throws<PdfFormatException>(() => DocToPdf.Convert([]));

        // A compound file without a WordDocument stream (an Outlook message).
        Assert.Contains("WordDocument", Assert.Throws<PdfFormatException>(() => DocToPdf.Convert(Fixtures.Read(Path.Combine("Poi", "quick.msg")))).Message, StringComparison.Ordinal);

        // DocxToPdf and OdtToPdf point at DocToPdf for a .doc file.
        var docx = Assert.Throws<NotSupportedException>(() => DocxToPdf.Convert(Sample("test2.doc")));
        Assert.Contains("DocToPdf", docx.Message, StringComparison.Ordinal);
        Assert.Contains("encrypted", Assert.Throws<NotSupportedException>(() => DocxToPdf.Convert(Fixtures.Read(Path.Combine("Poi", "bug53475-password-is-pass.docx")))).Message, StringComparison.Ordinal);
        Assert.Contains("DocToPdf", Assert.Throws<PdfFormatException>(() => OdtToPdf.Convert(Sample("test2.doc"))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncated_documents_throw_format_errors_or_convert()
    {
        var data = Sample("testWORD_various.doc");
        foreach (var length in new[] { 8, 512, 1024, 2048, 4096, data.Length / 2, data.Length - 512, data.Length - 1 })
        {
            var exception = Record.Exception(() => DocToPdf.ConvertToBytes(data[..length]));
            Assert.True(exception is null or PdfFormatException, $"{length}: {exception}");
        }
    }

    /// <summary>Damaged copies of the samples (bytes changed at random) convert or throw the expected exceptions only.</summary>
    [Theory]
    [InlineData("testWORD_various.doc")]
    [InlineData("testWORD.doc")]
    [InlineData("rasp.doc")]
    [InlineData("testWORD_3imgs.doc")]
    [InlineData("testWORD_override_list_numbering.doc")]
    public void Damaged_documents_throw_only_the_expected_exceptions(string name)
    {
        var original = Sample(name);
        var random = new Random(name.Length * 7919);
        var limits = ResourceLimits.Default with { MaxPages = 200, Timeout = TimeSpan.FromSeconds(20) };
        for (var round = 0; round < 40; round++)
        {
            var data = (byte[])original.Clone();
            for (var i = 0; i < 1 + (round % 8); i++)
            {
                // Skip the compound file header most of the time: the streams are what the reader parses.
                var position = round % 5 == 0 ? random.Next(data.Length) : 512 + random.Next(data.Length - 512);
                data[position] = (byte)random.Next(256);
            }

            var exception = Record.Exception(() => DocToPdf.ConvertToBytes(data, new DocConverterOptions { Limits = limits }));
            Assert.True(exception is null or PdfFormatException or NotSupportedException or ResourceLimitExceededException, $"{name} round {round}: {exception}");
        }
    }

    [Fact]
    public void Limits_bound_the_document()
    {
        var data = Sample("testWORD.doc");
        Assert.Throws<ResourceLimitExceededException>(() => DocToPdf.Convert(data, new DocConverterOptions { Limits = ResourceLimits.Default with { MaxElements = 40 } }));
        var nesting = Assert.Throws<ResourceLimitExceededException>(() => DocToPdf.Convert(data, new DocConverterOptions { Limits = ResourceLimits.Default with { MaxNestingDepth = 1 } }));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), nesting.LimitName);
        var cells = Assert.Throws<ResourceLimitExceededException>(() => DocToPdf.Convert(data, new DocConverterOptions { Limits = ResourceLimits.Default with { MaxTableCells = 3 } }));
        Assert.Equal(nameof(ResourceLimits.MaxTableCells), cells.LimitName);
        var stream = Assert.Throws<ResourceLimitExceededException>(() => DocToPdf.Convert(data, new DocConverterOptions { Limits = ResourceLimits.Default with { MaxDecompressedBytes = 1000 } }));
        Assert.Equal(nameof(ResourceLimits.MaxDecompressedBytes), stream.LimitName);
        Assert.Throws<ResourceLimitExceededException>(() => DocToPdf.Convert(data, new DocConverterOptions { Limits = ResourceLimits.Default with { Timeout = TimeSpan.FromTicks(1) } }));

        // A picture larger than MaxResourceBytes is left out.
        var diagnostics = new List<DocumentDiagnostic>();
        var pdf = DocToPdf.ConvertToBytes(Sample("PngPicture.doc"), new DocConverterOptions { Diagnostics = diagnostics.Add, Limits = ResourceLimits.Default with { MaxResourceBytes = 100 } });
        Assert.Contains(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded && d.Message.Contains("MaxResourceBytes", StringComparison.Ordinal));
        Assert.Empty(new ImageExtractor(PdfReader.Open(pdf)).ExtractPage(0).Images);
    }

    [Fact]
    public void Pictures_are_drawn_and_unsupported_ones_reported()
    {
        var images = new ImageExtractor(PdfReader.Open(DocToPdf.ConvertToBytes(Sample("testWORD_3imgs.doc")))).ExtractPage(0).Images;
        Assert.Equal(3, images.Count);
        var diagnostics = new List<DocumentDiagnostic>();
        DocToPdf.Convert(Sample("testWORD.doc"), new DocConverterOptions { Diagnostics = diagnostics.Add });
        Assert.DoesNotContain(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded);
    }

    [Fact]
    public async Task Streams_files_and_async_overloads_convert()
    {
        var data = Sample("test2.doc");
        var expected = Docx.AllText(DocToPdf.ConvertToBytes(data));
        Assert.Contains("simple word document", expected, StringComparison.Ordinal);
        using (var stream = new MemoryStream(data))
        {
            Assert.Equal(expected, Docx.AllText(DocToPdf.ConvertToBytes(stream)));
        }

        using (var stream = new MemoryStream(data))
        {
            Assert.Equal(expected, Docx.AllText(DocToPdf.Convert(stream).ToArray()));
        }

        var path = Path.Combine(Path.GetTempPath(), $"doc-{Guid.NewGuid():N}.doc");
        await File.WriteAllBytesAsync(path, data, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(expected, Docx.AllText(DocToPdf.Convert(path).ToArray()));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(expected, Docx.AllText((await DocToPdf.ConvertAsync(data, cancellationToken: TestContext.Current.CancellationToken)).ToArray()));
        using (var stream = new MemoryStream(data))
        {
            Assert.Equal(expected, Docx.AllText((await DocToPdf.ConvertAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).ToArray()));
        }

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocToPdf.ConvertAsync(data, cancellationToken: canceled.Token));
        Assert.Throws<ArgumentNullException>(() => DocToPdf.Convert((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => DocToPdf.Convert((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => DocToPdf.Convert((string)null!));
        Assert.Throws<ArgumentNullException>(() => DocToPdf.Convert(data, new DocConverterOptions { Limits = null! }));
    }

    [Fact]
    public void Document_properties_and_options_reach_the_pdf()
    {
        var pdf = DocToPdf.Convert(Sample("testWORD.doc"));
        Assert.Equal("Sample Word Document", pdf.Info.Title);
        Assert.Equal("Keith Bennett", pdf.Info.Author);
        Assert.Contains(pdf.Outline, o => o.Title == "Heading Level 1");
        Assert.Equal("Mine", DocToPdf.Convert(Sample("testWORD.doc"), new DocConverterOptions { Title = "Mine", Tagged = true }).Info.Title);
        Assert.Empty(DocToPdf.Convert(Sample("testWORD.doc"), new DocConverterOptions { HeadingBookmarks = false }).Outline);
    }

    /// <summary>The web addresses the PDF's links open, without a trailing slash.</summary>
    private static List<string> Links(PdfDocument pdf) =>
        [.. pdf.Pages.SelectMany(p => p.Links).Select(l => l.Uri?.ToString().TrimEnd('/')).OfType<string>()];

    /// <summary>Finds a paragraph's words in order from a position, returning the position after its last word.</summary>
    private static int FindWords(string name, string text, string paragraph, int position)
    {
        foreach (var raw in paragraph.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = Words(raw);
            if (word.Length == 0)
            {
                continue; // Punctuation alone, or characters the bundled fonts do not have.
            }

            var found = text.IndexOf(word, position, StringComparison.OrdinalIgnoreCase);
            Assert.True(found >= 0, $"{name}: '{raw}' of \"{paragraph}\" is missing after position {position}.");
            position = found + word.Length;
        }

        return position;
    }

    /// <summary>Letters and digits only, of the scripts the bundled fonts have (Latin, Greek, Cyrillic).</summary>
    private static string Words(string text) => new([.. text.Where(c => char.IsLetterOrDigit(c) && c < '\u0590')]);

    /// <summary>The text without white space.</summary>
    private static string Compact(string text) => new([.. text.Where(c => !char.IsWhiteSpace(c))]);
}
