using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Slides;
using RaiCore.Pdf.Slides.Model;
using RaiCore.Pdf.Slides.Ppt;
using static RaiCore.Pdf.Tests.Slides.PptBuilder;

namespace RaiCore.Pdf.Tests.Slides;

/// <summary>
/// Converting PowerPoint 97-2003 presentations to PDF: real-world files from Apache POI and Apache Tika checked against
/// an independent reader (Fixtures/Slides/Ppt/make-expected.py) and the expectations of POI's and Tika's own tests;
/// small built files for each structure (styles, groups, free forms, pictures, hidden slides), damaged and hostile
/// files, encrypted and older versions, limits.
/// </summary>
public partial class PptToPdfTests
{
    private static string FixturePath(string name) => Path.Combine("Slides", "Ppt", name);

    private static byte[] Fixture(string name) => Fixtures.Read(FixturePath(name));

    private static string[] Pages(byte[] pdf) => [.. new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    private static JsonDocument Expected() => JsonDocument.Parse(File.ReadAllText(Fixtures.Path(FixturePath("expected.json"))));

    private static Presentation Read(byte[] data, PptxConverterOptions? options = null, List<SlideDiagnostic>? diagnostics = null) =>
        PptConverter.Read(data, options ?? new PptxConverterOptions { IncludeHiddenSlides = true },
            report: diagnostics is null ? null : (slide, subject, message) => diagnostics.Add(new SlideDiagnostic(slide, subject, message)));

    [GeneratedRegex(@"\w+")]
    private static partial Regex Word();

    private static Dictionary<string, int> Words(IEnumerable<string> texts) =>
        texts.SelectMany(t => Word().Matches(t.ToLowerInvariant()).Select(m => m.Value)).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>The text of a slide's own shapes (not its master's), groups included.</summary>
    private static List<string> Texts(IEnumerable<Shape> shapes, bool masters = false)
    {
        var texts = new List<string>();
        foreach (var shape in shapes.Where(s => masters || !s.Background))
        {
            switch (shape)
            {
                case GroupShape group:
                    texts.AddRange(Texts(group.Children, true));
                    break;
                case AutoShape { Text: { } frame }:
                    texts.AddRange(frame.Paragraphs.Select(p => string.Concat(p.Runs.Select(r => r.Text))));
                    break;
            }
        }

        return texts;
    }

    private static IEnumerable<Shape> All(IEnumerable<Shape> shapes) =>
        shapes.SelectMany(s => s is GroupShape g ? All(g.Children).Prepend(s) : [s]);

    /// <summary>The presentations the independent reader could read.</summary>
    public static TheoryData<string> Readable()
    {
        var data = new TheoryData<string>();
        using var expected = Expected();
        foreach (var property in expected.RootElement.EnumerateObject().Where(p => p.Value.TryGetProperty("slides", out _)))
        {
            data.Add(property.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Readable))]
    public void Slides_size_properties_and_text_match_the_independent_reader(string name)
    {
        using var expected = Expected();
        var deck = expected.RootElement.GetProperty(name);
        var presentation = Read(Fixture(name));
        var slides = deck.GetProperty("slides").EnumerateArray().ToList();
        Assert.Equal(slides.Count, presentation.Slides.Count);
        var size = deck.GetProperty("size").EnumerateArray().Select(v => v.GetDouble()).ToList();
        Assert.Equal(size[0], presentation.Width, 3);
        Assert.Equal(size[1], presentation.Height, 3);
        if (deck.GetProperty("title").GetString() is { } title)
        {
            Assert.Equal(title, presentation.Title);
        }

        if (deck.GetProperty("author").GetString() is { } author)
        {
            Assert.Equal(author, presentation.Author);
        }

        for (var i = 0; i < slides.Count; i++)
        {
            Assert.Equal(slides[i].GetProperty("hidden").GetBoolean(), presentation.Slides[i].Hidden);

            // Every word the reader finds in the slide's text records is in the slide's shapes, as often; text in
            // the slide list that no shape refers to is not drawn by PowerPoint either (one file has such text).
            var oracle = Words(slides[i].GetProperty("texts").EnumerateArray().Select(t => t.GetString()!));
            var ours = Words(Texts(presentation.Slides[i].Shapes));
            var missing = oracle.Where(w => ours.GetValueOrDefault(w.Key) < w.Value).Select(w => w.Key).ToList();
            Assert.True(missing.Count == 0, $"{name} slide {i + 1} lacks {string.Join(", ", missing)}");
        }

        var pdf = PptToPdf.ConvertToBytes(Fixture(name), new PptxConverterOptions { IncludeHiddenSlides = true });
        Assert.Equal(slides.Count == 0 ? 1 : slides.Count, PdfReader.Open(pdf).Pages.Count);
        if (ExternalTools.Find("qpdf") is not null)
        {
            ExternalTools.AssertQpdfClean(pdf);
        }
    }

    /// <summary>The presentations with text that POI's and Tika's tests assert.</summary>
    public static TheoryData<string> Quoted()
    {
        var data = new TheoryData<string>();
        using var expected = Expected();
        foreach (var property in expected.RootElement.EnumerateObject())
        {
            if (property.Value.TryGetProperty("quoted", out var quoted) && quoted.TryGetProperty("slides", out _))
            {
                data.Add(property.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Quoted))]
    public void Text_that_POI_and_Tika_find_is_on_the_pages(string name)
    {
        using var expected = Expected();
        var quoted = expected.RootElement.GetProperty(name).GetProperty("quoted");
        var pages = Pages(PptToPdf.ConvertToBytes(Fixture(name))).Select(p => Regex.Replace(p, @"\s+", " ")).ToArray();
        var slides = quoted.GetProperty("slides").EnumerateArray().ToList();
        Assert.Equal(slides.Count, pages.Length);
        for (var i = 0; i < slides.Count; i++)
        {
            foreach (var text in slides[i].EnumerateArray().Select(t => t.GetString()!))
            {
                Assert.Contains(text, pages[i], StringComparison.Ordinal);
            }
        }

        if (quoted.TryGetProperty("title", out var title))
        {
            var pdf = PptToPdf.Convert(Fixture(name));
            Assert.Equal(title.GetString(), pdf.Info.Title);
            Assert.Equal(quoted.GetProperty("author").GetString(), pdf.Info.Author);
            Assert.Equal("RaiCore.Pdf.Slides", pdf.Info.Creator);
        }
    }

    [Fact]
    public void Encrypted_and_older_presentations_are_not_supported()
    {
        var encrypted = Assert.Throws<NotSupportedException>(() => PptToPdf.Convert(Fixture("Password_Protected-hello.ppt")));
        Assert.Contains("encrypted", encrypted.Message, StringComparison.Ordinal);
        var old = Assert.Throws<NotSupportedException>(() => PptToPdf.Convert(Fixture("pp40only.ppt")));
        Assert.Contains("PowerPoint 4.0", old.Message, StringComparison.Ordinal);

        // A PowerPoint 95 file: its own Header stream, and a current user stream PowerPoint 97 did not write.
        var (stream, _) = Document([Slide([])]);
        var ppt95 = Compound(("Current User", new byte[32]), ("Header", new byte[16]), ("PowerPoint Document", stream));
        Assert.Contains("PowerPoint 95", Assert.Throws<NotSupportedException>(() => PptToPdf.Convert(ppt95)).Message, StringComparison.Ordinal);
        var ppt95Only = Compound(("Header", new byte[16]), ("Text_Content", new byte[16]));
        Assert.Contains("PowerPoint 95", Assert.Throws<NotSupportedException>(() => PptToPdf.Convert(ppt95Only)).Message, StringComparison.Ordinal);

        // The encrypted marker of the current user atom, and an encrypted OOXML package.
        Assert.Throws<NotSupportedException>(() => PptToPdf.Convert(Presentation([Slide([])], token: 0xF3D1C4DF)));
        Assert.Throws<NotSupportedException>(() => PptToPdf.Convert(Compound(("EncryptionInfo", new byte[16]), ("EncryptedPackage", new byte[16]))));

        // PPTX points at this converter for a PowerPoint 97-2003 file.
        var pptx = Assert.Throws<NotSupportedException>(() => PptxToPdf.Convert(Fixture("basic_test_ppt_file.ppt")));
        Assert.Contains("PptToPdf", pptx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_PowerPoint_97_copy_in_a_dual_storage_file_is_converted()
    {
        var (stream, edit) = Document([Slide([Shape(202, 1025, (100, 100, 2000, 600), null, false, 0xA00, TextBox("Dual"))])]);
        var dual = Compound(("Current User", new byte[32]), ("Header", new byte[16]), ("PowerPoint Document", new byte[64]),
            ("PP97_DUALSTORAGE/Current User", CurrentUser(edit)), ("PP97_DUALSTORAGE/PowerPoint Document", stream));
        Assert.Contains("Dual", Pages(PptToPdf.ConvertToBytes(dual))[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Data_that_is_not_a_presentation_is_rejected()
    {
        Assert.Throws<PresentationFormatException>(() => PptToPdf.Convert(Encoding.ASCII.GetBytes("hello, not a presentation")));
        var zip = Assert.Throws<PresentationFormatException>(() => PptToPdf.Convert(Fixtures.Read(Path.Combine("Slides", "basic.pptx"))));
        Assert.Contains("PptxToPdf", zip.Message, StringComparison.Ordinal);
        Assert.Throws<PresentationFormatException>(() => PptToPdf.Convert(Compound(("WordDocument", new byte[64]))));
        Assert.Throws<PresentationFormatException>(() => PptToPdf.Convert(Compound(("Current User", CurrentUser(0)), ("PowerPoint Document", new byte[100]))));
        Assert.Throws<ArgumentNullException>(() => PptToPdf.Convert((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => PptToPdf.Convert((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => PptToPdf.Convert((string)null!));
    }

    [Fact]
    public void Damaged_and_truncated_files_throw_only_documented_exceptions()
    {
        foreach (var name in new[] { "basic_test_ppt_file.ppt", "pictures.ppt", "bullets.ppt" })
        {
            var data = Fixture(name);
            for (var length = 0; length < data.Length; length += Math.Max(1, data.Length / 23))
            {
                try
                {
                    PptToPdf.ConvertToBytes(data[..length]);
                }
                catch (PresentationFormatException)
                {
                }
            }

            // Bytes overwritten all through the file.
            var random = new Random(42);
            for (var round = 0; round < 40; round++)
            {
                var damaged = (byte[])data.Clone();
                for (var i = 0; i < 16; i++)
                {
                    damaged[random.Next(512, damaged.Length)] = (byte)random.Next(256);
                }

                try
                {
                    PptToPdf.ConvertToBytes(damaged);
                }
                catch (Exception ex) when (ex is PresentationFormatException or NotSupportedException or ResourceLimitExceededException)
                {
                }
            }
        }
    }

    [Fact]
    public void A_broken_edit_chain_is_recovered_from_the_persist_directories()
    {
        // POI's sample: the edit chain loops back on itself before reaching the edit that has most slides.
        Assert.Equal(6, Read(Fixture("57272_corrupted_usereditatom.ppt")).Slides.Count);

        var looped = Presentation([Slide([Shape(202, 1025, (100, 100, 2000, 600), null, false, 0xA00, TextBox("Loop"))])], selfLoop: true);
        Assert.Contains("Loop", Pages(PptToPdf.ConvertToBytes(looped))[0], StringComparison.Ordinal);

        // No current user stream at all: the stream's own directories and document.
        var (stream, _) = Document([Slide([Shape(202, 1025, (100, 100, 2000, 600), null, false, 0xA00, TextBox("Lost"))])]);
        Assert.Contains("Lost", Pages(PptToPdf.ConvertToBytes(Compound(("PowerPoint Document", stream))))[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Hidden_and_chosen_slides()
    {
        static byte[] Hidden(bool hidden) => Atom(0x03F9, [.. Ints(0, 0), 0, 0, .. Shorts(hidden ? 4 : 0), 0, 0, 0, 0]);
        static byte[] Titled(string text, bool hidden) =>
            Slide([Shape(202, 1025, (100, 100, 2000, 600), null, false, 0xA00, TextBox(text))], 0, 0, Hidden(hidden));
        var data = Presentation([Titled("One", false), Titled("Two", true), Titled("Three", false)]);
        var shown = Pages(PptToPdf.ConvertToBytes(data));
        Assert.Equal(["One", "Three"], shown.Select(p => p.Trim()));
        Assert.Equal(3, PptToPdf.Convert(data, new PptxConverterOptions { IncludeHiddenSlides = true }).Pages.Count);
        Assert.Equal(["Two", "Three"], Pages(PptToPdf.ConvertToBytes(data, new PptxConverterOptions { SlideNumbers = [3, 2, 9] })).Select(p => p.Trim()));
        var model = Read(data);
        Assert.Equal([1, 2, 3], model.Slides.Select(s => s.Number));
        Assert.True(model.Slides[1].Hidden);

        // No slides: one blank page of the slide size.
        var empty = PdfReader.Open(PptToPdf.ConvertToBytes(data, new PptxConverterOptions { SlideNumbers = [] }));
        Assert.Single(empty.Pages);
        Assert.Equal(720, empty.Pages[0].MediaBox.Width, 3);
    }

    [Fact]
    public void Text_runs_paragraphs_and_bullets_take_their_styles()
    {
        // "Big bold\rcentered red" with a bullet of a symbol font on the second paragraph.
        var text = "Big bold\rcentered red";
        var style = new List<byte>();
        style.AddRange(Ints(9));
        style.AddRange(Shorts(0));
        style.AddRange(Ints(0x0800));
        style.AddRange(Shorts(0)); // left
        style.AddRange(Ints(13));
        style.AddRange(Shorts(1));
        style.AddRange(Ints(0x0800 | 0x0F | 0x80 | 0x10 | 0x0100 | 0x0400 | 0x1000));
        style.AddRange(Shorts(0x0F, 0xA7, 1)); // bullet flags, character, font 1 (Wingdings)
        style.AddRange(Shorts(1, 80, 144, 0)); // center, line spacing 80%, text at 18 pt, bullet at 0
        style.AddRange(Ints(9, 0x1 | 0x20000));
        style.AddRange(Shorts(1, 32)); // bold, 32 pt
        style.AddRange(Ints(13, 0x40000 | 0x10000));
        style.AddRange(Shorts(2)); // Times New Roman
        style.AddRange([0xFF, 0x00, 0x00, 0xFE]); // red
        var shape = Shape(202, 1025, (100, 100, 3000, 1000), Properties((0x87, 1, null), (0x85, 2, null)), false, 0xA00, TextBox(text, 4, [.. style]));
        var slide = Read(Presentation([Slide([shape])], ["Arial", "Wingdings", "Times New Roman"])).Slides[0];
        var frame = Assert.IsType<AutoShape>(Assert.Single(slide.Shapes)).Text!;
        Assert.Equal(TextAnchor.Middle, frame.Anchor);
        Assert.False(frame.Wrap);
        Assert.Equal(2, frame.Paragraphs.Count);
        var first = frame.Paragraphs[0];
        Assert.Null(first.Bullet);
        Assert.Equal(ParagraphAlignment.Left, first.Alignment);
        Assert.True(first.Runs[0].Style.Bold);
        Assert.Equal(32, first.Runs[0].Style.Size);
        Assert.Equal("Arial", first.Runs[0].Style.Font);
        var second = frame.Paragraphs[1];
        Assert.Equal(1, second.Level);
        Assert.Equal(ParagraphAlignment.Center, second.Alignment);
        Assert.Equal(new Spacing(0.8, false), second.LineSpacing);
        Assert.Equal(18, second.MarginLeft, 3);
        Assert.Equal(-18, second.Indent, 3);
        Assert.Equal("\u25AA", second.Bullet!.Text); // Wingdings 0xA7, as Unicode
        Assert.Null(second.Bullet.Font);
        Assert.Equal("centered red", string.Concat(second.Runs.Select(r => r.Text)));
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), second.Runs[0].Style.Color);
        Assert.Equal("Times New Roman", second.Runs[0].Style.Font);
        Assert.False(second.Runs[0].Style.Bold);
    }

    [Fact]
    public void Placeholders_take_the_master_text_styles_and_masters_draw_behind_slides()
    {
        var basic = Read(Fixture("basic_test_ppt_file.ppt"));
        var title = basic.Slides[1].Shapes.OfType<AutoShape>().Single(s => s.Placeholder == PlaceholderKind.Title);
        Assert.Equal("This is the title on page 2", string.Concat(title.Text!.Paragraphs[0].Runs.Select(r => r.Text)));
        Assert.Equal(44, title.Text.Paragraphs[0].Runs[0].Style.Size);
        Assert.Equal(ParagraphAlignment.Center, title.Text.Paragraphs[0].Alignment);
        var body = basic.Slides[1].Shapes.OfType<AutoShape>().Single(s => s.Placeholder == PlaceholderKind.Other);
        Assert.All(body.Text!.Paragraphs, p => Assert.Equal("\u2022", p.Bullet!.Text));
        Assert.All(body.Text.Paragraphs, p => Assert.Equal(32, p.Runs[0].Style.Size));
        Assert.Null(body.Fill);
        Assert.Null(body.Line);

        // The master's footer placeholder shows the footer the presentation sets; its title placeholder is not drawn.
        var footers = Read(Fixture("bug58144-headers-footers-2003.ppt")).Slides[0];
        var footer = footers.Shapes.OfType<AutoShape>().Single(s => s.Background && s.Text is not null);
        Assert.Equal("Confidential", string.Concat(footer.Text!.Paragraphs[0].Runs.Select(r => r.Text)));
        Assert.DoesNotContain(Pages(PptToPdf.ConvertToBytes(Fixture("bug58144-headers-footers-2003.ppt")))[0], "Click to edit", StringComparison.Ordinal);

        // Formatting of runs: bold, italic, colors, underline and a font of the collection.
        var runs = Read(Fixture("Single_Coloured_Page_With_Fonts_and_Alignments.ppt")).Slides[0].Shapes.OfType<AutoShape>()
            .Where(s => s.Text is not null).SelectMany(s => s.Text!.Paragraphs.SelectMany(p => p.Runs)).ToList();
        Assert.True(runs.Single(r => r.Text.StartsWith("This is the subtitle", StringComparison.Ordinal)).Style.Bold);
        var blue = runs.Single(r => r.Text.StartsWith("This bit is blue", StringComparison.Ordinal)).Style;
        Assert.True(blue.Italic);
        Assert.True(blue.Color.Blue > 0.5 && blue.Color.Red < 0.5);
        var underlined = runs.Single(r => r.Text.StartsWith("This bit is underlined", StringComparison.Ordinal)).Style;
        Assert.True(underlined.Underline);
        Assert.Equal("Times New Roman", underlined.Font);
        Assert.Equal(24, underlined.Size);
    }

    [Fact]
    public void Shapes_groups_and_free_forms_are_placed_and_drawn()
    {
        var shapes = Read(Fixture("text_shapes.ppt")).Slides[0].Shapes.OfType<AutoShape>().ToList();
        Assert.Contains(shapes, s => s.Geometry is PresetGeometry { Name: "ellipse" });
        Assert.Contains(shapes, s => s.Geometry is PresetGeometry { Name: "octagon" });

        // A group's children are in its coordinate space; one turned 90 degrees stores its turned box.
        var group = Group(2000, (0, 0, 1000, 1000), (800, 800, 1600, 1600), false,
            Shape(1, 2001, (0, 0, 500, 500), Properties((0x181, 0x0000FF, null)), true),
            Shape(3, 2002, (500, 0, 250, 1000), Properties((0x0004, 90 << 16, null)), true));
        var freeform = Shape(0, 3000, (0, 0, 800, 800), Properties(
            (0x142, 100, null), (0x143, 100, null),
            (0x145, 0, [.. Shorts(4, 4, 8), .. Ints(0, 0, 100, 0, 100, 100, 0, 100)]),
            (0x146, 0, [.. Shorts(5, 5, 2), .. Shorts(0xAA00, 0x4000, 0x0003, 0x6001, 0x8000)])));
        var slide = Read(Presentation([Slide([group, freeform])])).Slides[0];
        var model = Assert.IsType<GroupShape>(slide.Shapes[0]);
        Assert.Equal(new ShapeBox(100, 100, 100, 100), model.Box);
        Assert.Equal((0d, 0d, 125d, 125d), (model.ChildX, model.ChildY, model.ChildWidth, model.ChildHeight));
        var square = Assert.IsType<AutoShape>(model.Children[0]);
        Assert.Equal(new ShapeBox(0, 0, 62.5, 62.5), square.Box);
        Assert.Equal(new SolidFill(PdfColor.FromRgb(255, 0, 0)), square.Fill);
        var turned = Assert.IsType<AutoShape>(model.Children[1]);
        Assert.Equal(90, turned.Box.Rotation);
        Assert.Equal((62.5, 31.25), (turned.Box.Width, turned.Box.Height));
        Assert.Equal((-15.625, 78.125), (turned.Box.X, turned.Box.Y));
        var path = Assert.Single(Assert.IsType<AutoShape>(slide.Shapes[1]).Geometry.Build(100, 100));
        Assert.Equal([SegmentKind.Move, SegmentKind.Line, SegmentKind.Line, SegmentKind.Line, SegmentKind.Close], path.Segments.Select(s => s.Kind));
        Assert.Equal((100d, 100d), (path.Segments[2].X, path.Segments[2].Y));
        Assert.False(path.Filled);

        // Groups in a real file: Tika's sample nests text boxes and WordArt in groups.
        Assert.Contains(Read(Fixture("testPPT_groups.ppt")).Slides[0].Shapes, s => s is GroupShape { Children.Count: > 1 });
        var pdf = PptToPdf.ConvertToBytes(Fixture("testPPT_groups.ppt"));
        Assert.Contains("WordArt1", Pages(pdf)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Pictures_backgrounds_and_unsupported_formats()
    {
        var diagnostics = new List<SlideDiagnostic>();
        var pictures = Read(Fixture("pictures.ppt"), diagnostics: diagnostics);
        var data = pictures.Slides.SelectMany(s => s.Shapes).OfType<PictureShape>().Select(p => p.Data).ToList();
        Assert.Contains(data, d => d is [0xFF, 0xD8, ..]);
        Assert.Contains(data, d => d is [0x89, (byte)'P', (byte)'N', (byte)'G', ..]);
        Assert.Contains(data, d => d is [0xD7, 0xCD, 0xC6, 0x9A, ..]);
        Assert.Contains(data, d => d is [0x01, 0x00, 0x00, 0x00, ..]);
        Assert.Contains(diagnostics, d => d.Message.Contains("PICT is not supported", StringComparison.Ordinal));
        var converted = new List<SlideDiagnostic>();
        var pdf = PptToPdf.ConvertToBytes(Fixture("pictures.ppt"), new PptxConverterOptions { Diagnostics = converted.Add });
        Assert.Contains(converted, d => d.Message.Contains("WMF is not supported", StringComparison.Ordinal));
        Assert.Contains(converted, d => d.Message.Contains("EMF is not supported", StringComparison.Ordinal));
        Assert.Contains(" Do", PptxToPdfTests.Content(pdf), StringComparison.Ordinal);

        // Backgrounds: a picture, a pattern (as a color), a texture, a gradient; shapes filled alike.
        var backgrounds = Read(Fixture("backgrounds.ppt")).Slides;
        Assert.IsType<PictureFill>(backgrounds[0].Background);
        Assert.IsType<SolidFill>(backgrounds[1].Background);
        Assert.IsType<PictureFill>(backgrounds[2].Background);
        var gradient = Assert.IsType<GradientFill>(backgrounds[3].Background);
        Assert.True(gradient.Stops.Count >= 2);
        Assert.IsType<GradientFill>(backgrounds[3].Shapes.OfType<AutoShape>().Single().Fill);

        // A device-independent bitmap becomes a BMP file.
        var dib = new byte[40 + 4];
        Ints(40, 1, 1).CopyTo(dib, 0);
        Shorts(1, 24).CopyTo(dib, 12);
        var blip = Atom(0xF01F, [.. new byte[16], 0xFF, .. dib], 0x7A8);
        var entry = Atom(0xF007, [5, 5, .. new byte[16], 0, 0, .. Ints(blip.Length, 1, 0), 0, 0, 0, 0], 5, 2);
        var store = Container(0x040B, Container(0xF000, Container(0xF001, 1, entry)));
        var picture = Shape(75, 1025, (0, 0, 800, 800), Properties((0x0104, 1, null)));
        var bitmap = Read(Presentation([Slide([picture])], documentMore: [store], extraStreams: ("Pictures", blip))).Slides[0];
        var bmp = Assert.IsType<PictureShape>(Assert.Single(bitmap.Shapes)).Data!;
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal(54, BitConverter.ToInt32(bmp, 10));
        Assert.Contains(" Do", PptxToPdfTests.Content(PptToPdf.ConvertToBytes(Presentation([Slide([picture])], documentMore: [store], extraStreams: ("Pictures", blip)))), StringComparison.Ordinal);
    }

    [Fact]
    public void Links_open_from_text_and_shapes()
    {
        var links = Read(Fixture("WithLinks.ppt")).Slides.SelectMany(s => All(s.Shapes)).OfType<AutoShape>()
            .SelectMany(s => s.Text?.Paragraphs.SelectMany(p => p.Runs) ?? []).Where(r => r.Link is not null).ToList();
        Assert.Contains(links, r => r.Text == "http://jakarta.apache.org/poi/" && r.Link == new Uri("http://jakarta.apache.org/poi/"));
        Assert.Contains(links, r => r.Link == new Uri("http://slashdot.org/"));
    }

    [Fact]
    public void Cyrillic_and_CJK_text_is_kept()
    {
        var cyrillic = Texts(Read(Fixture("60003.ppt")).Slides[0].Shapes);
        Assert.Contains(cyrillic, t => t.Contains("\u0421\u042A\u0422\u0420\u0423\u0414\u041D\u0418\u0427\u0415\u0421\u0422\u0412\u041E", StringComparison.Ordinal));
        var cjk = Read(Fixture("bug55902-mixedFontChineseCharacters.ppt")).Slides[0];
        Assert.Contains(Texts(cjk.Shapes), t => t.Contains("\u30AB\u30BF\u30AB\u30CA", StringComparison.Ordinal));
    }

    [Fact]
    public void Hostile_records_are_bounded()
    {
        // Style runs that claim more than the text, a record longer than its container, garbage properties.
        var style = Ints(-1, 0, 0x7FFFFFFF, -1, -1, -1);
        var shape = Shape(1, 1025, (0, 0, 3000, 1000), Properties((0x145, 0, [.. Shorts(-1, -1, 8)]), (0x146, 0, new byte[3]), (0x197, 0, Shorts(1, 1, 8))),
            false, 0xA00, TextBox("Hostile", 4, style), Atom(0x0FA6, [], 0, 0, 1 << 30));
        var pdf = PptToPdf.ConvertToBytes(Presentation([Slide([shape])]));
        Assert.Contains("Hostile", Pages(pdf)[0], StringComparison.Ordinal);

        // Groups nested deeper than the limit.
        var nested = Shape(1, 1, (0, 0, 10, 10), null, true);
        for (var i = 0; i < 40; i++)
        {
            nested = Group(100 + i, (0, 0, 10, 10), (0, 0, 10, 10), true, nested);
        }

        var deep = Presentation([Slide([nested])]);
        var depth = Assert.Throws<ResourceLimitExceededException>(() => PptToPdf.Convert(deep, new PptxConverterOptions { Limits = ResourceLimits.Default with { MaxNestingDepth = 16 } }));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), depth.LimitName);
        Assert.Single(PptToPdf.Convert(deep).Pages);

        // A slide whose persist id is missing is left out and reported.
        var (stream, edit) = Document([Slide([])]);
        stream[edit - 1] = 0x7F; // The slide's offset in the persist directory: past the stream.
        var reported = new List<SlideDiagnostic>();
        Assert.Single(PptToPdf.Convert(Compound(("Current User", CurrentUser(edit)), ("PowerPoint Document", stream)), new PptxConverterOptions { Diagnostics = reported.Add }).Pages);
        Assert.Contains(reported, d => d.Subject == "slide");
    }

    [Fact]
    public void Limits_and_cancellation_are_respected()
    {
        var data = Fixture("basic_test_ppt_file.ppt");
        Assert.Equal(nameof(ResourceLimits.MaxPages), Assert.Throws<ResourceLimitExceededException>(
            () => PptToPdf.Convert(data, new PptxConverterOptions { Limits = ResourceLimits.Default with { MaxPages = 1 } })).LimitName);
        Assert.Equal(nameof(ResourceLimits.MaxElements), Assert.Throws<ResourceLimitExceededException>(
            () => PptToPdf.Convert(data, new PptxConverterOptions { Limits = ResourceLimits.Default with { MaxElements = 50 } })).LimitName);
        Assert.Single(PptToPdf.Convert(data, new PptxConverterOptions { SlideNumbers = [2], Limits = ResourceLimits.Default with { MaxPages = 1 } }).Pages);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PptToPdf.ConvertAsync(data, null, canceled.Token).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Streams_files_and_async_overloads_convert_alike()
    {
        var data = Fixture("basic_test_ppt_file.ppt");
        using var stream = new MemoryStream(data);
        Assert.Equal(2, PptToPdf.Convert(stream).Pages.Count);
        stream.Position = 0;
        Assert.Equal(2, (await PptToPdf.ConvertAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Pages.Count);
        stream.Position = 0;
        Assert.Equal(2, PdfReader.Open(PptToPdf.ConvertToBytes(stream)).Pages.Count);
        Assert.Equal(2, PptToPdf.Convert(Fixtures.Path(FixturePath("basic_test_ppt_file.ppt"))).Pages.Count);
        Assert.Equal("Mine", PptToPdf.Convert(data, new PptxConverterOptions { Title = "Mine" }).Info.Title);
        Assert.Equal("This is a test title", PptToPdf.Convert(data).Info.Title);
    }
}
