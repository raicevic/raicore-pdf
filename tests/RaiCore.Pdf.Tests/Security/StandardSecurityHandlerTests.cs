using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RaiCore.Pdf.Security;

namespace RaiCore.Pdf.Tests.Security;

/// <summary>
/// The security handler's key derivation against known answers: encryption dictionaries written by qpdf 12.2 (through
/// pikepdf 9.11) and Algorithm 2.B hashes computed by pypdf 6.21, recorded here.
/// </summary>
public class StandardSecurityHandlerTests
{
    private static readonly byte[] QpdfDocumentId = Convert.FromHexString("1c9ab28322bdc15e3fb29104d5a55d31");

    /// <summary>qpdf, R6, user "user", owner "owner".</summary>
    private static readonly StandardSecurityParameters QpdfR6 = new(
        6,
        Convert.FromHexString("3307fd625c191015fa158c4b1026eacf75eba2c20fe5587250d943a39128f153349014fbd03cb1de295b43472d81f1da"),
        Convert.FromHexString("d7de036bf0f88fe11676b8d04323c5f8aaf4431106b6c0eca358908681fa31e5d70dd5b7aec40a9abaf1e2f6bf646189"),
        Convert.FromHexString("b9852bc0c7f34fce650fc7efdc0b7048a6bd3920f698e295948fafb0100cb0ef"),
        Convert.FromHexString("4c6cc80c638f1b307360cf4877118a241eb6573b3cc87115cd29c1cb94c86fb9"),
        Convert.FromHexString("2ecc3e2059563c34f825a970240c248e"),
        -1028,
        QpdfDocumentId,
        EncryptMetadata: true);

    /// <summary>qpdf, R6, empty user password, owner "s3cr&#xE9;t &#xDF;" (non-ASCII, so UTF-8 matters), no permissions.</summary>
    private static readonly StandardSecurityParameters QpdfR6OwnerOnly = new(
        6,
        Convert.FromHexString("65e0ed06ceae1779008c9a993569c4c27cc9e0fa7f8294880d3331815ea15a5149d855914399ea7c69a73573d96f5aa9"),
        Convert.FromHexString("9c71fae9a9a0bb9184752db18abd30d30ef54f244fd86d3a7d9e9fac3aab95a3d7aa11a91d5d887bf3a2000912ee0a51"),
        Convert.FromHexString("9441ef368ca809f08d63cafabe2c72c92be42d5d82f3aba70f57074b48c63499"),
        Convert.FromHexString("343d94bb9bbe373e9aea499054f207900231e3845d3524f5a8c33b9e2c3e26a2"),
        Convert.FromHexString("4cd2d9acc42f05a095f7805a530a94cd"),
        -3392,
        QpdfDocumentId,
        EncryptMetadata: true);

    /// <summary>qpdf, R4 (AES-128), user "user", owner "owner".</summary>
    private static readonly StandardSecurityParameters QpdfR4 = new(
        4,
        Convert.FromHexString("0ba3835f88f90388e74e54584125ce142be0de24c6b0d37746e075b891756671"),
        Convert.FromHexString("17c9846c9537755412f8800cddd0bb340122456a91bae5134273a6db134c87c4"),
        null,
        null,
        null,
        -1044,
        QpdfDocumentId,
        EncryptMetadata: true);

    private const string OwnerOnlyPassword = "s3cr\u00E9t \u00DF";

    [Fact]
    public void Algorithm_2B_hash_matches_pypdf()
    {
        var salt = Convert.FromHexString("0102030405060708");

        Assert.Equal(
            "8d1efb4f1bdbb651341704c2139de4f6be05d6d4609af56916b21646ed74825c",
            Convert.ToHexStringLower(StandardSecurityHandler.HashR6([], salt, [])));

        // A 127-byte password and a 48-byte user key, as in an owner password hash.
        var password = Enumerable.Range(0x21, 127).Select(i => (byte)i).ToArray();
        var userKey = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();
        Assert.Equal(
            "aaccf0b09e4a487c7daa937a28ce33169fadedd9826e44a1c0694537cdcea2ee",
            Convert.ToHexStringLower(StandardSecurityHandler.HashR6(password, salt, userKey)));

        // Stops after round 67, whose last byte of E (35) equals the round number - 32: "less than or equal" matters.
        Assert.Equal(
            "a4c6f20a1fedb58e9bdd1af3defe272cd4924b82f7484307a5d8166e662b4e29",
            Convert.ToHexStringLower(StandardSecurityHandler.HashR6("password62"u8, salt, [])));
    }

    [Fact]
    public void Algorithm_2B_reproduces_the_password_hashes_qpdf_wrote()
    {
        var u = QpdfR6.U;
        var o = QpdfR6.O;
        Assert.Equal(u[..32], StandardSecurityHandler.HashR6("user"u8, u[32..40], []));
        Assert.Equal(o[..32], StandardSecurityHandler.HashR6("owner"u8, o[32..40], u));

        var ownerOnly = QpdfR6OwnerOnly;
        Assert.Equal(ownerOnly.U[..32], StandardSecurityHandler.HashR6([], ownerOnly.U[32..40], []));
        Assert.Equal(
            ownerOnly.O[..32],
            StandardSecurityHandler.HashR6(Encoding.UTF8.GetBytes(OwnerOnlyPassword), ownerOnly.O[32..40], ownerOnly.U));
    }

    [Theory]
    [InlineData("user", false)]
    [InlineData("owner", true)]
    public void Opens_a_qpdf_aes256_file_with_either_password(string password, bool isOwner)
    {
        var handler = StandardSecurityHandler.Authenticate(QpdfR6, password);

        Assert.NotNull(handler);
        Assert.Equal(isOwner, handler.IsOwner);
        Assert.Equal("438dee10dd5bbae711e1b01567d33d872cf74cc1c0a2023432816421eeeb9ab0", Convert.ToHexStringLower(handler.FileKey));
    }

    [Fact]
    public void Opens_a_qpdf_owner_only_file_with_the_empty_password_and_the_unicode_owner_password()
    {
        var asUser = StandardSecurityHandler.Authenticate(QpdfR6OwnerOnly, null);
        var asOwner = StandardSecurityHandler.Authenticate(QpdfR6OwnerOnly, OwnerOnlyPassword);

        Assert.NotNull(asUser);
        Assert.False(asUser.IsOwner);
        Assert.NotNull(asOwner);
        Assert.True(asOwner.IsOwner);
        Assert.Equal("196a1c85c7d47572e2ebd945c482be734213cc54b159d755ed3137a44d92a87c", Convert.ToHexStringLower(asUser.FileKey));
        Assert.Equal(asUser.FileKey, asOwner.FileKey);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("User")]
    [InlineData("")]
    public void A_wrong_password_does_not_open_a_qpdf_file(string password)
    {
        Assert.Null(StandardSecurityHandler.Authenticate(QpdfR6, password));
        Assert.Null(StandardSecurityHandler.Authenticate(QpdfR4, password));
    }

    [Fact]
    public void Revision_4_values_match_qpdf()
    {
        var user = StandardSecurityHandler.Pad("user"u8);
        var owner = StandardSecurityHandler.Pad("owner"u8);

        var o = StandardSecurityHandler.ComputeOwnerValueR4(owner, user);
        Assert.Equal(QpdfR4.O, o);

        var fileKey = StandardSecurityHandler.ComputeFileKeyR4(user, o, QpdfR4.P, QpdfDocumentId, encryptMetadata: true);
        Assert.Equal("899b991de0db68ba269951d8687689b7", Convert.ToHexStringLower(fileKey));
        Assert.Equal(QpdfR4.U[..16], StandardSecurityHandler.ComputeUserValueR4(fileKey, QpdfDocumentId)[..16]);

        Assert.Equal(fileKey, StandardSecurityHandler.Authenticate(QpdfR4, "user")!.FileKey);
        Assert.True(StandardSecurityHandler.Authenticate(QpdfR4, "owner")!.IsOwner);
    }

    [Fact]
    public void A_perms_entry_that_does_not_match_P_is_rejected()
    {
        var tampered = QpdfR6 with { P = -4 };

        Assert.Throws<InvalidDataException>(() => StandardSecurityHandler.Authenticate(tampered, "user"));
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    public void A_created_handler_opens_with_its_passwords_and_decrypts_what_it_encrypts(PdfEncryptionAlgorithm algorithm)
    {
        var writer = StandardSecurityHandler.Create(new PdfEncryption { UserPassword = "u", OwnerPassword = "o", Algorithm = algorithm });

        var user = StandardSecurityHandler.Authenticate(writer.Parameters, "u");
        var owner = StandardSecurityHandler.Authenticate(writer.Parameters, "o");
        Assert.NotNull(user);
        Assert.NotNull(owner);
        Assert.False(user.IsOwner);
        Assert.True(owner.IsOwner);
        Assert.Equal(writer.FileKey, user.FileKey);
        Assert.Null(StandardSecurityHandler.Authenticate(writer.Parameters, "x"));

        var data = "Hello, encrypted world"u8.ToArray();
        var encrypted = writer.Encrypt(data, 7, 0);
        Assert.Equal(16 + 32, encrypted.Length);
        Assert.Equal(data, user.Decrypt(encrypted, 7, 0));
        Assert.NotEqual(encrypted, writer.Encrypt(data, 7, 0)); // A fresh random IV every time.
        Assert.Equal(algorithm == PdfEncryptionAlgorithm.Aes256 ? 32 : 16, writer.FileKey.Length);
    }

    [Fact]
    public void Aes128_uses_a_key_per_object_and_aes256_the_file_key()
    {
        var data = "same data"u8.ToArray();
        var aes128 = StandardSecurityHandler.Create(new PdfEncryption { Algorithm = PdfEncryptionAlgorithm.Aes128 });
        var aes256 = StandardSecurityHandler.Create(new PdfEncryption());

        Assert.ThrowsAny<CryptographicException>(() => DecryptOrThrow(aes128, aes128.Encrypt(data, 1, 0), 2));
        Assert.Equal(data, aes256.Decrypt(aes256.Encrypt(data, 1, 0), 2, 0));
        Assert.Equal(data, TestPdfDecryptor.AesDecrypt(aes256.FileKey, aes256.Encrypt(data, 1, 0)));
    }

    [Fact]
    public void Empty_data_encrypts_to_an_iv_and_one_padding_block()
    {
        var handler = StandardSecurityHandler.Create(new PdfEncryption());

        var encrypted = handler.Encrypt([], 1, 0);

        Assert.Equal(32, encrypted.Length);
        Assert.Empty(handler.Decrypt(encrypted, 1, 0));
        Assert.Empty(handler.Decrypt([], 1, 0));
    }

    [Fact]
    public void Aes256_owner_value_hashes_the_user_value_and_perms_repeats_P()
    {
        var handler = StandardSecurityHandler.Create(new PdfEncryption
        {
            UserPassword = "u",
            OwnerPassword = "o",
            Permissions = PdfPermissions.Print | PdfPermissions.Copy,
            EncryptMetadata = false,
        });
        var parameters = handler.Parameters;

        Assert.Equal(48, parameters.O.Length);
        Assert.Equal(48, parameters.U.Length);
        Assert.Equal(parameters.O[..32], StandardSecurityHandler.HashR6("o"u8, parameters.O[32..40], parameters.U));

        using var aes = Aes.Create();
        aes.Key = handler.FileKey;
        var perms = aes.DecryptEcb(parameters.Perms!, PaddingMode.None);
        Assert.Equal(parameters.P, BinaryPrimitives.ReadInt32LittleEndian(perms));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, perms[4..8]);
        Assert.Equal((byte)'F', perms[8]);
        Assert.Equal("adb"u8.ToArray(), perms[9..12]);
    }

    [Theory]
    [InlineData(PdfPermissions.None, PdfEncryptionAlgorithm.Aes256, -3392)] // 0xFFFFF2C0, as qpdf writes it.
    [InlineData(PdfPermissions.None, PdfEncryptionAlgorithm.Aes128, -3904)] // 0xFFFFF0C0
    [InlineData(PdfPermissions.All, PdfEncryptionAlgorithm.Aes256, -4)]
    [InlineData(PdfPermissions.All, PdfEncryptionAlgorithm.Aes128, -4)]
    [InlineData(PdfPermissions.Print | PdfPermissions.PrintHighQuality, PdfEncryptionAlgorithm.Aes256, -1340)] // 0xFFFFFAC4
    [InlineData(PdfPermissions.Copy | PdfPermissions.ExtractForAccessibility, PdfEncryptionAlgorithm.Aes128, -3376)] // 0xFFFFF2D0
    [InlineData(PdfPermissions.Modify | PdfPermissions.Annotate | PdfPermissions.FillForms | PdfPermissions.Assemble, PdfEncryptionAlgorithm.Aes128, -2584)] // 0xFFFFF5E8
    public void Permissions_map_to_the_bits_of_P(PdfPermissions permissions, PdfEncryptionAlgorithm algorithm, int p) =>
        Assert.Equal(p, StandardSecurityHandler.PermissionBits(permissions, algorithm));

    [Fact]
    public void Invalid_permissions_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => StandardSecurityHandler.PermissionBits(PdfPermissions.PrintHighQuality, PdfEncryptionAlgorithm.Aes256));
        Assert.Throws<ArgumentException>(() => StandardSecurityHandler.PermissionBits((PdfPermissions)1, PdfEncryptionAlgorithm.Aes256));
        Assert.Throws<ArgumentException>(() => StandardSecurityHandler.PermissionBits((PdfPermissions)(1 << 12), PdfEncryptionAlgorithm.Aes256));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("abc", "abc")]
    [InlineData("a\u00A0b\u3000c", "a b c")] // Non-ASCII spaces become spaces.
    [InlineData("pass\u00ADwo\u200Brd\uFEFF", "password")] // Soft hyphen, zero-width space and BOM map to nothing.
    [InlineData("\uFB01x", "fix")] // NFKC: the fi ligature.
    [InlineData("\u2168", "IX")] // NFKC: Roman numeral nine.
    [InlineData("e\u0301", "\u00E9")] // NFKC composes.
    [InlineData("\u00E6\u00F8\u00E5 \u4E2D\u6587 \U0001F600", "\u00E6\u00F8\u00E5 \u4E2D\u6587 \U0001F600")]
    public void Aes256_passwords_are_prepared_with_saslprep(string? password, string expected) =>
        Assert.Equal(Encoding.UTF8.GetBytes(expected), StandardSecurityHandler.PreparePasswordR6(password, "p"));

    [Theory]
    [InlineData("tab\there")]
    [InlineData("bell\u0007")]
    [InlineData("c1\u0085")]
    [InlineData("private\uE000")]
    [InlineData("lone \uD800 surrogate")]
    [InlineData("noncharacter\uFFFF")]
    [InlineData("\u200Eltr mark")]
    [InlineData("tag\U000E0041")]
    public void Prohibited_characters_are_rejected(string password)
    {
        var e = Assert.Throws<ArgumentException>(() => StandardSecurityHandler.PreparePasswordR6(password, "UserPassword"));
        Assert.Equal("UserPassword", e.ParamName);
    }

    [Fact]
    public void Aes256_passwords_are_cut_to_127_utf8_bytes()
    {
        var password = new string('a', 126) + "\u00E9\u00E9";

        var prepared = StandardSecurityHandler.PreparePasswordR6(password, "p");

        Assert.Equal(127, prepared.Length);
        Assert.Equal(0xC3, prepared[^1]); // Cut inside a UTF-8 sequence, byte-wise as the standard says.
    }

    [Fact]
    public void Aes128_passwords_are_latin1_cut_to_32_bytes()
    {
        Assert.Equal(new byte[] { 0x61, 0xE6, 0xF8, 0xE5 }, StandardSecurityHandler.PreparePasswordR4("a\u00E6\u00F8\u00E5", "p"));
        Assert.Equal(32, StandardSecurityHandler.PreparePasswordR4(new string('x', 40), "p").Length);
        Assert.Throws<ArgumentException>(() => StandardSecurityHandler.PreparePasswordR4("\u4E2D", "p"));
        Assert.Throws<ArgumentException>(() => StandardSecurityHandler.PreparePasswordR4("\u20AC", "p"));
        Assert.Throws<ArgumentException>(() => StandardSecurityHandler.PreparePasswordR4("a\nb", "p"));
    }

    [Fact]
    public void Equivalent_passwords_open_the_same_file()
    {
        var handler = StandardSecurityHandler.Create(new PdfEncryption { UserPassword = "\uFB01le\u00A0pass", OwnerPassword = "o" });

        Assert.NotNull(StandardSecurityHandler.Authenticate(handler.Parameters, "file pass"));
        Assert.Null(StandardSecurityHandler.Authenticate(handler.Parameters, "file  pass"));
    }

    [Fact]
    public void Long_passwords_match_on_their_first_127_bytes()
    {
        var handler = StandardSecurityHandler.Create(new PdfEncryption { UserPassword = new string('k', 200) });

        Assert.NotNull(StandardSecurityHandler.Authenticate(handler.Parameters, new string('k', 127)));
        Assert.Null(StandardSecurityHandler.Authenticate(handler.Parameters, new string('k', 126)));
    }

    [Fact]
    public void Without_an_owner_password_the_owner_password_is_random()
    {
        var handler = StandardSecurityHandler.Create(new PdfEncryption { UserPassword = "u", Permissions = PdfPermissions.None });

        Assert.False(StandardSecurityHandler.Authenticate(handler.Parameters, "u")!.IsOwner);
        Assert.False(StandardSecurityHandler.Authenticate(handler.Parameters, "")?.IsOwner ?? false);
    }

    private static byte[] DecryptOrThrow(StandardSecurityHandler handler, byte[] data, int objectNumber)
    {
        var plain = handler.Decrypt(data, objectNumber, 0);

        // A wrong key can produce valid-looking padding by chance; the content would still differ.
        return plain.AsSpan().SequenceEqual("same data"u8) ? plain : throw new CryptographicException("Wrong key.");
    }
}
