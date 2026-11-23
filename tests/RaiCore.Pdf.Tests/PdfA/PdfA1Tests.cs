using System.Text;
using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;
using RaiCore.Pdf.Tests.Fonts;
using RaiCore.Pdf.Tests.Tagging;

namespace RaiCore.Pdf.Tests.PdfA;

/// <summary>
/// PDF/A-1 (PDF/A-1b and PDF/A-1a, #195): PDF 1.4 files without transparency, checked locally; veraPDF validates the
/// samples in CI (<see cref="PdfAVeraPdfTests"/>).
/// </summary>
public class PdfA1Tests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));

    public static TheoryData<string, PdfAConformance> Samples => new()
    {
        { "canvas", PdfAConformance.PdfA1B },
        { "canvas", PdfAConformance.PdfA1A },
        { "html", PdfAConformance.PdfA1B },
        { "html", PdfAConformance.PdfA1A },
        { "layout", PdfAConformance.PdfA1A },
    };

    private static PdfDocument Sample(string kind, PdfAConformance level) => kind switch
    {
        "canvas" => PdfASamples.Opaque(level),
        "html" => PdfASamples.HtmlOpaque(level),
        _ => PdfASamples.Layout(level),
    };

    /// <summary>A PDF/A-1b document whose one page has text, ready to have more drawn on it.</summary>
    private static PdfDocument Document(PdfAConformance level = PdfAConformance.PdfA1B, bool text = true)
    {
        var doc = new PdfDocument { PdfAConformance = level, Language = "en" }.EmbedStandardFonts();
        var page = doc.AddPage();
        if (text)
        {
            page.Canvas.DrawText("Archived", Sans, 12, 72, 720);
        }

        return doc;
    }

    private static string Message(Action save) => Assert.Throws<InvalidOperationException>(save).Message;

    private static string Message(PdfDocument doc) => Message(() => doc.ToArray());

    [Theory]
    [InlineData(PdfAConformance.PdfA1B, "1.4")]
    [InlineData(PdfAConformance.PdfA1A, "1.4")]
    [InlineData(PdfAConformance.PdfA2B, "1.7")]
    [InlineData(PdfAConformance.None, "1.7")]
    public void PdfA1_files_are_PDF_1_4_with_a_cross_reference_table_and_an_identifier(PdfAConformance level, string version)
    {
        var bytes = Document(level).ToArray();
        var pdf = Encoding.Latin1.GetString(bytes);

        Assert.StartsWith($"%PDF-{version}\n", pdf, StringComparison.Ordinal);
        Assert.Equal(version, PdfReader.Open(bytes).Version);
        Assert.Contains("\nxref\n0 ", pdf, StringComparison.Ordinal);
        Assert.DoesNotContain("/ObjStm", pdf, StringComparison.Ordinal);
        Assert.DoesNotContain("/XRef", pdf, StringComparison.Ordinal);
        Assert.Contains("/ID [<", pdf, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void The_samples_use_nothing_that_PDF_1_4_or_PDF_A_1_forbids(string kind, PdfAConformance level)
    {
        var bytes = Sample(kind, level).ToArray();
        var reader = PdfReader.Open(bytes);
        var images = 0;
        var subsets = 0;
        foreach (var number in reader.Store.ObjectNumbers)
        {
            var value = reader.Resolve(new PdfReference(number));
            var dictionary = value is PdfStreamObject stream ? stream.Dictionary : value as PdfDictionary;
            if (dictionary is null)
            {
                continue;
            }

            foreach (var key in new[] { "SMask", "Group", "BM", "EmbeddedFiles", "AF", "OCProperties", "Interpolate", "AA", "ObjStm" })
            {
                Assert.True(dictionary[key] is null, $"Object {number} has /{key}.");
            }

            foreach (var key in new[] { "CA", "ca" })
            {
                Assert.True(dictionary[key] is null || Number(dictionary[key]!) == 1, $"Object {number} has /{key} {dictionary[key]}.");
            }

            if ((dictionary["Subtype"] as PdfName)?.Value == "Image")
            {
                images++;
                var bits = dictionary["BitsPerComponent"] is PdfInteger { } bpc ? bpc.Value : 1; // A stencil mask has 1 bit.
                Assert.True(dictionary["BitsPerComponent"] is not null || dictionary["ImageMask"] == PdfBoolean.True);
                Assert.Contains(bits, new long[] { 1, 2, 4, 8 });
            }

            Assert.NotEqual("OpenType", (dictionary["Subtype"] as PdfName)?.Value);
            if ((dictionary["Type"] as PdfName)?.Value == "FontDescriptor" && ((PdfName)dictionary["FontName"]!).Value.Contains('+', StringComparison.Ordinal)
                && dictionary["FontFile"] is null && (dictionary["FontFile2"] is not null || dictionary["FontFile3"] is not null))
            {
                subsets++;
            }
        }

        Assert.True(images >= (kind == "layout" ? 1 : 3), $"Only {images} images.");
        Assert.True(subsets >= 1);
        var metadata = (PdfStreamObject)reader.Resolve(reader.Catalog["Metadata"])!;
        Assert.Null(metadata.Dictionary["Filter"]);
        Assert.Equal(level.IsLevelA(), reader.Catalog["StructTreeRoot"] is not null);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_CID_font_subset_has_a_CIDSet_of_its_glyphs(string kind, PdfAConformance level)
    {
        var reader = PdfReader.Open(Sample(kind, level).ToArray());
        var checkedFonts = 0;
        foreach (var number in reader.Store.ObjectNumbers)
        {
            if (reader.Resolve(new PdfReference(number)) is not PdfDictionary { } font || (font["Subtype"] as PdfName)?.Value is not ("CIDFontType0" or "CIDFontType2"))
            {
                continue;
            }

            var descriptor = (PdfDictionary)reader.Resolve(font["FontDescriptor"])!;
            var bits = reader.Store.Decode((PdfStreamObject)reader.Resolve(descriptor["CIDSet"])!);
            var set = Enumerable.Range(0, bits.Length * 8).Where(cid => (bits[cid / 8] & (0x80 >> (cid % 8))) != 0).ToHashSet();
            Assert.Equal([0, .. GlyphsOf((PdfArray)reader.Resolve(font["W"])!)], set.Order());
            checkedFonts++;
        }

        Assert.True(checkedFonts >= (kind == "canvas" ? 3 : 1));
    }

    [Fact]
    public void Only_PdfA1_writes_a_CIDSet()
    {
        foreach (var level in new[] { PdfAConformance.None, PdfAConformance.PdfA2B })
        {
            var pdf = Encoding.Latin1.GetString(new PdfDocument { PdfAConformance = level }.EmbedStandardFonts().AddPageWithFont(Sans).ToArray());
            Assert.DoesNotContain("/CIDSet", pdf, StringComparison.Ordinal);
        }

        Assert.Contains("/CIDSet", Encoding.Latin1.GetString(Document().ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void A_font_that_is_not_subset_has_no_CIDSet()
    {
        var data = Fixtures.Read("LiberationSans-Regular.ttf");
        FontEmbeddingTests.SetFsType(data, 0x0100); // No subsetting.
        var doc = new PdfDocument { PdfAConformance = PdfAConformance.PdfA1B };
        doc.AddPage().Canvas.DrawText("Whole", PdfFont.Load(data), 12, 72, 720);

        Assert.DoesNotContain("/CIDSet", Encoding.Latin1.GetString(doc.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void A_CFF_font_embedded_whole_as_OpenType_is_rejected()
    {
        var data = Fixtures.Read("RaiCoreSans3-Subset.otf");
        FontEmbeddingTests.SetFsType(data, 0x0100); // No subsetting: embedded whole, as /OpenType (PDF 1.6).
        var font = PdfFont.Load(data);
        PdfDocument Make(PdfAConformance level)
        {
            var doc = new PdfDocument { PdfAConformance = level };
            doc.AddPage().Canvas.DrawText("Whole", font, 12, 72, 720);
            return doc;
        }

        var message = Message(Make(PdfAConformance.PdfA1B));
        Assert.Contains("PDF/A-1 (PDF 1.4) does not allow OpenType font programs", message, StringComparison.Ordinal);
        Assert.Contains("the CFF font 'RaiCore", message, StringComparison.Ordinal);
        Assert.Contains("/OpenType", Encoding.Latin1.GetString(Make(PdfAConformance.PdfA2B).ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1B)]
    [InlineData(PdfAConformance.PdfA1A)]
    public void Translucent_colors_are_rejected(PdfAConformance level)
    {
        var fill = Document(level);
        fill.Pages[0].Canvas.SetFillColor(new PdfColor(1, 0, 0, 0.5)).Rectangle(72, 600, 100, 50).Fill();
        var message = Message(fill);
        Assert.Contains("PDF/A-1 does not allow transparency, but page 1 draws a translucent color (an alpha below 1", message, StringComparison.Ordinal);
        Assert.Contains("use PDF/A-2 or PDF/A-3", message, StringComparison.Ordinal);

        var stroke = Document(level);
        stroke.Pages[0].Canvas.SetStrokeColor(new PdfColor(1, 0, 0, 0.99)).Rectangle(72, 600, 100, 50).Stroke();
        Assert.Contains("draws a translucent color", Message(stroke), StringComparison.Ordinal);
    }

    [Fact]
    public void Opaque_colors_are_accepted_after_a_translucent_one_only_without_PdfA1()
    {
        var doc = Document(PdfAConformance.PdfA2B);
        doc.Pages[0].Canvas.SetFillColor(new PdfColor(1, 0, 0, 0.5)).Rectangle(72, 600, 100, 50).Fill().SetFillColor(PdfColor.Black);
        doc.ToArray();

        var opaque = Document();
        opaque.Pages[0].Canvas.SetFillColor(new PdfColor(1, 0, 0)).Rectangle(72, 600, 100, 50).Fill().SetStrokeColor(new PdfColor(0, 0, 1)).Stroke();
        opaque.ToArray();
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    public void Transparency_groups_are_rejected_whatever_their_opacity(double opacity)
    {
        var doc = Document();
        doc.Pages[0].Canvas.BeginGroup(opacity).Rectangle(72, 600, 100, 50).Fill().EndGroup();

        Assert.Contains("page 1 draws a transparency group (PdfCanvas.BeginGroup", Message(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void The_message_names_the_page_and_pages_written_one_by_one_are_checked()
    {
        var doc = Document();
        doc.AddPage().Canvas.SetFillColor(new PdfColor(0, 0, 0, 0.3)).Rectangle(72, 600, 100, 50).Fill();
        Assert.Contains("but page 2 draws", Message(doc), StringComparison.Ordinal);

        var streamed = Document();
        streamed.BeginSave(Stream.Null);
        streamed.FlushPages();
        streamed.AddPage().Canvas.BeginGroup(0.5).EndGroup();
        Assert.Contains("but page 2 draws a transparency group", Message(streamed.FlushPages), StringComparison.Ordinal);
    }

    [Fact]
    public void A_PdfA1_document_written_page_by_page_is_the_saved_file()
    {
        var saved = PdfASamples.Opaque(PdfAConformance.PdfA1A).ToArray();
        var streamed = PdfASamples.Opaque(PdfAConformance.PdfA1A);
        using var output = new MemoryStream();
        streamed.BeginSave(output);
        streamed.FlushPages();
        streamed.EndSave();

        Assert.True(saved.AsSpan().SequenceEqual(output.ToArray()));
        Assert.StartsWith("%PDF-1.4", Encoding.Latin1.GetString(saved), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<g opacity="0.5"><rect width="10" height="10"/></g>""", "an SVG image with a transparency group")]
    [InlineData("""<rect width="10" height="10" fill="red" fill-opacity="0.5"/>""", "an SVG image with a translucent color")]
    [InlineData("""<image width="10" height="10" href="data:image/svg+xml;base64,PHN2ZyB4bWxucz0naHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmcnIHdpZHRoPScxMCcgaGVpZ2h0PScxMCc+PHJlY3Qgd2lkdGg9JzEwJyBoZWlnaHQ9JzEwJyBvcGFjaXR5PScwLjUnLz48L3N2Zz4="/>""", "an SVG image with an SVG image with a translucent color")]
    public void Svg_images_with_transparency_are_rejected(string content, string what)
    {
        var doc = Document();
        doc.Pages[0].Canvas.DrawImage(PdfImage.LoadSvg($"""<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10">{content}</svg>"""), 72, 600, 50, 50);

        Assert.Contains($"page 1 draws {what}", Message(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void An_opaque_svg_image_is_accepted()
    {
        var doc = Document();
        doc.Pages[0].Canvas.DrawImage(PdfImage.LoadSvg(PdfASamples.OpaqueSvg), 72, 500, 200, 100);
        doc.ToArray();
    }

    [Fact]
    public void Images_with_semi_transparent_pixels_are_rejected()
    {
        var doc = Document();
        doc.Pages[0].Canvas.DrawImage(PdfImage.Load(Fixtures.Path("PngSuite/basn6a08.png")), 72, 600, 32, 32);

        Assert.Equal(
            "PDF/A-1 does not allow transparency, but an image has semi-transparent pixels (an alpha channel). Flatten it onto an opaque background, or use PDF/A-2 or PDF/A-3.",
            Message(doc));

        var svg = Document();
        var png = Convert.ToBase64String(Fixtures.Read("PngSuite/basn6a08.png"));
        svg.Pages[0].Canvas.DrawImage(PdfImage.LoadSvg($"""<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><image width="10" height="10" href="data:image/png;base64,{png}"/></svg>"""), 72, 600, 32, 32);
        Assert.Contains("semi-transparent pixels", Message(svg), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Images/transparent.gif")]
    [InlineData("PngSuite/tbbn3p08.png")]
    public void Pixels_that_are_fully_transparent_or_opaque_become_a_stencil_mask(string file)
    {
        var image = PdfImage.Load(Fixtures.Path(file));
        var alpha = image.Image.Alpha!;
        var doc = Document();
        doc.Pages[0].Canvas.DrawImage(image, 72, 600, 64, 64);
        var reader = PdfReader.Open(doc.ToArray());

        var xobject = ((PdfStreamObject)reader.Resolve(((PdfDictionary)reader.Resolve(reader.Pages[0].Resources["XObject"])!).Entries.Single().Value)!).Dictionary;
        Assert.Null(xobject["SMask"]);
        var mask = (PdfStreamObject)reader.Resolve(xobject["Mask"])!;
        Assert.Equal(PdfBoolean.True, mask.Dictionary["ImageMask"]);
        Assert.Equal(image.Image.Width, ((PdfInteger)mask.Dictionary["Width"]!).Value);
        Assert.Equal(image.Image.Height, ((PdfInteger)mask.Dictionary["Height"]!).Value);
        Assert.Null(mask.Dictionary["Decode"]);

        // A set bit masks the pixel out: exactly the transparent ones. Rows start on a byte.
        var bits = reader.Store.Decode(mask);
        var rowBytes = (image.Image.Width + 7) / 8;
        Assert.Equal(rowBytes * image.Image.Height, bits.Length);
        for (var i = 0; i < alpha.Length; i++)
        {
            var (row, column) = Math.DivRem(i, image.Image.Width);
            var masked = (bits[(row * rowBytes) + (column / 8)] & (0x80 >> (column % 8))) != 0;
            Assert.True(masked == (alpha[i] == 0), $"Pixel {i}");
        }

        // PDF/A-2 keeps the soft mask.
        var pdfA2 = Document(PdfAConformance.PdfA2B);
        pdfA2.Pages[0].Canvas.DrawImage(image, 72, 600, 64, 64);
        var pdf = Encoding.Latin1.GetString(pdfA2.ToArray());
        Assert.Contains("/SMask", pdf, StringComparison.Ordinal);
        Assert.DoesNotContain("/ImageMask", pdf, StringComparison.Ordinal);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void A_stencil_mask_looks_like_the_soft_mask()
    {
        byte[] Render(PdfAConformance level)
        {
            var doc = new PdfDocument { PdfAConformance = level };
            var canvas = doc.AddPage(new PdfPageSize(200, 100)).Canvas;
            canvas.SetFillColor(new PdfColor(0, 0.5, 0)).Rectangle(0, 0, 200, 100).Fill(); // Shows through transparent pixels.
            canvas.DrawImage(PdfImage.Load(Fixtures.Path("Images/transparent.gif")), 0, 0, 100, 100);
            canvas.DrawImage(PdfImage.Load(Fixtures.Path("PngSuite/tbbn3p08.png")), 100, 0, 100, 100);
            using var bitmap = Conversion.ToImage(doc.ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
            return [.. Enumerable.Range(0, bitmap.Height).SelectMany(y => Enumerable.Range(0, bitmap.Width).SelectMany(x =>
            {
                var pixel = bitmap.GetPixel(x, y);
                return new[] { pixel.Red, pixel.Green, pixel.Blue };
            }))];
        }

        var stencil = Render(PdfAConformance.PdfA1B);
        var soft = Render(PdfAConformance.PdfA2B);

        Assert.Equal(soft.Length, stencil.Length);
        Assert.True(stencil.Zip(soft).Count(pair => Math.Abs(pair.First - pair.Second) > 8) < stencil.Length / 100);
        Assert.Contains(Enumerable.Range(0, stencil.Length / 3), p => stencil[(p * 3) + 1] > 100 && stencil[p * 3] < 50); // The green shows through.
    }

    [Fact]
    public void Images_with_16_bit_samples_are_written_with_8()
    {
        var image = PdfImage.Load(Fixtures.Path("PngSuite/tbbn2c16.png"));
        Assert.Equal(16, image.Image.BitsPerComponent);
        var key = image.Image.ColorKey!;

        var doc = Document();
        doc.Pages[0].Canvas.DrawImage(image, 72, 600, 64, 64).DrawImage(PdfImage.Load(Fixtures.Path("PngSuite/basn0g16.png")), 150, 600, 64, 64);
        var reader = PdfReader.Open(doc.ToArray());
        var xobjects = ((PdfDictionary)reader.Resolve(reader.Pages[0].Resources["XObject"])!).Entries.Select(entry => (PdfStreamObject)reader.Resolve(entry.Value)!).ToList();

        var rgb = xobjects[0];
        Assert.Equal(8, ((PdfInteger)rgb.Dictionary["BitsPerComponent"]!).Value);
        var data = reader.Store.Decode(rgb);
        Assert.Equal(image.Image.Width * image.Image.Height * 3, data.Length);
        Assert.Equal(image.Image.Data.Where((_, i) => i % 2 == 0), data);
        Assert.Equal(key.Select(k => (double)(k >> 8)), ((PdfArray)rgb.Dictionary["Mask"]!).Items.Select(Number));

        var gray = xobjects[1];
        Assert.Equal(8, ((PdfInteger)gray.Dictionary["BitsPerComponent"]!).Value);
        Assert.Equal(32 * 32, reader.Store.Decode(gray).Length);

        // Other levels keep the 16 bits.
        var pdfA2 = Document(PdfAConformance.PdfA2B);
        pdfA2.Pages[0].Canvas.DrawImage(image, 72, 600, 64, 64);
        Assert.Contains("/BitsPerComponent 16", Encoding.Latin1.GetString(pdfA2.ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1B)]
    [InlineData(PdfAConformance.PdfA1A)]
    public void Attachments_are_rejected(PdfAConformance level)
    {
        var doc = Document(level);
        doc.Attachments.Add(new PdfAttachment("notes.txt", "Notes"u8));

        Assert.Equal(
            "PDF/A-1 does not allow attached files: use PDF/A-3 (PdfAConformance.PdfA3B or PdfA3A), which allows any file.",
            Message(doc));
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1B, "PDF/A-1")]
    [InlineData(PdfAConformance.PdfA1A, "PDF/A-1")]
    [InlineData(PdfAConformance.PdfA2B, "PDF/A-2")]
    public void A_FacturX_invoice_cannot_be_PdfA1(PdfAConformance level, string part)
    {
        var doc = Document(level);
        var message = Assert.Throws<InvalidOperationException>(() => doc.AttachFacturXInvoice(PdfAttachmentTests.InvoiceXml, FacturXProfile.EN16931)).Message;

        Assert.Equal($"A Factur-X / ZUGFeRD invoice is a PDF/A-3 document, but this one is set to {part}.", message);
        Assert.Empty(doc.Attachments);
        Assert.Equal(level, doc.PdfAConformance);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1A, "Document(Table(TR(TH TH) TR(TD TD) TR(TD TD)))")]
    [InlineData(PdfAConformance.PdfA2A, "Document(Table(THead(TR(TH TH)) TBody(TR(TD TD)) TFoot(TR(TD TD))))")]
    public void PdfA1_leaves_out_the_row_groups_of_tables(PdfAConformance level, string tree)
    {
        var doc = Document(level, text: false);
        var table = doc.StructureRoot.AddChild(PdfStructureTypes.Table);
        var canvas = doc.Pages[0].Canvas;
        var y = 700;
        foreach (var (group, cell) in new[] { (PdfStructureTypes.TableHead, PdfStructureTypes.TableHeaderCell), (PdfStructureTypes.TableBody, PdfStructureTypes.TableDataCell), (PdfStructureTypes.TableFoot, PdfStructureTypes.TableDataCell) })
        {
            var row = table.AddChild(group).AddChild(PdfStructureTypes.TableRow);
            foreach (var text in new[] { "a", "b" })
            {
                canvas.BeginTag(row.AddChild(cell)).DrawText(text, Sans, 12, 72, y -= 20).EndTag();
            }
        }

        var bytes = doc.ToArray();
        var root = TaggedPdfInspector.Inspect(bytes);

        Assert.Equal(tree, root.ToString());
        Assert.Equal(level != PdfAConformance.PdfA1A, Encoding.Latin1.GetString(bytes).Contains("/S /THead", StringComparison.Ordinal)); // Not even unreferenced.
        var cells = new List<string>();
        void Collect(InspectedElement element)
        {
            if (element.Type is "TH" or "TD")
            {
                cells.Add(element.Text);
            }

            element.Children.ForEach(Collect);
        }

        Collect(root);
        Assert.Equal(["a", "b", "a", "b", "a", "b"], cells);
    }

    [Fact]
    public void A_row_group_with_content_of_its_own_and_other_PDF_1_5_types_are_rejected_at_level_A()
    {
        foreach (var type in new[] { PdfStructureTypes.TableHead, PdfStructureTypes.Annotation, "Ruby", "Warichu" })
        {
            var doc = Document(PdfAConformance.PdfA1A, text: false);
            doc.Pages[0].Canvas.BeginTag(doc.StructureRoot.AddChild(type)).DrawText("Own", Sans, 12, 72, 700).EndTag();

            Assert.Equal(
                $"PDF/A-1 level A requires the standard structure types of PDF 1.4, but an element has the type \"{type}\", which PDF 1.5 added, and no role map is written.",
                Message(doc));

            var pdfA2 = Document(PdfAConformance.PdfA2A, text: false);
            pdfA2.Pages[0].Canvas.BeginTag(pdfA2.StructureRoot.AddChild(type)).DrawText("Own", Sans, 12, 72, 700).EndTag();
            pdfA2.ToArray();
        }
    }

    [Fact]
    public void A_tagged_PdfA1b_document_leaves_out_row_groups_too()
    {
        var doc = PdfASamples.HtmlOpaque(PdfAConformance.PdfA1B, pdfUA: true);
        var tree = TaggedPdfInspector.Inspect(doc.ToArray()).ToString();

        Assert.Contains("Table(TR(", tree, StringComparison.Ordinal);
        Assert.DoesNotContain("THead", tree, StringComparison.Ordinal);
        Assert.DoesNotContain("TBody", tree, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("es-419")]
    [InlineData("en-GB-1994")]
    public void Level_A_languages_of_PdfA1_have_letters_only(string language)
    {
        var message = Message(Document(PdfAConformance.PdfA1A).WithLanguage(language));
        Assert.Equal(
            $"The document's language (PdfDocument.Language) is \"{language}\", which is not a language tag of letters such as \"en\" or \"de-CH\" (RFC 1766), as PDF/A-1 level A requires.",
            message);

        var element = Document(PdfAConformance.PdfA1A, text: false);
        var paragraph = element.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        paragraph.Language = language;
        element.Pages[0].Canvas.BeginTag(paragraph).DrawText("Hola", Sans, 12, 72, 700).EndTag();
        Assert.Contains("The language of a P element is", Message(element), StringComparison.Ordinal);

        // PDF/A-2a takes BCP 47 tags, and level B does not check.
        Document(PdfAConformance.PdfA2A).WithLanguage(language).ToArray();
        Document(PdfAConformance.PdfA1B).WithLanguage(language).ToArray();
    }

    [Fact]
    public void Html_without_transparency_converts_to_PdfA1a_and_with_it_fails_naming_the_feature()
    {
        var doc = PdfASamples.HtmlOpaque(PdfAConformance.PdfA1A);
        Assert.True(doc.Tagged);
        doc.ToArray();

        foreach (var (css, what) in new[]
        {
            ("opacity: 0.5", "a transparency group"),
            ("background: rgba(255, 0, 0, 0.25)", "a translucent color"),
            ("color: rgba(0, 0, 0, 0.5)", "a translucent color"),
            ("border: 2px solid rgba(0, 0, 0, 0.5)", "a translucent color"),
            ("border: 2px dashed rgba(0, 0, 0, 0.5)", "a transparency group"),
            ("background: linear-gradient(to right, red, rgba(0, 0, 255, 0.3))", "a gradient with translucent colors"),
        })
        {
            var html = HtmlToPdf.Convert($"""<html lang="en"><body><div style="{css}; height: 20px">Text</div></body></html>""", new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA1A });
            Assert.Contains($"PDF/A-1 does not allow transparency, but page 1 draws {what}", Message(html), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Transparent_html_images_fail_and_GIFs_with_a_transparent_color_do_not()
    {
        string Html(string file, string mime) => $"""<html lang="en"><body><img src="data:{mime};base64,{Convert.ToBase64String(Fixtures.Read(file))}" alt="Image"></body></html>""";
        var options = new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA1B };

        Assert.Contains("semi-transparent pixels", Message(HtmlToPdf.Convert(Html("PngSuite/basn6a08.png", "image/png"), options)), StringComparison.Ordinal);
        HtmlToPdf.Convert(Html("Images/transparent.gif", "image/gif"), options).ToArray();
    }

    /// <summary>The glyph IDs a CIDFont's <c>/W</c> array gives widths for (the form <c>[first [w1 w2 ...] ...]</c>).</summary>
    private static IEnumerable<int> GlyphsOf(PdfArray widths)
    {
        for (var i = 0; i + 1 < widths.Items.Count; i += 2)
        {
            var first = (int)((PdfInteger)widths.Items[i]).Value;
            for (var j = 0; j < ((PdfArray)widths.Items[i + 1]).Items.Count; j++)
            {
                yield return first + j;
            }
        }
    }

    private static double Number(PdfObject value) => value is PdfInteger i ? i.Value : ((PdfReal)value).Value;
}

internal static class PdfA1TestExtensions
{
    public static PdfDocument AddPageWithFont(this PdfDocument doc, PdfFont font)
    {
        doc.AddPage().Canvas.DrawText("Archived", font, 12, 72, 720);
        return doc;
    }

    public static PdfDocument WithLanguage(this PdfDocument doc, string language)
    {
        doc.Language = language;
        return doc;
    }
}
