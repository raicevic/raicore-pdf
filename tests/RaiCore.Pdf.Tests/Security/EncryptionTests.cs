using System.Buffers.Binary;
using System.Text;
using PDFtoImage;
using PDFtoImage.Exceptions;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Security;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Security;

internal static class EncryptionSamples
{
    /// <summary>
    /// A document with every kind of string and stream the writer produces: content, an embedded subset font (font file,
    /// ToUnicode map), an image, Unicode metadata, links with URIs, named destinations and an outline.
    /// </summary>
    public static PdfDocument CreateRichSample(PdfEncryption? encryption)
    {
        var doc = PdfDocumentTests.CreateSample();
        doc.Encryption = encryption;
        doc.Info.Author = "Secret Author";
        var font = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
        var page = doc.Pages[0];
        page.Canvas
            .DrawText("Embedded secret text", font, 12, 72, 680)
            .DrawImage(PdfImage.Load(Fixtures.Read(Path.Combine("Images", "transparent.gif"))), 300, 600, 50, 50);
        page.AddLink(72, 760, 200, 24, new Uri("https://example.com/secret-link"));
        page.AddLink(72, 730, 200, 14, "secret-destination");
        doc.NamedDestinations["secret-destination"] = new PdfDestination(doc.Pages[1]);
        doc.Outline.Add(new PdfOutlineItem("Secret bookmark", doc.Pages[1]));
        return doc;
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class EncryptionTests
{
    private const string UserPassword = "user secret";
    private const string OwnerPassword = "owner secret";

    [Theory]
    [InlineData(UserPassword)]
    [InlineData(OwnerPassword)]
    public void Aes256_output_decrypts_to_the_plain_document(string password)
    {
        var plain = EncryptionSamples.CreateRichSample(null).ToArray();
        var encrypted = EncryptionSamples.CreateRichSample(new PdfEncryption { UserPassword = UserPassword, OwnerPassword = OwnerPassword }).ToArray();

        // The key comes from the test's own implementation of the standard, not from the library.
        var key = TestPdfDecryptor.DeriveR6Key(encrypted, password);

        Assert.NotNull(key);
        TestPdfDecryptor.AssertDecryptsTo(plain, encrypted, (data, _) => TestPdfDecryptor.AesDecrypt(key, data));
    }

    [Fact]
    public void Aes256_owner_only_output_decrypts_with_the_empty_password()
    {
        var plain = EncryptionSamples.CreateRichSample(null).ToArray();
        var encrypted = EncryptionSamples.CreateRichSample(new PdfEncryption { OwnerPassword = OwnerPassword, Permissions = PdfPermissions.Print }).ToArray();

        var key = TestPdfDecryptor.DeriveR6Key(encrypted, string.Empty);

        Assert.NotNull(key);
        Assert.Equal(key, TestPdfDecryptor.DeriveR6Key(encrypted, OwnerPassword));
        TestPdfDecryptor.AssertDecryptsTo(plain, encrypted, (data, _) => TestPdfDecryptor.AesDecrypt(key, data));
    }

    [Theory]
    [InlineData(UserPassword)]
    [InlineData(OwnerPassword)]
    public void Aes128_output_decrypts_to_the_plain_document(string password)
    {
        var plain = EncryptionSamples.CreateRichSample(null).ToArray();
        var encrypted = EncryptionSamples.CreateRichSample(new PdfEncryption
        {
            UserPassword = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm = PdfEncryptionAlgorithm.Aes128,
        }).ToArray();

        var handler = StandardSecurityHandler.Authenticate(ReadParameters(encrypted), password);

        Assert.NotNull(handler);
        TestPdfDecryptor.AssertDecryptsTo(plain, encrypted, (data, number) => handler.Decrypt(data, number, 0));
    }

    [Fact]
    public void A_wrong_password_derives_no_key()
    {
        var encrypted = EncryptionSamples.CreateRichSample(new PdfEncryption { UserPassword = UserPassword, OwnerPassword = OwnerPassword }).ToArray();

        Assert.Null(TestPdfDecryptor.DeriveR6Key(encrypted, "wrong"));
        Assert.Null(TestPdfDecryptor.DeriveR6Key(encrypted, string.Empty));
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    public void Every_string_and_stream_is_encrypted(PdfEncryptionAlgorithm algorithm)
    {
        var plain = EncryptionSamples.CreateRichSample(null);
        plain.CompressContent = false;
        var encrypted = EncryptionSamples.CreateRichSample(new PdfEncryption { UserPassword = UserPassword, Algorithm = algorithm });
        encrypted.CompressContent = false;
        var plainBytes = plain.ToArray();
        var bytes = encrypted.ToArray();
        var text = Encoding.Latin1.GetString(bytes);

        // The plain file shows these in clear; the encrypted one must not.
        foreach (var secret in new[] { "Hello from RaiCore.Pdf", "Page 2", "Secret Author", "secret-link", "secret-destination", "Secret bookmark", "RaiCore.Pdf)", "D:2026" })
        {
            Assert.Contains(secret, Encoding.Latin1.GetString(plainBytes), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        var encryptNumber = TestPdfDecryptor.EncryptObjectNumber(bytes);
        var strings = 0;
        var streams = 0;
        foreach (var (number, tokens) in TestPdfDecryptor.ReadObjects(bytes))
        {
            foreach (var token in tokens.Where(t => t.Kind != TokenKind.Other && number != encryptNumber))
            {
                // An IV and at least one padded block.
                Assert.True(token.Bytes.Length >= 32 && token.Bytes.Length % 16 == 0, $"Object {number}: {token}");
                strings += token.Kind == TokenKind.String ? 1 : 0;
                streams += token.Kind == TokenKind.Stream ? 1 : 0;
            }
        }

        // Every string and stream of the plain file is accounted for.
        var plainObjects = TestPdfDecryptor.ReadObjects(plainBytes).Values.SelectMany(t => t).ToList();
        Assert.Equal(plainObjects.Count(t => t.Kind == TokenKind.String), strings);
        Assert.Equal(plainObjects.Count(t => t.Kind == TokenKind.Stream), streams);
        Assert.True(strings >= 8 && streams >= 6, $"{strings} strings, {streams} streams");
    }

    [Fact]
    public void Trailer_has_encrypt_and_a_plain_id_and_the_encrypt_dictionary_is_plain()
    {
        var bytes = EncryptionSamples.CreateRichSample(new PdfEncryption { UserPassword = UserPassword }).ToArray();
        var trailer = TestPdfDecryptor.ReadTrailer(bytes);
        var objects = TestPdfDecryptor.ReadObjects(bytes);

        // /Encrypt is an indirect reference to the last object.
        var encryptNumber = TestPdfDecryptor.EncryptObjectNumber(bytes);
        Assert.Equal(objects.Keys.Max(), encryptNumber);
        var id = trailer.SkipWhile(t => t.Text != "/ID").Skip(1).Where(t => t.Kind == TokenKind.String).Take(2).ToList();
        Assert.Equal(2, id.Count);
        Assert.All(id, s => Assert.Equal(16, s.Bytes.Length));

        var encrypt = objects[encryptNumber];
        Assert.Equal("/Standard", TestPdfDecryptor.ValueOf(encrypt, "/Filter").Text);
        Assert.Equal("5", TestPdfDecryptor.ValueOf(encrypt, "/V").Text);
        Assert.Equal("6", TestPdfDecryptor.ValueOf(encrypt, "/R").Text);
        Assert.Equal("256", TestPdfDecryptor.ValueOf(encrypt, "/Length").Text);
        Assert.Equal("/AESV3", TestPdfDecryptor.ValueOf(encrypt, "/CFM").Text);
        Assert.Equal("/StdCF", TestPdfDecryptor.ValueOf(encrypt, "/StmF").Text);
        Assert.Equal("/StdCF", TestPdfDecryptor.ValueOf(encrypt, "/StrF").Text);
        Assert.Equal(48, TestPdfDecryptor.ValueOf(encrypt, "/O").Bytes.Length);
        Assert.Equal(48, TestPdfDecryptor.ValueOf(encrypt, "/U").Bytes.Length);
        Assert.Equal(32, TestPdfDecryptor.ValueOf(encrypt, "/OE").Bytes.Length);
        Assert.Equal(32, TestPdfDecryptor.ValueOf(encrypt, "/UE").Bytes.Length);
        Assert.Equal(16, TestPdfDecryptor.ValueOf(encrypt, "/Perms").Bytes.Length);
        Assert.DoesNotContain(encrypt, t => t.Text == "/EncryptMetadata");

        // AES-256 in a PDF 1.7 file: Adobe extension level 8.
        var catalog = objects[1];
        Assert.Equal("/1.7", TestPdfDecryptor.ValueOf(catalog, "/BaseVersion").Text);
        Assert.Equal("8", TestPdfDecryptor.ValueOf(catalog, "/ExtensionLevel").Text);
    }

    [Fact]
    public void Aes128_encrypt_dictionary_and_catalog()
    {
        var bytes = EncryptionSamples.CreateRichSample(new PdfEncryption { Algorithm = PdfEncryptionAlgorithm.Aes128 }).ToArray();
        var objects = TestPdfDecryptor.ReadObjects(bytes);
        var encrypt = objects[TestPdfDecryptor.EncryptObjectNumber(bytes)];

        Assert.Equal("4", TestPdfDecryptor.ValueOf(encrypt, "/V").Text);
        Assert.Equal("4", TestPdfDecryptor.ValueOf(encrypt, "/R").Text);
        Assert.Equal("128", TestPdfDecryptor.ValueOf(encrypt, "/Length").Text);
        Assert.Equal("/AESV2", TestPdfDecryptor.ValueOf(encrypt, "/CFM").Text);
        Assert.Equal(32, TestPdfDecryptor.ValueOf(encrypt, "/O").Bytes.Length);
        Assert.DoesNotContain(encrypt, t => t.Text is "/OE" or "/UE" or "/Perms");
        Assert.DoesNotContain(objects[1], t => t.Text == "/Extensions");
    }

    [Theory]
    [InlineData(PdfPermissions.None, -3392)]
    [InlineData(PdfPermissions.All, -4)]
    [InlineData(PdfPermissions.Print | PdfPermissions.PrintHighQuality | PdfPermissions.Copy, -1324)]
    [InlineData(PdfPermissions.FillForms | PdfPermissions.Annotate, -3104)]
    public void P_and_perms_carry_the_permission_bits(PdfPermissions permissions, int p)
    {
        var bytes = EncryptionSamples.CreateRichSample(new PdfEncryption { OwnerPassword = OwnerPassword, Permissions = permissions }).ToArray();
        var encrypt = TestPdfDecryptor.ReadObjects(bytes)[TestPdfDecryptor.EncryptObjectNumber(bytes)];

        Assert.Equal(p.ToString(System.Globalization.CultureInfo.InvariantCulture), TestPdfDecryptor.ValueOf(encrypt, "/P").Text);

        var perms = TestPdfDecryptor.DecryptPerms(bytes, TestPdfDecryptor.DeriveR6Key(bytes, OwnerPassword)!);
        Assert.Equal(p, BinaryPrimitives.ReadInt32LittleEndian(perms));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, perms[4..8]);
        Assert.Equal((byte)'T', perms[8]);
        Assert.Equal("adb"u8.ToArray(), perms[9..12]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_metadata_stream_is_left_plain_only_when_asked(bool encryptMetadata)
    {
        var security = StandardSecurityHandler.Create(new PdfEncryption { EncryptMetadata = encryptMetadata });
        var writer = new PdfFileWriter();
        var catalog = new PdfDictionary { ["Type"] = new PdfName("Catalog") };
        var catalogRef = writer.Add(catalog);
        catalog["Metadata"] = writer.Add(new PdfStreamObject(
            "<x:xmpmeta>xmp-secret</x:xmpmeta>"u8.ToArray(),
            new PdfDictionary { ["Type"] = new PdfName("Metadata"), ["Subtype"] = new PdfName("XML") }));
        writer.Add(new PdfStreamObject("other-secret"u8.ToArray()));
        using var output = new MemoryStream();
        writer.Write(output, catalogRef, null, security: security);
        var text = Encoding.Latin1.GetString(output.ToArray());

        Assert.Equal(!encryptMetadata, text.Contains("xmp-secret", StringComparison.Ordinal));
        Assert.DoesNotContain("other-secret", text, StringComparison.Ordinal);
        Assert.Equal(!encryptMetadata, text.Contains("/EncryptMetadata false", StringComparison.Ordinal));

        var perms = TestPdfDecryptor.DecryptPerms(output.ToArray(), TestPdfDecryptor.DeriveR6Key(output.ToArray(), string.Empty)!);
        Assert.Equal(encryptMetadata ? (byte)'T' : (byte)'F', perms[8]);
    }

    [Fact]
    public void Encrypted_output_differs_every_time()
    {
        var encryption = new PdfEncryption { UserPassword = UserPassword };

        Assert.NotEqual(EncryptionSamples.CreateRichSample(encryption).ToArray(), EncryptionSamples.CreateRichSample(encryption).ToArray());
    }

    [Fact]
    public void Invalid_options_fail_when_saving()
    {
        var doc = PdfDocumentTests.CreateSample();

        doc.Encryption = new PdfEncryption { UserPassword = "bad\u0001password" };
        Assert.Equal("UserPassword", Assert.Throws<ArgumentException>(doc.ToArray).ParamName);

        doc.Encryption = new PdfEncryption { OwnerPassword = "\u20AC", Algorithm = PdfEncryptionAlgorithm.Aes128 };
        Assert.Equal("OwnerPassword", Assert.Throws<ArgumentException>(doc.ToArray).ParamName);

        doc.Encryption = new PdfEncryption { Permissions = PdfPermissions.PrintHighQuality };
        Assert.Throws<ArgumentException>(doc.ToArray);
    }

    [Fact]
    public void Pdfium_opens_an_owner_only_file_without_a_password_and_renders_it_like_the_plain_file()
    {
        var plain = PdfDocumentTests.CreateSample().ToArray();
        var encrypted = PdfDocumentTests.CreateSample();
        encrypted.Encryption = new PdfEncryption { OwnerPassword = OwnerPassword, Permissions = PdfPermissions.Print };

        var bytes = encrypted.ToArray();

        Assert.Equal(2, Conversion.GetPageCount(bytes));
        AssertSameRendering(plain, bytes, password: null);
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256, UserPassword)]
    [InlineData(PdfEncryptionAlgorithm.Aes256, OwnerPassword)]
    [InlineData(PdfEncryptionAlgorithm.Aes128, UserPassword)]
    [InlineData(PdfEncryptionAlgorithm.Aes128, OwnerPassword)]
    public void Pdfium_needs_the_user_password_and_then_renders_like_the_plain_file(PdfEncryptionAlgorithm algorithm, string password)
    {
        var plain = EncryptionSamples.CreateRichSample(null).ToArray();
        var bytes = EncryptionSamples.CreateRichSample(new PdfEncryption { UserPassword = UserPassword, OwnerPassword = OwnerPassword, Algorithm = algorithm }).ToArray();

        Assert.Throws<PdfPasswordProtectedException>(() => Conversion.GetPageCount(bytes));
        Assert.Throws<PdfPasswordProtectedException>(() => Conversion.GetPageCount(bytes, password: "wrong"));
        Assert.Equal(2, Conversion.GetPageCount(bytes, password: password));
        AssertSameRendering(plain, bytes, password);
    }

    private static void AssertSameRendering(byte[] plain, byte[] encrypted, string? password)
    {
        using var expected = Conversion.ToImage(plain, page: 0, options: new RenderOptions(Dpi: 72));
        using var actual = Conversion.ToImage(encrypted, page: 0, password: password, options: new RenderOptions(Dpi: 72));

        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Pixels, actual.Pixels);
        Assert.Contains(expected.Pixels, c => c != SKColors.White); // Not a blank page.
    }

    /// <summary>Reads the encryption dictionary values with the test's own lexer.</summary>
    internal static StandardSecurityParameters ReadParameters(byte[] pdf)
    {
        var encrypt = TestPdfDecryptor.ReadObjects(pdf)[TestPdfDecryptor.EncryptObjectNumber(pdf)];
        var trailer = TestPdfDecryptor.ReadTrailer(pdf);
        byte[]? Optional(string key) => encrypt.Any(t => t.Text == key) ? TestPdfDecryptor.ValueOf(encrypt, key).Bytes : null;
        return new StandardSecurityParameters(
            int.Parse(TestPdfDecryptor.ValueOf(encrypt, "/R").Text, System.Globalization.CultureInfo.InvariantCulture),
            TestPdfDecryptor.ValueOf(encrypt, "/O").Bytes,
            TestPdfDecryptor.ValueOf(encrypt, "/U").Bytes,
            Optional("/OE"),
            Optional("/UE"),
            Optional("/Perms"),
            int.Parse(TestPdfDecryptor.ValueOf(encrypt, "/P").Text, System.Globalization.CultureInfo.InvariantCulture),
            trailer.SkipWhile(t => t.Text != "/ID").First(t => t.Kind == TokenKind.String).Bytes,
            EncryptMetadata: !encrypt.Any(t => t.Text == "/EncryptMetadata"));
    }
}
