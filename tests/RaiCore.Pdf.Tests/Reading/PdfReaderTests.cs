using System.Text;
using RaiCore.Pdf.Filters;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Tests.Reading;

/// <summary>
/// Parsing existing files: each file form (classic, cross-reference and object streams, incremental updates, broken
/// cross-reference data), the page tree with inherited attributes, stream filters and encrypted input.
/// </summary>
/// <remarks>
/// The fixtures in Fixtures/Reader are small hand-written files: <c>xref-stream.pdf</c> was rewritten by qpdf
/// (pikepdf) with object streams and a cross-reference stream with a PNG predictor; <c>incremental.pdf</c> has an update
/// that replaces a page's content and adds a page; <c>broken-xref.pdf</c> has every offset 10 bytes off and a startxref
/// that points into an object; <c>hybrid.pdf</c> has a classic table and a cross-reference stream (<c>/XRefStm</c>)
/// for an object stream; <c>encrypted.pdf</c> is AES-128 encrypted with an empty user password.
/// </remarks>
public class PdfReaderTests
{
    [Fact]
    public void Reads_a_file_written_by_the_library()
    {
        var doc = new PdfDocument();
        doc.AddPage(PdfPageSize.A4).Canvas.SetFillColor(new PdfColor(1, 0, 0)).Rectangle(10, 20, 30, 40).Fill();
        doc.AddPage(PdfPageSize.Letter).Canvas.DrawText("Second", StandardFont.Helvetica, 12, 72, 700);

        var reader = PdfReader.Open(doc.ToArray());

        Assert.False(reader.IsRepaired);
        Assert.Equal("1.7", reader.Version);
        Assert.Equal(2, reader.Pages.Count);
        Assert.Equal(new PdfRectangle(0, 0, 595.27559, 841.88976), Round(reader.Pages[0].MediaBox));
        Assert.Equal(reader.Pages[0].MediaBox, reader.Pages[0].CropBox);
        Assert.Equal((612.0, 792.0), (reader.Pages[1].Width, reader.Pages[1].Height));
        Assert.Contains("10 20 30 40 re", TestPdf.Text(reader.Pages[0].GetContent()), StringComparison.Ordinal);
        Assert.Contains("(Second) Tj", TestPdf.Text(reader.Pages[1].GetContent()), StringComparison.Ordinal);
        var font = (PdfDictionary)reader.Resolve(((PdfDictionary)reader.Pages[1].Resources["Font"]!)["F1"])!;
        Assert.Equal("Helvetica", ((PdfName)font["BaseFont"]!).Value);
    }

    [Fact]
    public void Reads_cross_reference_streams_and_object_streams()
    {
        var reader = PdfReader.Open(Fixtures.Path("Reader/xref-stream.pdf"));

        Assert.False(reader.IsRepaired);
        Assert.Equal("1.5", reader.Version);
        Assert.Equal(2, reader.Pages.Count);
        Assert.Contains("(One) Tj", TestPdf.Text(reader.Pages[0].GetContent()), StringComparison.Ordinal);
        Assert.Contains("(Two) Tj", TestPdf.Text(reader.Pages[1].GetContent()), StringComparison.Ordinal);

        // The pages, the page tree and the font are all in the object stream; the media box and resources are inherited.
        Assert.Equal(new PdfRectangle(0, 0, 595, 842), reader.Pages[1].MediaBox);
        var font = (PdfDictionary)reader.Resolve(((PdfDictionary)reader.Pages[1].Resources["Font"]!)["F1"])!;
        Assert.Equal("Helvetica", ((PdfName)font["BaseFont"]!).Value);
        Assert.Contains("/Type /ObjStm", TestPdf.Text(Fixtures.Read("Reader/xref-stream.pdf")), StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_hybrid_files_through_their_cross_reference_stream()
    {
        // The classic table does not list the font: only the cross-reference stream named by /XRefStm does.
        var reader = PdfReader.Open(Fixtures.Path("Reader/hybrid.pdf"));

        var font = (PdfDictionary)reader.Resolve(((PdfDictionary)reader.Pages[0].Resources["Font"]!)["F1"])!;
        Assert.Equal("Helvetica", ((PdfName)font["BaseFont"]!).Value);
        Assert.False(reader.IsRepaired); // Found through /XRefStm, not by scanning the file.
    }

    [Fact]
    public void Incremental_updates_are_followed_and_the_newest_revision_wins()
    {
        var reader = PdfReader.Open(Fixtures.Path("Reader/incremental.pdf"));

        Assert.False(reader.IsRepaired);
        Assert.Equal(2, reader.Pages.Count); // The update added a page.
        Assert.Contains("0 1 0 rg", TestPdf.Text(reader.Pages[0].GetContent()), StringComparison.Ordinal); // Green replaced red.
        Assert.Contains("(Updated)", TestPdf.Text(reader.Pages[1].GetContent()), StringComparison.Ordinal);
        Assert.Equal(new PdfRectangle(0, 0, 300, 300), reader.Pages[1].MediaBox);
    }

    [Fact]
    public void A_broken_cross_reference_table_is_repaired_by_scanning_the_file()
    {
        var reader = PdfReader.Open(Fixtures.Path("Reader/broken-xref.pdf"));

        Assert.True(reader.IsRepaired);
        Assert.Equal(2, reader.Pages.Count);
        Assert.Contains("(Broken) Tj", TestPdf.Text(reader.Pages[0].GetContent()), StringComparison.Ordinal);
        Assert.Contains("0 0 1 rg", TestPdf.Text(reader.Pages[1].GetContent()), StringComparison.Ordinal);
        Assert.Equal(new PdfRectangle(0, 0, 400, 400), reader.Pages[1].MediaBox);
    }

    [Fact]
    public void A_file_without_cross_reference_data_is_repaired()
    {
        var pdf = TestPdf.Text(TestPdf.Pages("/MediaBox [0 0 200 100]", ("", "0 0 1 rg 0 0 10 10 re f")));
        var withoutXref = Encoding.Latin1.GetBytes(pdf[..pdf.IndexOf("xref", StringComparison.Ordinal)]);

        var reader = PdfReader.Open(withoutXref);

        Assert.True(reader.IsRepaired);
        Assert.Single(reader.Pages);
        Assert.Equal(200, reader.Pages[0].Width);
    }

    [Fact]
    public void A_wrong_offset_found_later_triggers_the_repair()
    {
        // The table is right except for the content stream's offset, which only matters when the content is read.
        var pdf = TestPdf.Text(TestPdf.Pages(string.Empty, ("", "1 0 0 rg 0 0 10 10 re f")));
        var contentOffset = pdf.IndexOf("4 0 obj", StringComparison.Ordinal);
        pdf = pdf.Replace($"{contentOffset:D10} 00000 n", $"{contentOffset + 3:D10} 00000 n", StringComparison.Ordinal);

        var reader = PdfReader.Open(Encoding.Latin1.GetBytes(pdf));
        Assert.False(reader.IsRepaired);

        Assert.Contains("1 0 0 rg", TestPdf.Text(reader.Pages[0].GetContent()), StringComparison.Ordinal);
        Assert.True(reader.IsRepaired);
    }

    [Fact]
    public void An_object_missing_from_the_table_triggers_the_repair()
    {
        var pdf = TestPdf.Text(TestPdf.Pages(string.Empty, ("", "0 1 0 rg 0 0 10 10 re f")));
        var contentOffset = pdf.IndexOf("4 0 obj", StringComparison.Ordinal);
        pdf = pdf.Replace($"{contentOffset:D10} 00000 n", "0000000000 00001 f", StringComparison.Ordinal);

        var reader = PdfReader.Open(Encoding.Latin1.GetBytes(pdf));

        Assert.Contains("0 1 0 rg", TestPdf.Text(reader.Pages[0].GetContent()), StringComparison.Ordinal);
        Assert.True(reader.IsRepaired);
    }

    [Fact]
    public void Pages_inherit_resources_boxes_and_rotation_from_the_page_tree()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 3 /MediaBox [0 0 500 800] /Rotate 90 /Resources << /Font << /F1 6 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /CropBox [-10 100 300 900] >>",              // Crop box clipped to the media box.
            "<< /Type /Pages /Parent 2 0 R /Kids [5 0 R] /Count 2 /MediaBox [100 50 400 250] /Rotate -90 /Resources << >> >>",
            "<< /Type /Page /Parent 4 0 R /Rotate 180 /MediaBox [300 400 0 0] /Resources << /XObject << >> >> >>", // Reversed corners.
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ]);

        var reader = PdfReader.Open(pdf);

        Assert.Equal(2, reader.Pages.Count);
        var first = reader.Pages[0];
        Assert.Equal(new PdfRectangle(0, 0, 500, 800), first.MediaBox);
        Assert.Equal(new PdfRectangle(0, 100, 300, 700), first.CropBox);
        Assert.Equal(90, first.Rotate);
        Assert.Equal((700.0, 300.0), (first.Width, first.Height)); // Turned: the crop box's height is the width.
        Assert.NotNull(first.Resources["Font"]);

        var second = reader.Pages[1];
        Assert.Equal(new PdfRectangle(0, 0, 300, 400), second.MediaBox);
        Assert.Equal(180, second.Rotate);
        Assert.Null(second.Resources["Font"]); // The nearest /Resources wins, not a merge.
        Assert.NotNull(second.Resources["XObject"]);
    }

    [Fact]
    public void Negative_rotation_is_normalized_and_invalid_boxes_fall_back()
    {
        var reader = PdfReader.Open(TestPdf.Pages(
            "/MediaBox [0 0 0 0]", ("/Rotate -90", "q Q"), ("/Rotate 45 /CropBox [5000 5000 6000 6000]", "q Q")));

        Assert.Equal(270, reader.Pages[0].Rotate);
        Assert.Equal(new PdfRectangle(0, 0, 612, 792), reader.Pages[0].MediaBox); // An empty media box: US Letter.
        Assert.Equal(0, reader.Pages[1].Rotate);
        Assert.Equal(reader.Pages[1].MediaBox, reader.Pages[1].CropBox); // A crop box outside the media box is ignored.
    }

    [Fact]
    public void Content_split_over_several_streams_is_joined()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /Contents [4 0 R 5 0 R] >>",
            TestPdf.Stream("1 0 0 rg"),
            TestPdf.Stream("78 9C 33 50 30 00 C3 A2 54 85 34 00 0E 69 02 9E>", "/Filter [/ASCIIHexDecode /FlateDecode]"),
        ]);

        var content = TestPdf.Text(PdfReader.Open(pdf).Pages[0].GetContent());

        Assert.StartsWith("1 0 0 rg\n", content, StringComparison.Ordinal);
        Assert.Contains("0 0 0 re f", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Encrypted_files_are_detected()
    {
        var exception = Assert.Throws<NotSupportedException>(() => PdfReader.Open(Fixtures.Path("Reader/encrypted.pdf")));
        Assert.Contains("encrypted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Syntax_is_parsed_leniently_and_exactly()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /Test 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Name /A#20B#e9 /Real 0.000488281 /Neg -.5 /Str (a\\(b\\) \\101\\n\\\r\nc) /Hex <48 65 6C6C 6F7> /Empty / /Null null"
                + " /Arr [1 2.5 /N (s) true false [nested] << /K 1 >>] /Ref 3 0 R /Bad 0 0 R % a comment\n /Last 1 >>",
        ]);

        var reader = PdfReader.Open(pdf);
        var test = (PdfDictionary)reader.Resolve(reader.Catalog["Test"])!;

        Assert.Equal("/A#20B#E9", test["Name"]!.ToString()); // Bytes kept, even when they are not UTF-8.
        Assert.Equal("0.000488281", test["Real"]!.ToString()); // Written back without rounding.
        Assert.Equal(-0.5, ((PdfReal)test["Neg"]!).Value);
        Assert.Equal("a(b) A\nc", Encoding.Latin1.GetString(((PdfString)test["Str"]!).Bytes));
        Assert.Equal("Hellop", Encoding.Latin1.GetString(((PdfString)test["Hex"]!).Bytes));
        Assert.Equal(string.Empty, ((PdfName)test["Empty"]!).Value);
        Assert.Null(test["Null"]); // A null value is no entry.
        Assert.Equal("[1 2.5 /N (s) true false [] <</K 1>>]", test["Arr"]!.ToString()); // Unknown keywords are skipped.
        Assert.Same(reader.Pages[0].Dictionary, reader.Resolve(test["Ref"]));
        Assert.IsType<PdfNull>(test["Bad"] ?? PdfNull.Instance); // A reference to object 0 is a reference to nothing.
        Assert.Equal(1, ((PdfInteger)test["Last"]!).Value);
    }

    [Fact]
    public void Stream_filters_decode()
    {
        Assert.Equal("Hello, PDF reader!", Decode("ASCII85Decode", "87cURD_*#-6q/;>ART+\\EZk~>"));
        Assert.Equal("\0\0\0\0abc", Decode("ASCII85Decode", "z@:E^~>"));
        Assert.Equal("Hi!", Decode("ASCIIHexDecode", "48 69\n21>"));
        Assert.Equal("Hi ", Decode("ASCIIHexDecode", "4869 2>")); // An odd final digit is padded with 0.
        Assert.Equal("aaaabcd", Encoding.Latin1.GetString(StreamDecoder.RunLength([253, (byte)'a', 2, (byte)'b', (byte)'c', (byte)'d', 128])));

        // The LZW example of ISO 32000-2, 7.4.4.2, and a longer one written by libtiff (whose codes grow to 12 bits one
        // code early, as /EarlyChange 1 says).
        Assert.Equal("-----A---B", Encoding.Latin1.GetString(StreamDecoder.Lzw([0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01], 1)));
        Assert.Equal(Fixtures.Read("Reader/lzw-decoded.bin"), StreamDecoder.Lzw(Fixtures.Read("Reader/lzw-encoded.bin"), 1));

        // PNG predictors: Up (2) on rows of 3 bytes.
        var predicted = new byte[] { 2, 1, 2, 3, 2, 1, 1, 1 };
        var stream = new PdfStreamObject(Flate.Encode(predicted));
        stream.Dictionary["Filter"] = new PdfName("FlateDecode");
        stream.Dictionary["DecodeParms"] = new PdfDictionary { ["Predictor"] = new PdfInteger(12), ["Columns"] = new PdfInteger(3) };
        Assert.Equal([1, 2, 3, 2, 3, 4], StreamDecoder.Decode(stream, o => o));

        // Damaged Flate data decodes as far as it goes.
        var flate = Flate.Encode(Encoding.ASCII.GetBytes(new string('x', 5000) + "the end"));
        var truncated = new PdfStreamObject(flate[..(flate.Length - 6)]);
        truncated.Dictionary["Filter"] = new PdfName("FlateDecode");
        Assert.StartsWith("xxxx", Encoding.ASCII.GetString(StreamDecoder.Decode(truncated, o => o)), StringComparison.Ordinal);

        var image = new PdfStreamObject([1, 2, 3]);
        image.Dictionary["Filter"] = new PdfName("DCTDecode");
        Assert.Throws<PdfFormatException>(() => StreamDecoder.Decode(image, o => o));

        static string Decode(string filter, string data)
        {
            var stream = new PdfStreamObject(Encoding.ASCII.GetBytes(data));
            stream.Dictionary["Filter"] = new PdfName(filter);
            return Encoding.Latin1.GetString(StreamDecoder.Decode(stream, o => o));
        }
    }

    [Fact]
    public void Named_destinations_resolve_through_the_name_tree_and_the_dests_dictionary()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 4 0 R >> /Dests << /Old [3 0 R /Fit] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Kids [5 0 R 4 0 R] >>", // Loops back to itself.
            "<< /Limits [(a) (z)] /Names [(chapter) << /D [3 0 R /XYZ 0 700 null] >> (x) [3 0 R /FitH 5]] >>",
        ]);

        var reader = PdfReader.Open(pdf);

        Assert.Equal("/XYZ", reader.ResolveDestination(new PdfString("chapter"u8.ToArray()))!.Items[1].ToString());
        Assert.Equal("/FitH", reader.ResolveDestination(new PdfString("x"u8.ToArray()))!.Items[1].ToString());
        Assert.Equal("/Fit", reader.ResolveDestination(new PdfName("Old"))!.Items[1].ToString());
        Assert.Null(reader.ResolveDestination(new PdfString("missing"u8.ToArray())));
        Assert.Same(reader.Pages[0], reader.PageOf(reader.ResolveDestination(new PdfString("x"u8.ToArray()))!.Items[0]));
    }

    private static PdfRectangle Round(PdfRectangle r) =>
        new(Math.Round(r.X, 5), Math.Round(r.Y, 5), Math.Round(r.Width, 5), Math.Round(r.Height, 5));
}
