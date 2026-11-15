using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf.Tests.Reading;

/// <summary>Opening encrypted files (#111): qpdf's output in every supported variant, and our own encrypted output.</summary>
public class EncryptedReadingTests
{
    private const string Title = "\u00c5rsrapport \u2013 fortrolig";

    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));

    private static string Fixture(string name) => Fixtures.Path(Path.Combine("Reader", "Encrypted", name));

    /// <summary>The files make-fixtures.py writes with qpdf: name, user password and what the encryption is.</summary>
    public static TheoryData<string, string> QpdfFiles => new()
    {
        { "rc4-40.pdf", "user" }, // Revision 2, RC4 40-bit.
        { "rc4-128.pdf", "" }, // Revision 3, RC4 128-bit, restricted permissions.
        { "rc4-128-r4.pdf", "user" }, // Revision 4 with the RC4 crypt filter (/V2), metadata unencrypted.
        { "aes-128-objstm.pdf", "user" }, // Revision 4, AES-128, object streams and a cross-reference stream.
        { "aes-256-objstm.pdf", "user" }, // Revision 6, AES-256, object streams, metadata unencrypted.
    };

    [Theory]
    [MemberData(nameof(QpdfFiles))]
    public void Files_encrypted_by_qpdf_open_with_the_user_password(string name, string password)
    {
        var reader = PdfReader.Open(Fixture(name), password);

        Assert.True(reader.IsEncrypted);
        Assert.False(reader.IsOpenedWithOwnerPassword);
        AssertContent(reader);
    }

    [Theory]
    [MemberData(nameof(QpdfFiles))]
    public void Files_encrypted_by_qpdf_open_with_the_owner_password(string name, string password)
    {
        _ = password;
        var reader = PdfReader.Open(Fixture(name), "owner");

        Assert.True(reader.IsOpenedWithOwnerPassword);
        AssertContent(reader);
    }

    [Theory]
    [InlineData("rc4-40.pdf")]
    [InlineData("aes-128-objstm.pdf")]
    [InlineData("aes-256-objstm.pdf")]
    public void A_wrong_or_missing_password_is_a_password_exception(string name)
    {
        var missing = Assert.Throws<PdfPasswordException>(() => PdfReader.Open(Fixture(name)));
        var wrong = Assert.Throws<PdfPasswordException>(() => PdfReader.Open(Fixture(name), "guess"));

        Assert.Contains("password", missing.Message, StringComparison.Ordinal);
        Assert.Contains("neither", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Permissions_are_read_from_the_encryption_dictionary()
    {
        var restricted = PdfReader.Open(Fixture("rc4-128.pdf")); // No user password: opens as is.

        Assert.Equal(PdfPermissions.Print | PdfPermissions.ExtractForAccessibility, restricted.Permissions);
        Assert.False(restricted.IsOpenedWithOwnerPassword);
        Assert.Equal(restricted.Permissions, PdfReader.Open(Fixture("rc4-128.pdf"), "owner").Permissions); // What the file says, whoever opens it.

        var plain = PdfReader.Open(DocumentBytes(encryption: null));
        Assert.False(plain.IsEncrypted);
        Assert.Equal(PdfPermissions.All, plain.Permissions);
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256, true)]
    [InlineData(PdfEncryptionAlgorithm.Aes256, false)]
    [InlineData(PdfEncryptionAlgorithm.Aes128, true)]
    [InlineData(PdfEncryptionAlgorithm.Aes128, false)]
    public void Our_encrypted_output_reads_back_with_either_password(PdfEncryptionAlgorithm algorithm, bool encryptMetadata)
    {
        var permissions = PdfPermissions.Print | PdfPermissions.Copy;
        var bytes = DocumentBytes(new PdfEncryption
        {
            UserPassword = "user secret",
            OwnerPassword = "owner secret",
            Permissions = permissions,
            Algorithm = algorithm,
            EncryptMetadata = encryptMetadata,
        });

        foreach (var (password, owner) in new[] { ("user secret", false), ("owner secret", true) })
        {
            var reader = PdfReader.Open(bytes, password);

            Assert.Equal(owner, reader.IsOpenedWithOwnerPassword);
            Assert.Equal(permissions, reader.Permissions & ~PdfPermissions.ExtractForAccessibility); // AES-256 always sets that bit.
            Assert.Equal(Title, InfoTitle(reader));
            Assert.Contains(Title, Metadata(reader), StringComparison.Ordinal);
            Assert.Equal("da-DK", PdfText.Decode(Assert.IsType<PdfString>(reader.Catalog["Lang"]).Bytes)); // The catalog is read before the password.
            Assert.StartsWith("BT /F1 12 Tf 72 720 Td <002B0048004F004F0052> Tj ET", Encoding.Latin1.GetString(reader.Pages[0].GetContent()), StringComparison.Ordinal); // "Hello" by glyph.
        }

        Assert.Throws<PdfPasswordException>(() => PdfReader.Open(bytes));
    }

    [Theory]
    [InlineData("rc4-40.pdf")]
    [InlineData("aes-256-objstm.pdf")]
    public void The_encryption_dictionary_itself_is_not_decrypted(string name)
    {
        var data = File.ReadAllBytes(Fixture(name));
        var raw = new PdfObjectStore(data);

        var reader = PdfReader.Open(data, "user");

        foreach (var key in new[] { "O", "U" })
        {
            var expected = Assert.IsType<PdfString>(Assert.IsType<PdfDictionary>(raw.Resolve(raw.Trailer["Encrypt"]))[key]).Bytes.ToArray();
            Assert.Equal(expected, Assert.IsType<PdfString>(Assert.IsType<PdfDictionary>(reader.Resolve(reader.Trailer["Encrypt"]))[key]).Bytes.ToArray());
        }
    }

    [Fact]
    public void An_owner_password_only_file_opens_without_a_password()
    {
        var bytes = DocumentBytes(new PdfEncryption { OwnerPassword = "owner", Permissions = PdfPermissions.Print });

        var reader = PdfReader.Open(bytes);

        Assert.False(reader.IsOpenedWithOwnerPassword);
        Assert.Equal(PdfPermissions.Print, reader.Permissions & ~PdfPermissions.ExtractForAccessibility);
        Assert.Equal(Title, InfoTitle(reader));
    }

    [Fact]
    public void Unicode_passwords_are_prepared_the_same_way_when_reading()
    {
        // AES-256 passwords are SASLprep'd: a non-ASCII space reads as a space, a soft hyphen as nothing.
        var bytes = DocumentBytes(new PdfEncryption { UserPassword = "p\u00e5 kode", OwnerPassword = "ejer" });

        Assert.Equal(Title, InfoTitle(PdfReader.Open(bytes, "p\u00e5\u00a0ko\u00adde")));
    }

    [Theory]
    [InlineData("aes-256-objstm.pdf")]
    [InlineData("rc4-40.pdf")]
    public void Pages_of_encrypted_files_import_and_draw(string name)
    {
        var reader = PdfReader.Open(Fixture(name), "owner");
        var doc = new PdfDocument { Encryption = new PdfEncryption { UserPassword = "new" } };
        doc.ImportPages(reader);
        doc.AddPage(new PdfPageSize(612, 792)).Canvas.DrawPage(reader.Pages[1], 0, 0, 306, 396);

        var copy = PdfReader.Open(doc.ToArray(), "new");

        Assert.Equal(3, copy.Pages.Count);
        Assert.Contains("(One) Tj", Encoding.Latin1.GetString(copy.Pages[0].GetContent()), StringComparison.Ordinal);
        Assert.Contains("(Two) Tj", Encoding.Latin1.GetString(copy.Pages[1].GetContent()), StringComparison.Ordinal);
        ExternalTools.AssertQpdfClean(doc.ToArray(), "new");
    }

    [Fact]
    public void A_repaired_file_decrypts_objects_with_their_generation()
    {
        // AES-128 keys depend on the object's generation: a file without cross-reference data is repaired by scanning
        // for objects, and object 4 is generation 1.
        var handler = RaiCore.Pdf.Security.StandardSecurityHandler.Create(new PdfEncryption { Algorithm = PdfEncryptionAlgorithm.Aes128 });
        var p = handler.Parameters;
        static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
        var pdf = Encoding.ASCII.GetBytes(
            "%PDF-1.7\n"
            + "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Note 4 1 R >>\nendobj\n"
            + "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"
            + "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>\nendobj\n"
            + $"4 1 obj\n<{Hex(handler.Encrypt("generation one"u8, 4, 1))}>\nendobj\n"
            + $"5 0 obj\n<< /Filter /Standard /V 4 /R 4 /Length 128 /CF << /StdCF << /CFM /AESV2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF /O <{Hex(p.O)}> /U <{Hex(p.U)}> /P {p.P} >>\nendobj\n"
            + $"trailer\n<< /Root 1 0 R /Encrypt 5 0 R /ID [<{Hex(p.DocumentId)}><{Hex(p.DocumentId)}>] >>\n%%EOF\n");

        var reader = PdfReader.Open(pdf);

        Assert.True(reader.IsRepaired);
        Assert.Equal("generation one", Encoding.ASCII.GetString(Assert.IsType<PdfString>(reader.Resolve(reader.Catalog["Note"])).Bytes));
    }

    [Fact]
    public void Unsupported_security_handlers_are_named()
    {
        var pdf = TestPdf.Build(
            [
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>",
                "<< /Filter /Adobe.PubSec /V 4 /R 4 >>",
            ],
            "/Root 1 0 R /Encrypt 4 0 R /ID [<00><00>]");

        var error = Assert.Throws<NotSupportedException>(() => PdfReader.Open(pdf));

        Assert.Contains("Adobe.PubSec", error.Message, StringComparison.Ordinal);
    }

    private static void AssertContent(PdfReader reader)
    {
        Assert.Equal(2, reader.Pages.Count);
        Assert.Equal("1 0 0 rg 100 600 200 100 re f BT /F1 36 Tf 100 500 Td (One) Tj ET", Encoding.Latin1.GetString(reader.Pages[0].GetContent()));
        Assert.Equal("0 0 1 rg 100 600 200 100 re f BT /F1 36 Tf 100 500 Td (Two) Tj ET", Encoding.Latin1.GetString(reader.Pages[1].GetContent()));
        Assert.Equal("\u00c5rsrapport", InfoTitle(reader)); // pikepdf copies the XMP title into the Info dictionary.
        Assert.Contains("\u00c5rsrapport", Metadata(reader), StringComparison.Ordinal);
    }

    private static string? InfoTitle(PdfReader reader) =>
        reader.Resolve(reader.Trailer["Info"]) is PdfDictionary info && reader.Resolve(info["Title"]) is PdfString title ? PdfText.Decode(title.Bytes) : null;

    private static string Metadata(PdfReader reader) =>
        reader.Resolve(reader.Catalog["Metadata"]) is PdfStreamObject stream ? Encoding.UTF8.GetString(reader.Store.Decode(stream)) : string.Empty;

    private static byte[] DocumentBytes(PdfEncryption? encryption)
    {
        var doc = new PdfDocument { Encryption = encryption, Language = "da-DK", Info = { Title = Title } };
        doc.AddPage().Canvas.DrawText("Hello", Sans, 12, 72, 720);
        return doc.ToArray();
    }
}
