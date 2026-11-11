using System.Text;
using System.Xml.Linq;
using PDFtoImage;
using RaiCore.Pdf.Filters;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Fonts.OpenType;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;
using RaiCore.Pdf.Security;

namespace RaiCore.Pdf.Tests.PdfA;

/// <summary>
/// PDF/A structure that can be checked without veraPDF (which runs in CI, see <see cref="PdfAVeraPdfTests"/>): the
/// identification, the output intent, embedded fonts and the rules enforced when saving.
/// </summary>
public class PdfATests
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace PdfAId = "http://www.aiim.org/pdfa/ns/id/";
    private static readonly XNamespace PdfASchema = "http://www.aiim.org/pdfa/ns/schema#";
    private static readonly XNamespace PdfAProperty = "http://www.aiim.org/pdfa/ns/property#";

    private static PdfDocument Minimal(PdfAConformance level = PdfAConformance.PdfA2B)
    {
        var doc = new PdfDocument { PdfAConformance = level }.EmbedStandardFonts();
        doc.AddPage().Canvas.DrawText("Archived", StandardFont.Helvetica, 12, 72, 720);
        return doc;
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2B, "2")]
    [InlineData(PdfAConformance.PdfA3B, "3")]
    public void Xmp_identifies_the_part_and_conformance(PdfAConformance level, string part)
    {
        var xmp = PdfMetadataReader.Xmp(Minimal(level).ToArray());

        Assert.Equal(part, Assert.Single(xmp.Descendants(PdfAId + "part")).Value);
        Assert.Equal("B", Assert.Single(xmp.Descendants(PdfAId + "conformance")).Value);
    }

    [Fact]
    public void Without_PdfA_there_is_no_identification_output_intent_or_extension_schema()
    {
        var doc = new PdfDocument();
        doc.Info.CustomProperties["Department"] = "Archive";
        doc.AddPage().Canvas.DrawText("Plain", StandardFont.Helvetica, 12, 72, 720);
        var bytes = doc.ToArray();

        Assert.Empty(PdfMetadataReader.Xmp(bytes).Descendants(PdfAId + "part"));
        Assert.Empty(PdfMetadataReader.Xmp(bytes).Descendants(PdfASchema + "namespaceURI"));
        Assert.Null(PdfReader.Open(bytes).Catalog["OutputIntents"]);
    }

    [Fact]
    public void The_output_intent_embeds_an_srgb_monitor_profile()
    {
        var reader = PdfReader.Open(Minimal().ToArray());

        var intents = (PdfArray)reader.Resolve(reader.Catalog["OutputIntents"])!;
        var intent = (PdfDictionary)reader.Resolve(Assert.Single(intents.Items))!;
        Assert.Equal("OutputIntent", Name(intent["Type"]));
        Assert.Equal("GTS_PDFA1", Name(intent["S"]));
        Assert.Equal("sRGB IEC61966-2.1", Text(intent["OutputConditionIdentifier"]));

        Assert.IsType<PdfReference>(intent["DestOutputProfile"]); // One shared, indirect profile stream.
        var profile = (PdfStreamObject)reader.Resolve(intent["DestOutputProfile"])!;
        Assert.Equal(3, ((PdfInteger)profile.Dictionary["N"]!).Value);
        var icc = StreamDecoder.Decode(profile, reader.Resolve);
        Assert.Equal(icc.Length, (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(icc));
        Assert.Equal("mntr", Encoding.ASCII.GetString(icc, 12, 4)); // A monitor profile,
        Assert.Equal("RGB ", Encoding.ASCII.GetString(icc, 16, 4)); // in RGB,
        Assert.Equal("XYZ ", Encoding.ASCII.GetString(icc, 20, 4));
        Assert.Equal("acsp", Encoding.ASCII.GetString(icc, 36, 4));
        Assert.InRange(icc[8], 2, 4); // of an ICC version PDF/A-2 allows (below 5).
    }

    [Fact]
    public void The_trailer_has_a_file_identifier_and_no_encryption()
    {
        var reader = PdfReader.Open(Minimal().ToArray());

        var id = (PdfArray)reader.Trailer["ID"]!;
        Assert.Equal(2, id.Items.Count);
        Assert.Equal(16, ((PdfString)id.Items[0]).Bytes.Length);
        Assert.Null(reader.Trailer["Encrypt"]);
    }

    [Fact]
    public void Encryption_cannot_be_combined_with_PdfA()
    {
        var doc = Minimal();
        doc.Encryption = new PdfEncryption { UserPassword = "secret" };

        var ex = Assert.Throws<InvalidOperationException>(() => doc.ToArray());
        Assert.Contains("encryption", ex.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => HtmlToPdf.ConvertToBytes(
            "<p>Secret</p>", new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA2B, Encryption = new PdfEncryption { UserPassword = "secret" } }));
    }

    [Fact]
    public void Xmp_metadata_cannot_be_turned_off()
    {
        var doc = Minimal();
        doc.IncludeXmpMetadata = false;

        var ex = Assert.Throws<InvalidOperationException>(() => doc.ToArray());
        Assert.Contains("XMP", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pages_of_other_files_are_rejected()
    {
        var source = PdfReader.Open(new PdfDocument().AddPageWithText("Source").ToArray());

        var imported = Minimal();
        imported.ImportPages(source);
        Assert.Contains("other PDF files", Assert.Throws<InvalidOperationException>(() => imported.ToArray()).Message, StringComparison.Ordinal);

        var drawn = Minimal();
        drawn.Pages[0].Canvas.DrawPage(source.Pages[0], 0, 0, 100, 100);
        Assert.Throws<InvalidOperationException>(() => drawn.ToArray());

        // Without PDF/A both are fine.
        imported.PdfAConformance = PdfAConformance.None;
        drawn.PdfAConformance = PdfAConformance.None;
        imported.ToArray();
        drawn.ToArray();
    }

    [Fact]
    public void Standard_fonts_need_a_substitute()
    {
        var doc = new PdfDocument { PdfAConformance = PdfAConformance.PdfA2B };
        doc.AddPage().Canvas.DrawText("Unembedded", StandardFont.TimesRoman, 12, 72, 720);

        var ex = Assert.Throws<InvalidOperationException>(() => doc.ToArray());
        Assert.Contains("Times-Roman", ex.Message, StringComparison.Ordinal);
        Assert.Contains("EmbedStandardFonts", ex.Message, StringComparison.Ordinal);

        // SVG text uses the standard fonts too.
        var svg = new PdfDocument { PdfAConformance = PdfAConformance.PdfA2B };
        svg.AddPage().Canvas.DrawImage(PdfImage.LoadSvg("<svg xmlns='http://www.w3.org/2000/svg' width='50' height='20'><text y='15'>Hi</text></svg>"), 72, 700, 50, 20);
        Assert.Throws<InvalidOperationException>(() => svg.ToArray());
        svg.EmbedStandardFonts().ToArray();
    }

    [Theory]
    [InlineData(StandardFont.Symbol)]
    [InlineData(StandardFont.ZapfDingbats)]
    public void Symbol_fonts_have_no_substitute(StandardFont font)
    {
        Assert.Null(StandardFontSubstitutes.Get(font));
        var doc = Minimal();
        doc.Pages[0].Canvas.DrawText("abc", font, 12, 72, 700);

        var ex = Assert.Throws<InvalidOperationException>(() => doc.ToArray());
        Assert.Contains("no embeddable substitute", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StandardFont.Helvetica, "LiberationSans")]
    [InlineData(StandardFont.HelveticaBoldOblique, "LiberationSans-BoldItalic")]
    [InlineData(StandardFont.TimesRoman, "LiberationSerif")]
    [InlineData(StandardFont.TimesItalic, "LiberationSerif-Italic")]
    [InlineData(StandardFont.CourierBold, "LiberationMono-Bold")]
    [InlineData(StandardFont.CourierBoldOblique, "LiberationMono-BoldItalic")]
    public void Each_standard_font_has_the_matching_Liberation_face(StandardFont font, string postScriptName)
    {
        var substitute = StandardFontSubstitutes.Get(font)!;

        Assert.Equal(postScriptName, substitute.PostScriptName);
        Assert.Same(substitute, StandardFontSubstitutes.Get(font));

        // Metric-compatible: text measures the same (up to the rounding of the standard widths to 1/1000 em), so
        // layout done with the standard font still fits.
        const string Text = "The quick brown fox, \u00C6\u00D8\u00C5 \u20AC 1234";
        Assert.Equal(PdfFont.Standard(font).MeasureText(Text, 10), substitute.MeasureText(Text, 10), 0.05);
    }

    [Fact]
    public void Standard_fonts_are_embedded_as_TrueType_fonts_with_WinAnsi_encoding()
    {
        // Codes A0 and AD are named "space" and "hyphen" by WinAnsiEncoding; 80 is the euro sign.
        const string Text = "Caf\u00E9 \u20AC5 \u2013 \u201Cok\u201D no\u00A0break soft\u00ADhyphen";
        var doc = Minimal();
        doc.Pages[0].Canvas.DrawText(Text, StandardFont.CourierOblique, 12, 72, 700);
        var reader = PdfReader.Open(doc.ToArray());

        var fonts = (PdfDictionary)reader.Pages[0].Resources["Font"]!;
        var font = (PdfDictionary)reader.Resolve(fonts["F2"])!;
        Assert.Equal("TrueType", Name(font["Subtype"]));
        Assert.Equal("WinAnsiEncoding", Name(font["Encoding"]));
        Assert.Matches("^[A-Z]{6}\\+LiberationMono-Italic$", Name(font["BaseFont"]));

        var descriptor = (PdfDictionary)reader.Resolve(font["FontDescriptor"])!;
        var flags = ((PdfInteger)descriptor["Flags"]!).Value;
        Assert.Equal(32, flags & 36); // Nonsymbolic, not symbolic.
        Assert.Equal(64, flags & 64); // Italic.
        Assert.Equal(1, flags & 1); // Fixed pitch.

        var program = (PdfStreamObject)reader.Resolve(descriptor["FontFile2"])!;
        var embedded = OpenTypeFont.Load(StreamDecoder.Decode(program, reader.Resolve));
        Assert.True(embedded.HasTable("cmap"));
        Assert.Equal(0x00030000u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(embedded.GetTable("post"))); // No glyph names.
        Assert.Equal(32, embedded.GetTable("post").Length);

        var first = (int)((PdfInteger)font["FirstChar"]!).Value;
        var last = (int)((PdfInteger)font["LastChar"]!).Value;
        var widths = (PdfArray)reader.Resolve(font["Widths"])!;
        Assert.Equal(last - first + 1, widths.Items.Count);

        // Every code maps through its WinAnsi glyph name's Unicode value and the (3,1) cmap to a real glyph whose
        // width is the one in /Widths, as PDF/A (ISO 19005-2, 6.2.11.4.1 and 6.2.11.5) requires.
        var codes = WinAnsiCodes(Text);
        Assert.Contains((byte)0xA0, codes);
        Assert.Contains((byte)0xAD, codes);
        foreach (var code in codes)
        {
            var unicode = code switch { 0xA0 => ' ', 0xAD => '-', _ => WinAnsiEncoding.Decode(code) };
            var gid = embedded.GetGlyphId(unicode);
            Assert.True(gid != 0, $"Code {code:X2} maps to .notdef.");
            var width = Number(widths.Items[code - first]);
            Assert.Equal(embedded.GetAdvanceWidth(gid) * 1000.0 / embedded.UnitsPerEm, width, 1.0);
            Assert.True(width > 0);

            // Readers that take the character's own Unicode value (no-break space, soft hyphen) find the same glyph.
            Assert.Equal(gid, embedded.GetGlyphId(WinAnsiEncoding.Decode(code)));
        }

        Assert.Equal(codes.Min(), first);
        Assert.Equal(codes.Max(), last);
    }

    [Fact]
    public void Standard_fonts_can_be_embedded_without_PdfA()
    {
        var embedded = new PdfDocument().EmbedStandardFonts();
        embedded.AddPage().Canvas.DrawText("Embedded", StandardFont.Helvetica, 12, 72, 720);
        Assert.Equal("TrueType", Name(FirstFont(embedded.ToArray())["Subtype"]));

        // A substitute may decline a font, which is then written as before.
        var declined = new PdfDocument { StandardFontSubstitute = _ => null };
        declined.AddPage().Canvas.DrawText("Standard", StandardFont.Helvetica, 12, 72, 720);
        var font = FirstFont(declined.ToArray());
        Assert.Equal("Type1", Name(font["Subtype"]));
        Assert.Equal("Helvetica", Name(font["BaseFont"]));
        Assert.Null(font["FontDescriptor"]);
    }

    [Fact]
    public void A_substitute_must_have_TrueType_outlines()
    {
        var cff = PdfFont.Load(Fixtures.Path("RaiCoreSans3-Subset.otf"));
        var doc = new PdfDocument { StandardFontSubstitute = _ => cff };
        doc.AddPage().Canvas.DrawText("Text", StandardFont.Helvetica, 12, 72, 720);

        Assert.Throws<NotSupportedException>(() => doc.ToArray());
    }

    [Fact]
    public void The_notdef_glyph_is_rejected()
    {
        // The test font has "A" but no "B".
        var font = PdfFont.Load(Fixtures.Path("RaiCoreTestTT-Regular.ttf"));
        var doc = Minimal();
        doc.Pages[0].Canvas.DrawText("AB", font, 12, 72, 700);

        var ex = Assert.Throws<InvalidOperationException>(() => doc.ToArray());
        Assert.Contains(".notdef", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'B' (U+0042)", ex.Message, StringComparison.Ordinal);

        doc.PdfAConformance = PdfAConformance.None;
        doc.ToArray(); // Drawn as the missing-glyph box otherwise.
    }

    [Fact]
    public void A_substitute_without_a_glyph_for_a_code_is_rejected()
    {
        // The test font has "A" but no "B": the code for "B" would show .notdef.
        var font = PdfFont.Load(Fixtures.Path("RaiCoreTestTT-Regular.ttf"));
        var doc = new PdfDocument { PdfAConformance = PdfAConformance.PdfA2B, StandardFontSubstitute = _ => font };
        doc.AddPage().Canvas.DrawText("AB", StandardFont.Helvetica, 12, 72, 720);

        Assert.Contains("'B' (U+0042)", Assert.Throws<InvalidOperationException>(() => doc.ToArray()).Message, StringComparison.Ordinal);

        doc.PdfAConformance = PdfAConformance.None;
        doc.ToArray();
    }

    [Fact]
    public void Html_text_that_no_font_has_is_rejected()
    {
        var options = new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA2B };

        var ex = Assert.Throws<InvalidOperationException>(() => HtmlToPdf.ConvertToBytes("<p>Kanji \u6F22</p>", options));
        Assert.Contains("U+6F22", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cmyk_images_are_rejected()
    {
        var doc = Minimal();
        doc.Pages[0].Canvas.DrawImage(PdfImage.Load(Fixtures.Path("Images/cmyk.jpg")), 72, 600, 50, 50);

        var ex = Assert.Throws<InvalidOperationException>(() => doc.ToArray());
        Assert.Contains("CMYK", ex.Message, StringComparison.Ordinal);

        doc.PdfAConformance = PdfAConformance.None;
        doc.ToArray();
    }

    [Fact]
    public void An_unknown_level_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDocument { PdfAConformance = (PdfAConformance)3 });
    }

    [Fact]
    public void Links_are_printable_annotations()
    {
        var reader = PdfReader.Open(PdfASamples.Canvas(PdfAConformance.PdfA2B).ToArray());

        var annotations = ((PdfArray)reader.Resolve(reader.Pages[0].Dictionary["Annots"])!).Items;
        Assert.Equal(3, annotations.Count);
        Assert.All(annotations, a => Assert.Equal(4, ((PdfInteger)((PdfDictionary)reader.Resolve(a)!)["F"]!).Value));
    }

    [Fact]
    public void Custom_properties_are_described_by_an_extension_schema()
    {
        var doc = Minimal();
        doc.Info.CustomProperties["Department"] = "Archive";
        doc.Info.CustomProperties["Case number"] = "42";
        var xmp = PdfMetadataReader.Xmp(doc.ToArray());

        var schema = Assert.Single(xmp.Descendants(PdfASchema + "namespaceURI"), e => e.Value == "http://ns.adobe.com/pdfx/1.3/").Parent!;
        Assert.Equal("pdfx", schema.Element(PdfASchema + "prefix")!.Value);
        var properties = schema.Element(PdfASchema + "property")!.Element(Rdf + "Seq")!.Elements(Rdf + "li").ToList();
        Assert.Equal(["Case_x0020_number", "Department"], properties.Select(p => p.Element(PdfAProperty + "name")!.Value));
        Assert.All(properties, p =>
        {
            Assert.Equal("Text", p.Element(PdfAProperty + "valueType")!.Value);
            Assert.Equal("external", p.Element(PdfAProperty + "category")!.Value);
            Assert.NotEmpty(p.Element(PdfAProperty + "description")!.Value);
        });

        // The properties themselves are where the schema says.
        Assert.Equal("Archive", xmp.Descendants(XNamespace.Get("http://ns.adobe.com/pdfx/1.3/") + "Department").Single().Value);

        // No custom properties, no extension schema.
        Assert.Empty(PdfMetadataReader.Xmp(Minimal().ToArray()).Descendants(PdfASchema + "namespaceURI"));
    }

    [Fact]
    public void Html_options_make_a_PdfA_document_with_embedded_standard_fonts()
    {
        var doc = HtmlToPdf.Convert("<p>Text</p>", new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA3B });

        Assert.Equal(PdfAConformance.PdfA3B, doc.PdfAConformance);
        Assert.NotNull(doc.StandardFontSubstitute);
        Assert.Null(HtmlToPdf.Convert("<p>Text</p>").StandardFontSubstitute);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_font_in_the_samples_is_embedded_and_no_color_is_CMYK(bool html)
    {
        var doc = html ? PdfASamples.Html(PdfAConformance.PdfA2B) : PdfASamples.Canvas(PdfAConformance.PdfA2B);
        var reader = PdfReader.Open(doc.ToArray());

        var fonts = 0;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        void Visit(PdfDictionary? resources)
        {
            if (resources is null || !seen.Add(resources))
            {
                return;
            }

            foreach (var (_, value) in (reader.Resolve(resources["Font"]) as PdfDictionary)?.Entries ?? [])
            {
                var font = (PdfDictionary)reader.Resolve(value)!;
                var descendant = Name(font["Subtype"]) == "Type0"
                    ? (PdfDictionary)reader.Resolve(((PdfArray)reader.Resolve(font["DescendantFonts"])!).Items[0])!
                    : font;
                var descriptor = (PdfDictionary)reader.Resolve(descendant["FontDescriptor"])!;
                Assert.True(descriptor["FontFile2"] is not null || descriptor["FontFile3"] is not null, $"{Name(font["BaseFont"])} is not embedded.");
                fonts++;
            }

            foreach (var (_, value) in (reader.Resolve(resources["XObject"]) as PdfDictionary)?.Entries ?? [])
            {
                var xobject = ((PdfStreamObject)reader.Resolve(value)!).Dictionary;
                Assert.NotEqual("DeviceCMYK", (reader.Resolve(xobject["ColorSpace"]) as PdfName)?.Value);
                Assert.Null(xobject["Interpolate"]);
                Visit(reader.Resolve(xobject["Resources"]) as PdfDictionary);
            }
        }

        foreach (var page in reader.Pages)
        {
            Assert.NotNull(page.Dictionary["Resources"]); // Every content stream has its own resources (6.2.2).
            Visit(page.Resources);
        }

        Assert.True(fonts >= (html ? 4 : 7), $"Only {fonts} fonts found.");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Pdfium_renders_the_sample()
    {
        var pdf = PdfASamples.Canvas(PdfAConformance.PdfA2B).ToArray();
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

        // The first line (Helvetica, now Liberation Sans) is drawn: some pixel on its baseline row is dark.
        var y = bitmap.Height - (int)(PdfPageSize.A4.Height - 72) - 3;
        Assert.Contains(Enumerable.Range(72, 200), x => bitmap.GetPixel(x, y).Red < 100);
    }

    private static HashSet<byte> WinAnsiCodes(string text) => [.. WinAnsiEncoding.Encode(text)];

    private static PdfDictionary FirstFont(byte[] pdf)
    {
        var reader = PdfReader.Open(pdf);
        var fonts = (PdfDictionary)reader.Pages[0].Resources["Font"]!;
        return (PdfDictionary)reader.Resolve(fonts.Entries.First().Value)!;
    }

    private static string Name(PdfObject? value) => ((PdfName)value!).Value;

    private static string Text(PdfObject? value) => Encoding.Latin1.GetString(((PdfString)value!).Bytes);

    private static double Number(PdfObject value) => value is PdfInteger i ? i.Value : ((PdfReal)value).Value;
}

internal static class PdfATestExtensions
{
    public static PdfDocument AddPageWithText(this PdfDocument doc, string text)
    {
        doc.AddPage().Canvas.DrawText(text, StandardFont.Helvetica, 12, 72, 720);
        return doc;
    }
}
