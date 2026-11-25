using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using RaiCore.Pdf.Cfb;
using RaiCore.Pdf.Email;
using RaiCore.Pdf.Email.Msg;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Tests.Email;

/// <summary>
/// Attachments that Outlook keeps as embedded OLE objects (#293), from Fixtures/Msg/make-ole.py: a packaged file, an
/// Office 2007+ document, a PDF kept whole, a Word 97 document (the storage itself), an object with only its picture,
/// and one with nothing to take out. ole-expected.json holds what the script put in.
/// </summary>
public class MsgOleTests
{
    private static readonly JsonElement Expected = JsonDocument.Parse(Fixtures.Read("Msg/ole-expected.json")).RootElement;

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static JsonElement ExpectedFor(string name) =>
        Expected.GetProperty("attachments").EnumerateArray().Single(a => a.GetProperty("name").GetString() == name);

    [Fact]
    public void The_file_inside_each_object_becomes_the_attachment()
    {
        var message = MsgMessage.Read(Fixtures.Read("Msg/ole.msg"), ResourceLimits.Default);

        Assert.Equal(
            ["budget.txt text/plain", "Report.docx application/vnd.openxmlformats-officedocument.wordprocessingml.document", "Scan.pdf application/pdf",
             "Letter.doc application/msword", "Sheet.xls application/vnd.ms-excel", "Slides.ppt application/vnd.ms-powerpoint", "Drawing.bmp image/bmp"],
            message.Attachments.Where(a => a.Message is null).Select(a => $"{a.FileName} {a.MimeType}"));
        foreach (var name in new[] { "budget.txt", "Report.docx", "Scan.pdf" })
        {
            var data = message.Attachments.Single(a => a.FileName == name).Data;
            Assert.Equal(ExpectedFor(name).GetProperty("sha256").GetString(), Sha256(data));
            Assert.Equal(ExpectedFor(name).GetProperty("size").GetInt32(), data.Length);
        }

        var bitmap = message.Attachments.Single(a => a.FileName == "Drawing.bmp").Data;
        Assert.Equal("BM"u8.ToArray(), bitmap[..2]);
        Assert.Equal(bitmap.Length, BinaryPrimitives.ReadInt32LittleEndian(bitmap.AsSpan(2)));
        Assert.Equal(14 + 40, BinaryPrimitives.ReadInt32LittleEndian(bitmap.AsSpan(10))); // 24-bit: no palette.
        Assert.Equal(ExpectedFor("Drawing.bmp").GetProperty("dib").GetProperty("sha256").GetString(), Sha256(bitmap[14..]));
        Assert.Equal(3, PdfImage.Load(bitmap).PixelWidth); // A bitmap the core reads.

        // The object left out, and the one in the attached message, reported by their attachment names.
        Assert.Equal(["Equation", "Inner equation"], message.Problems.Select(p => p.FileName));
        Assert.All(message.Problems, p => Assert.Contains("Microsoft Equation 3.0", p.Message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Letter.doc")]
    [InlineData("Sheet.xls")]
    [InlineData("Slides.ppt")]
    public void An_office_97_object_is_written_out_as_a_compound_file_with_every_stream(string name)
    {
        var message = MsgMessage.Read(Fixtures.Read("Msg/ole.msg"), ResourceLimits.Default);
        var letter = message.Attachments.Single(a => a.FileName == name).Data;

        var file = CompoundFile.Open(letter);
        var streams = new Dictionary<string, string>();
        void Walk(CfbEntry storage, string prefix)
        {
            foreach (var child in storage.Children)
            {
                if (child.Kind == CfbEntryKind.Stream)
                {
                    streams[prefix + child.Name] = Sha256(file.Read(child));
                }
                else
                {
                    Walk(child, prefix + child.Name + "/");
                }
            }
        }

        Walk(file.Root, "");
        var expected = ExpectedFor(name).GetProperty("streams").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal), streams.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void The_pdf_embeds_and_lists_the_objects_and_reports_the_one_left_out()
    {
        var diagnostics = new List<HtmlDiagnostic>();
        var options = new EmailConverterOptions { Html = new HtmlConverterOptions { Diagnostics = diagnostics.Add } };

        var document = EmailToPdf.Convert(Fixtures.Read("Msg/ole.msg"), options);

        Assert.Equal(["Drawing.bmp", "Inner message.eml", "Letter.doc", "Report.docx", "Scan.pdf", "Sheet.xls", "Slides.ppt", "budget.txt"],
            document.Attachments.Select(a => a.FileName).Order(StringComparer.Ordinal));
        var text = new TextExtractor(PdfReader.Open(document.ToArray())).ExtractText();
        Assert.Contains("Letter.doc", text, StringComparison.Ordinal);
        Assert.Equal(["Equation", "Inner equation"], diagnostics.Where(d => d.Message.Contains("embedded object", StringComparison.Ordinal)).Select(d => d.Subject));
        Assert.All(diagnostics.Where(d => d.Subject.Contains("quation", StringComparison.Ordinal)), d => Assert.Equal(HtmlDiagnosticKind.ResourceNotLoaded, d.Kind));
    }

    [Fact]
    public void Native_data_that_is_not_a_package_is_kept_after_its_size()
    {
        byte[] native = [5, 0, 0, 0, 1, 2, 3, 4, 5, 99];
        Assert.Equal((string.Empty, new byte[] { 1, 2, 3, 4, 5 }), OleObject.Package(native));
        Assert.Null(OleObject.Package([200, 0, 0, 0, 1, 2]));
        Assert.Null(OleObject.Package([1, 2, 3]));

        // A package's file name comes from its source path when the label has no extension.
        var body = new List<byte> { 2, 0 };
        body.AddRange("Budget\0C:\\Users\\ada\\budget-2026.csv\0"u8.ToArray());
        body.AddRange(BitConverter.GetBytes(0x00030000));
        body.AddRange(BitConverter.GetBytes(4));
        body.AddRange("tmp\0"u8.ToArray());
        body.AddRange(BitConverter.GetBytes(3));
        body.AddRange("a;b"u8.ToArray());
        var package = OleObject.Package([.. BitConverter.GetBytes(body.Count), .. body])!.Value;
        Assert.Equal(("budget-2026.csv", "a;b"), (package.FileName, System.Text.Encoding.ASCII.GetString(package.Data)));
    }

    [Theory]
    [InlineData(3u, ".wmf")]
    [InlineData(14u, ".emf")]
    [InlineData(2u, null)] // CF_BITMAP: not a format a file is made of.
    public void A_presentation_gives_its_picture_by_clipboard_format(uint format, string? extension)
    {
        byte[] picture = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] stream = [.. BitConverter.GetBytes(-1), .. BitConverter.GetBytes(format), .. BitConverter.GetBytes(4),
            .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(-1), .. BitConverter.GetBytes(2), .. BitConverter.GetBytes(0),
            .. BitConverter.GetBytes(5080), .. BitConverter.GetBytes(2540), .. BitConverter.GetBytes(picture.Length), .. picture];

        var (actual, data) = OleObject.Presentation(stream);

        Assert.Equal(extension, actual);
        if (extension == ".wmf")
        {
            // The placeable header: key, bounding box 5080 x 2540 HIMETRIC at 2540 a inch (2 x 1 inches), checksum.
            Assert.Equal(0x9AC6CDD7u, BinaryPrimitives.ReadUInt32LittleEndian(data));
            Assert.Equal((5080, 2540, 2540), (BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(10)), BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(12)), (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14))));
            ushort checksum = 0;
            for (var i = 0; i < 20; i += 2)
            {
                checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i));
            }

            Assert.Equal(checksum, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(20)));
            Assert.Equal(picture, data![22..]);
        }
        else if (extension == ".emf")
        {
            Assert.Equal(picture, data);
        }

        Assert.Equal((null, null), OleObject.Presentation([.. stream[..8], 0, 0, 0, 0])); // Cut short.

        // The same with a target device (its size counts itself): skipped to reach the picture.
        byte[] withDevice = [.. stream[..8], .. BitConverter.GetBytes(12), 9, 9, 9, 9, 9, 9, 9, 9, .. stream[12..]];
        var (deviceExtension, deviceData) = OleObject.Presentation(withDevice);
        Assert.Equal(actual, deviceExtension);
        Assert.Equal(data, deviceData);
        Assert.Equal((null, null), OleObject.Presentation([.. BitConverter.GetBytes(5), .. stream[4..]])); // A named format.
    }

    [Fact]
    public void A_paletted_bitmap_points_its_file_header_past_the_palette()
    {
        // An 8-bit DIB of 2 x 1 pixels with 2 colours used: header 40, palette 2 x 4, pixels 4 (rows padded to 4 bytes).
        byte[] dib = [.. BitConverter.GetBytes(40), .. BitConverter.GetBytes(2), .. BitConverter.GetBytes(1), 1, 0, 8, 0,
            .. BitConverter.GetBytes(0), .. BitConverter.GetBytes(4), .. new byte[8], .. BitConverter.GetBytes(2), .. BitConverter.GetBytes(0),
            0, 0, 255, 0, 255, 0, 0, 0, 0, 1, 0, 0];
        byte[] stream = [.. BitConverter.GetBytes(-1), .. BitConverter.GetBytes(8), .. BitConverter.GetBytes(4), .. new byte[16],
            .. BitConverter.GetBytes(52), .. BitConverter.GetBytes(26), .. BitConverter.GetBytes(dib.Length), .. dib];

        var (extension, bitmap) = OleObject.Presentation(stream);

        Assert.Equal(".bmp", extension);
        Assert.Equal(14 + 40 + 8, BinaryPrimitives.ReadInt32LittleEndian(bitmap.AsSpan(10)));
        var image = PdfImage.Load(bitmap!);
        Assert.Equal((2, 1), (image.PixelWidth, image.PixelHeight));
    }

    [Fact]
    public void Written_compound_files_keep_large_small_empty_and_nested_streams()
    {
        // The whole message rewritten: storages in storages, many entries (several directory sectors), empty streams.
        var fixture = CompoundFile.Open(Fixtures.Read("Msg/ole.msg"));
        var source = fixture.Root;
        var written = CompoundFileWriter.Write(fixture, source, ResourceLimits.Default);
        var copy = CompoundFile.Open(written);
        Assert.Equal(Names(fixture, source), Names(copy, copy.Root));

        // A stream on each side of the 4096-byte cutoff: regular sectors and the mini stream.
        var letter = MsgMessage.Read(Fixtures.Read("Msg/ole.msg"), ResourceLimits.Default).Attachments.Single(a => a.FileName == "Letter.doc").Data;
        var letterFile = CompoundFile.Open(letter);
        Assert.Equal(5120, letterFile.Read(letterFile.Root["WordDocument"]!).Length);
        Assert.Equal("table stream"u8.ToArray(), letterFile.Read(letterFile.Root["1Table"]!));

        static List<string> Names(CompoundFile file, CfbEntry storage)
        {
            var names = new List<string>();
            void Walk(CfbEntry entry, string prefix)
            {
                foreach (var child in entry.Children.OrderBy(c => c.Name, StringComparer.Ordinal))
                {
                    names.Add(prefix + child.Name + (child.Kind == CfbEntryKind.Stream ? $" {Convert.ToHexStringLower(SHA256.HashData(file.Read(child)))}" : "/"));
                    if (child.Kind != CfbEntryKind.Stream)
                    {
                        Walk(child, prefix + child.Name + "/");
                    }
                }
            }

            Walk(storage, "");
            return names;
        }
    }

    [Fact]
    public void Embedded_storages_stay_within_the_limits()
    {
        var limits = ResourceLimits.Default with { MaxDecompressedBytes = 4000 };

        Assert.Throws<ResourceLimitExceededException>(() => MsgMessage.Read(Fixtures.Read("Msg/ole.msg"), limits));

        // Every stream of the Word object fits on its own (the largest is 5120 bytes), but not all of them together.
        var letter = MsgMessage.Read(Fixtures.Read("Msg/ole.msg"), ResourceLimits.Default).Attachments.Single(a => a.FileName == "Letter.doc").Data;
        var file = CompoundFile.Open(letter);
        var together = ResourceLimits.Default with { MaxDecompressedBytes = 6000 };
        var error = Assert.Throws<ResourceLimitExceededException>(() => CompoundFileWriter.Write(file, file.Root, together));
        Assert.Contains("together", error.Message, StringComparison.Ordinal);
        Assert.NotEmpty(CompoundFileWriter.Write(file, file.Root, ResourceLimits.Default with { MaxDecompressedBytes = 7000 }));
        Assert.Throws<ResourceLimitExceededException>(() => CompoundFileWriter.Write(file, file.Root, ResourceLimits.Default with { MaxElements = 3 }));
    }
}
