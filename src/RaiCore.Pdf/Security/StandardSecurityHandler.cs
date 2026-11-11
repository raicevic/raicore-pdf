using System.Buffers.Binary;
using System.Security.Cryptography;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Security;

/// <summary>
/// The values of a standard security handler's encryption dictionary (ISO 32000-2, tables 20 and 21) that key
/// derivation and password checks need. A writer gets them from <see cref="StandardSecurityHandler.Create"/>; a reader
/// parses them from the file's <c>/Encrypt</c> dictionary and passes them to
/// <see cref="StandardSecurityHandler.Authenticate"/>.
/// </summary>
/// <param name="Revision">The <c>/R</c> entry: 6 (AES-256) or 4 (AES-128).</param>
/// <param name="O">The <c>/O</c> entry: 48 bytes for revision 6, 32 for revision 4.</param>
/// <param name="U">The <c>/U</c> entry: 48 bytes for revision 6, 32 for revision 4.</param>
/// <param name="OE">The <c>/OE</c> entry (revision 6 only): the file key encrypted with the owner password.</param>
/// <param name="UE">The <c>/UE</c> entry (revision 6 only): the file key encrypted with the user password.</param>
/// <param name="Perms">The <c>/Perms</c> entry (revision 6 only): <c>/P</c> encrypted with the file key.</param>
/// <param name="P">The <c>/P</c> entry: the permission bits as a signed 32-bit integer.</param>
/// <param name="DocumentId">The first string of the trailer's <c>/ID</c>; revision 4 mixes it into the file key.</param>
/// <param name="EncryptMetadata">The <c>/EncryptMetadata</c> entry (default <c>true</c>).</param>
internal sealed record StandardSecurityParameters(
    int Revision,
    byte[] O,
    byte[] U,
    byte[]? OE,
    byte[]? UE,
    byte[]? Perms,
    int P,
    byte[] DocumentId,
    bool EncryptMetadata);

/// <summary>
/// The standard security handler (ISO 32000-2, 7.6.4) for AES-256 (V5/R6, crypt filter <c>/AESV3</c>) and AES-128
/// (V4/R4, <c>/AESV2</c>): the file key, the password values of the encryption dictionary, password checks, and the
/// AES-CBC encryption of strings and streams (a random 16-byte IV in front, PKCS#7 padding). RC4 encryption of content
/// is not supported; revision 4 uses RC4 only inside its <c>/O</c> and <c>/U</c> computations, as the standard requires.
/// </summary>
/// <remarks>
/// The writer creates a handler with <see cref="Create"/> and encrypts each string and stream with
/// <see cref="Encrypt"/>; a reader gets one from <see cref="Authenticate"/> and calls <see cref="Decrypt"/>.
/// </remarks>
internal sealed class StandardSecurityHandler
{
    /// <summary>The 32-byte padding string of ISO 32000-2, 7.6.4.3.2, step a.</summary>
    private static readonly byte[] PasswordPadding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private StandardSecurityHandler(StandardSecurityParameters parameters, byte[] fileKey, bool isOwner)
    {
        Parameters = parameters;
        FileKey = fileKey;
        IsOwner = isOwner;
    }

    /// <summary>The encryption dictionary values.</summary>
    public StandardSecurityParameters Parameters { get; }

    /// <summary>The security handler revision: 6 (AES-256) or 4 (AES-128).</summary>
    public int Revision => Parameters.Revision;

    /// <summary>The file encryption key: 32 bytes for revision 6, 16 for revision 4.</summary>
    public byte[] FileKey { get; }

    /// <summary>Whether the handler was opened with the owner password (always true for a handler created for writing).</summary>
    public bool IsOwner { get; }

    /// <summary>
    /// Creates a handler for writing: validates the options, prepares the passwords, draws a random file key and computes
    /// the encryption dictionary values. Also draws a random document ID for the trailer.
    /// </summary>
    /// <exception cref="ArgumentException">A password or the permissions are invalid.</exception>
    public static StandardSecurityHandler Create(PdfEncryption options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var documentId = RandomNumberGenerator.GetBytes(16);
        var p = PermissionBits(options.Permissions, options.Algorithm);

        switch (options.Algorithm)
        {
            case PdfEncryptionAlgorithm.Aes256:
            {
                var user = PreparePasswordR6(options.UserPassword, nameof(PdfEncryption.UserPassword));
                var owner = string.IsNullOrEmpty(options.OwnerPassword)
                    ? RandomNumberGenerator.GetBytes(32)
                    : PreparePasswordR6(options.OwnerPassword, nameof(PdfEncryption.OwnerPassword));
                var fileKey = RandomNumberGenerator.GetBytes(32);

                // Algorithm 8: /U and /UE.
                var userSalts = RandomNumberGenerator.GetBytes(16);
                byte[] u = [.. HashR6(user, userSalts.AsSpan(0, 8), []), .. userSalts];
                var ue = AesNoPadding(HashR6(user, userSalts.AsSpan(8, 8), []), fileKey, encrypt: true);

                // Algorithm 9: /O and /OE, which also hash /U.
                var ownerSalts = RandomNumberGenerator.GetBytes(16);
                byte[] o = [.. HashR6(owner, ownerSalts.AsSpan(0, 8), u), .. ownerSalts];
                var oe = AesNoPadding(HashR6(owner, ownerSalts.AsSpan(8, 8), u), fileKey, encrypt: true);

                // Algorithm 10: /Perms.
                var perms = new byte[16];
                BinaryPrimitives.WriteInt32LittleEndian(perms, p);
                perms.AsSpan(4, 4).Fill(0xFF);
                perms[8] = options.EncryptMetadata ? (byte)'T' : (byte)'F';
                "adb"u8.CopyTo(perms.AsSpan(9));
                RandomNumberGenerator.Fill(perms.AsSpan(12));
                using (var aes = Aes.Create())
                {
                    aes.Key = fileKey;
                    perms = aes.EncryptEcb(perms, PaddingMode.None);
                }

                return new StandardSecurityHandler(
                    new StandardSecurityParameters(6, o, u, oe, ue, perms, p, documentId, options.EncryptMetadata),
                    fileKey,
                    isOwner: true);
            }

            case PdfEncryptionAlgorithm.Aes128:
            {
                var user = Pad(PreparePasswordR4(options.UserPassword, nameof(PdfEncryption.UserPassword)));
                var owner = string.IsNullOrEmpty(options.OwnerPassword)
                    ? RandomNumberGenerator.GetBytes(32)
                    : Pad(PreparePasswordR4(options.OwnerPassword, nameof(PdfEncryption.OwnerPassword)));
                var o = ComputeOwnerValueR4(owner, user);
                var fileKey = ComputeFileKeyR4(user, o, p, documentId, options.EncryptMetadata);
                var u = ComputeUserValueR4(fileKey, documentId);
                return new StandardSecurityHandler(
                    new StandardSecurityParameters(4, o, u, null, null, null, p, documentId, options.EncryptMetadata),
                    fileKey,
                    isOwner: true);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Algorithm, "Unknown encryption algorithm.");
        }
    }

    /// <summary>
    /// Opens an encrypted file: checks <paramref name="password"/> as the owner password, then as the user password
    /// (Algorithm 2.A for revision 6, Algorithms 6 and 7 for revision 4), and returns a handler with the file key, or
    /// <c>null</c> if the password is wrong. A <c>null</c> password is the empty password.
    /// </summary>
    /// <exception cref="NotSupportedException">The revision is not 4 or 6.</exception>
    /// <exception cref="InvalidDataException">The values are malformed, or (revision 6) <c>/Perms</c> does not match <c>/P</c>.</exception>
    public static StandardSecurityHandler? Authenticate(StandardSecurityParameters parameters, string? password)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.Revision switch
        {
            6 => AuthenticateR6(parameters, password),
            4 => AuthenticateR4(parameters, password),
            _ => throw new NotSupportedException($"Standard security handler revision {parameters.Revision} is not supported."),
        };
    }

    /// <summary>
    /// Encrypts a string or stream of indirect object <paramref name="objectNumber"/>: a random 16-byte IV followed by the
    /// AES-CBC ciphertext with PKCS#7 padding. Revision 6 uses the file key itself; revision 4 a key derived per object.
    /// </summary>
    public byte[] Encrypt(ReadOnlySpan<byte> data, int objectNumber, int generation)
    {
        using var aes = Aes.Create();
        aes.Key = ObjectKey(objectNumber, generation);
        var result = new byte[16 + aes.GetCiphertextLengthCbc(data.Length, PaddingMode.PKCS7)];
        RandomNumberGenerator.Fill(result.AsSpan(0, 16));
        aes.EncryptCbc(data, result.AsSpan(0, 16), result.AsSpan(16), PaddingMode.PKCS7);
        return result;
    }

    /// <summary>Decrypts what <see cref="Encrypt"/> produced. Empty input gives empty output.</summary>
    /// <exception cref="CryptographicException">The data is not a whole number of AES blocks or its padding is invalid.</exception>
    public byte[] Decrypt(ReadOnlySpan<byte> data, int objectNumber, int generation)
    {
        if (data.IsEmpty)
        {
            return [];
        }

        if (data.Length < 16)
        {
            throw new CryptographicException("Encrypted data is shorter than the AES initialization vector.");
        }

        using var aes = Aes.Create();
        aes.Key = ObjectKey(objectNumber, generation);
        return aes.DecryptCbc(data[16..], data[..16], PaddingMode.PKCS7);
    }

    /// <summary>
    /// Whether a stream with this dictionary is encrypted: all are, except the XMP metadata stream when
    /// <c>/EncryptMetadata</c> is false.
    /// </summary>
    public bool EncryptsStream(PdfDictionary streamDictionary)
    {
        ArgumentNullException.ThrowIfNull(streamDictionary);
        return Parameters.EncryptMetadata || streamDictionary["Type"] is not PdfName { Value: "Metadata" };
    }

    /// <summary>Creates the <c>/Encrypt</c> dictionary. It is written as an indirect object that is itself not encrypted.</summary>
    public PdfDictionary CreateEncryptDictionary()
    {
        var aes256 = Revision == 6;
        var dictionary = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(aes256 ? 5 : 4),
            ["R"] = new PdfInteger(Revision),
            ["Length"] = new PdfInteger(aes256 ? 256 : 128),
            ["CF"] = new PdfDictionary
            {
                ["StdCF"] = new PdfDictionary
                {
                    ["AuthEvent"] = new PdfName("DocOpen"),
                    ["CFM"] = new PdfName(aes256 ? "AESV3" : "AESV2"),
                    ["Length"] = new PdfInteger(aes256 ? 32 : 16),
                },
            },
            ["StmF"] = new PdfName("StdCF"),
            ["StrF"] = new PdfName("StdCF"),
            ["O"] = new PdfString(Parameters.O, hex: true),
            ["U"] = new PdfString(Parameters.U, hex: true),
        };
        if (aes256)
        {
            dictionary["OE"] = new PdfString(Parameters.OE!, hex: true);
            dictionary["UE"] = new PdfString(Parameters.UE!, hex: true);
        }

        dictionary["P"] = new PdfInteger(Parameters.P);
        if (aes256)
        {
            dictionary["Perms"] = new PdfString(Parameters.Perms!, hex: true);
        }

        if (!Parameters.EncryptMetadata)
        {
            dictionary["EncryptMetadata"] = PdfBoolean.False;
        }

        return dictionary;
    }

    /// <summary>
    /// Adds what the catalog needs: AES-256 in a PDF 1.7 file is Adobe extension level 8
    /// (<c>/Extensions &lt;&lt;/ADBE &lt;&lt;/BaseVersion /1.7 /ExtensionLevel 8&gt;&gt;&gt;&gt;</c>), as qpdf and Acrobat write it.
    /// </summary>
    public void AddCatalogEntries(PdfDictionary catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (Revision == 6)
        {
            catalog["Extensions"] = new PdfDictionary
            {
                ["ADBE"] = new PdfDictionary
                {
                    ["BaseVersion"] = new PdfName("1.7"),
                    ["ExtensionLevel"] = new PdfInteger(8),
                },
            };
        }
    }

    /// <summary>
    /// The <c>/P</c> value for <paramref name="permissions"/>: bits 1-2 clear, bits 7-8 and 13-32 set (ISO 32000-2,
    /// table 22), and for revision 6 bit 10 set (PDF 2.0 deprecates the accessibility restriction).
    /// </summary>
    internal static int PermissionBits(PdfPermissions permissions, PdfEncryptionAlgorithm algorithm)
    {
        if ((permissions & ~PdfPermissions.All) != 0)
        {
            throw new ArgumentException($"Unknown permission flags: {permissions}.", nameof(permissions));
        }

        if (permissions.HasFlag(PdfPermissions.PrintHighQuality) && !permissions.HasFlag(PdfPermissions.Print))
        {
            throw new ArgumentException("PrintHighQuality requires Print.", nameof(permissions));
        }

        var bits = unchecked((uint)permissions | 0xFFFFF0C0u);
        if (algorithm == PdfEncryptionAlgorithm.Aes256)
        {
            bits |= (uint)PdfPermissions.ExtractForAccessibility;
        }

        return unchecked((int)bits);
    }

    /// <summary>
    /// Algorithm 2.B (ISO 32000-2, 7.6.4.3.4): the revision 6 password hash. At least 64 rounds of AES-128-CBC over 64
    /// copies of password, previous hash and <paramref name="userKey"/>, each followed by SHA-256, -384 or -512 as the
    /// ciphertext dictates. <paramref name="userKey"/> is empty for user password values and the 48-byte <c>/U</c> for
    /// owner password values.
    /// </summary>
    internal static byte[] HashR6(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> userKey)
    {
        byte[] k = SHA256.HashData([.. password, .. salt, .. userKey]);
        using var aes = Aes.Create();
        for (var round = 1; ; round++)
        {
            var sequenceLength = password.Length + k.Length + userKey.Length;
            var k1 = new byte[sequenceLength * 64];
            var sequence = k1.AsSpan(0, sequenceLength);
            password.CopyTo(sequence);
            k.CopyTo(sequence[password.Length..]);
            userKey.CopyTo(sequence[(password.Length + k.Length)..]);
            for (var i = 1; i < 64; i++)
            {
                sequence.CopyTo(k1.AsSpan(i * sequenceLength));
            }

            aes.Key = k[..16];
            var e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);

            // The first 16 bytes of E as a big-endian number, modulo 3, equal the sum of the bytes modulo 3 (256 = 1 mod 3).
            var sum = 0;
            for (var i = 0; i < 16; i++)
            {
                sum += e[i];
            }

            k = (sum % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };

            if (round >= 64 && e[^1] <= round - 32)
            {
                return k[..32];
            }
        }
    }

    /// <summary>
    /// Prepares a revision 6 password: a simplified SASLprep (RFC 4013; see <see cref="PdfEncryption"/>), then UTF-8
    /// cut to 127 bytes. <c>null</c> is the empty password.
    /// </summary>
    /// <exception cref="ArgumentException">The password contains a prohibited character.</exception>
    internal static byte[] PreparePasswordR6(string? password, string parameterName)
    {
        if (string.IsNullOrEmpty(password))
        {
            return [];
        }

        // Mapping (RFC 4013, 2.1): non-ASCII spaces become a space; "commonly mapped to nothing" characters go away.
        var mapped = new System.Text.StringBuilder(password.Length);
        for (var i = 0; i < password.Length;)
        {
            if (System.Text.Rune.DecodeFromUtf16(password.AsSpan(i), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                throw new ArgumentException("The password contains an unpaired surrogate.", parameterName);
            }

            i += consumed;
            if (IsNonAsciiSpace(rune.Value))
            {
                mapped.Append(' ');
            }
            else if (!MapsToNothing(rune.Value))
            {
                mapped.Append(rune.ToString());
            }
        }

        // Normalization (2.2) and prohibited output (2.3).
        var normalized = mapped.ToString().Normalize(System.Text.NormalizationForm.FormKC);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (IsProhibited(rune))
            {
                throw new ArgumentException(
                    $"The password contains a character that SASLprep prohibits (U+{rune.Value:X4}).", parameterName);
            }
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
        return bytes.Length > 127 ? bytes[..127] : bytes;
    }

    /// <summary>
    /// Prepares a revision 4 password: PDFDocEncoding, which for the accepted characters (printable ASCII and Latin-1
    /// U+00A1-U+00FF except the soft hyphen) is the Latin-1 byte, cut to 32 bytes.
    /// </summary>
    /// <exception cref="ArgumentException">The password contains a character outside that set.</exception>
    internal static byte[] PreparePasswordR4(string? password, string parameterName)
    {
        if (string.IsNullOrEmpty(password))
        {
            return [];
        }

        var bytes = new byte[Math.Min(password.Length, 32)];
        for (var i = 0; i < password.Length; i++)
        {
            var c = password[i];
            if (c is < ' ' or (> '~' and < '\u00A1') or '\u00AD' or > '\u00FF')
            {
                throw new ArgumentException(
                    $"AES-128 passwords must be printable Latin-1 (PDFDocEncoding); U+{(int)c:X4} is not. Use AES-256 for other characters.",
                    parameterName);
            }

            if (i < bytes.Length)
            {
                bytes[i] = (byte)c;
            }
        }

        return bytes;
    }

    /// <summary>Algorithm 2, step a: the password cut or padded to 32 bytes with the padding string.</summary>
    internal static byte[] Pad(ReadOnlySpan<byte> password)
    {
        var padded = new byte[32];
        var length = Math.Min(password.Length, 32);
        password[..length].CopyTo(padded);
        PasswordPadding.AsSpan(0, 32 - length).CopyTo(padded.AsSpan(length));
        return padded;
    }

    /// <summary>Algorithm 3 (revision 4): the <c>/O</c> value from the padded owner and user passwords.</summary>
    internal static byte[] ComputeOwnerValueR4(byte[] paddedOwner, byte[] paddedUser) =>
        Rc4Rounds(OwnerKeyR4(paddedOwner), paddedUser, decrypt: false);

    /// <summary>Algorithm 2 (revision 4): the 16-byte file key from the padded user password.</summary>
    internal static byte[] ComputeFileKeyR4(byte[] paddedUser, byte[] o, int p, byte[] documentId, bool encryptMetadata)
    {
        var input = new List<byte>(32 + 32 + 4 + documentId.Length + 4);
        input.AddRange(paddedUser);
        input.AddRange(o.AsSpan(0, 32));
        var pBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(pBytes, p);
        input.AddRange(pBytes);
        input.AddRange(documentId);
        if (!encryptMetadata)
        {
            input.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        }

#pragma warning disable CA5351 // ISO 32000 mandates MD5 for revision 4 key derivation.
        var hash = MD5.HashData([.. input]);
        for (var i = 0; i < 50; i++)
        {
            hash = MD5.HashData(hash);
        }
#pragma warning restore CA5351

        return hash;
    }

    /// <summary>Algorithm 5 (revision 4): the <c>/U</c> value (16 bytes of hash, then 16 bytes of padding).</summary>
    internal static byte[] ComputeUserValueR4(byte[] fileKey, byte[] documentId)
    {
#pragma warning disable CA5351 // ISO 32000 mandates MD5 for revision 4 key derivation.
        var hash = MD5.HashData([.. PasswordPadding, .. documentId]);
#pragma warning restore CA5351
        var u = new byte[32];
        Rc4Rounds(fileKey, hash, decrypt: false).CopyTo(u, 0);
        return u;
    }

    private static StandardSecurityHandler? AuthenticateR6(StandardSecurityParameters parameters, string? password)
    {
        if (parameters.O.Length < 48 || parameters.U.Length < 48 || parameters.OE is not { Length: 32 }
            || parameters.UE is not { Length: 32 })
        {
            throw new InvalidDataException("The revision 6 /O, /U, /OE or /UE entry has the wrong length.");
        }

        byte[] prepared;
        try
        {
            prepared = PreparePasswordR6(password, nameof(password));
        }
        catch (ArgumentException)
        {
            return null;
        }

        var o = parameters.O.AsSpan(0, 48);
        var u = parameters.U.AsSpan(0, 48);
        byte[] fileKey;
        bool isOwner;
        if (CryptographicOperations.FixedTimeEquals(HashR6(prepared, o[32..40], u), o[..32]))
        {
            fileKey = AesNoPadding(HashR6(prepared, o[40..48], u), parameters.OE, encrypt: false);
            isOwner = true;
        }
        else if (CryptographicOperations.FixedTimeEquals(HashR6(prepared, u[32..40], []), u[..32]))
        {
            fileKey = AesNoPadding(HashR6(prepared, u[40..48], []), parameters.UE, encrypt: false);
            isOwner = false;
        }
        else
        {
            return null;
        }

        // Algorithm 13: /Perms, decrypted with the file key, repeats /P; a mismatch means the file was tampered with.
        if (parameters.Perms is { Length: >= 16 } perms)
        {
            using var aes = Aes.Create();
            aes.Key = fileKey;
            var plain = aes.DecryptEcb(perms.AsSpan(0, 16), PaddingMode.None);
            if (!plain.AsSpan(9, 3).SequenceEqual("adb"u8) || BinaryPrimitives.ReadInt32LittleEndian(plain) != parameters.P)
            {
                throw new InvalidDataException("The /Perms entry does not match /P; the file may have been tampered with.");
            }
        }

        return new StandardSecurityHandler(parameters, fileKey, isOwner);
    }

    private static StandardSecurityHandler? AuthenticateR4(StandardSecurityParameters parameters, string? password)
    {
        if (parameters.O.Length < 32 || parameters.U.Length < 16)
        {
            throw new InvalidDataException("The revision 4 /O or /U entry is too short.");
        }

        byte[] padded;
        try
        {
            padded = Pad(PreparePasswordR4(password, nameof(password)));
        }
        catch (ArgumentException)
        {
            return null;
        }

        // Algorithm 7: as the owner password, /O decrypts to the padded user password.
        var userFromOwner = Rc4Rounds(OwnerKeyR4(padded), parameters.O.AsSpan(0, 32).ToArray(), decrypt: true);
        if (TryUserPasswordR4(parameters, userFromOwner) is { } ownerKey)
        {
            return new StandardSecurityHandler(parameters, ownerKey, isOwner: true);
        }

        // Algorithm 6: as the user password.
        return TryUserPasswordR4(parameters, padded) is { } userKey
            ? new StandardSecurityHandler(parameters, userKey, isOwner: false)
            : null;
    }

    private static byte[]? TryUserPasswordR4(StandardSecurityParameters parameters, byte[] paddedUser)
    {
        var fileKey = ComputeFileKeyR4(paddedUser, parameters.O, parameters.P, parameters.DocumentId, parameters.EncryptMetadata);
        var u = ComputeUserValueR4(fileKey, parameters.DocumentId);
        return CryptographicOperations.FixedTimeEquals(u.AsSpan(0, 16), parameters.U.AsSpan(0, 16)) ? fileKey : null;
    }

    /// <summary>Algorithm 3, steps a-d: the RC4 key from the padded owner password.</summary>
    private static byte[] OwnerKeyR4(byte[] paddedOwner)
    {
#pragma warning disable CA5351 // ISO 32000 mandates MD5 for revision 4 key derivation.
        var hash = MD5.HashData(paddedOwner);
        for (var i = 0; i < 50; i++)
        {
            hash = MD5.HashData(hash);
        }
#pragma warning restore CA5351

        return hash;
    }

    /// <summary>
    /// The 20 RC4 passes of Algorithms 3, 5 and 7: with the key, then with each byte of the key XORed with 1 to 19
    /// (in reverse order to decrypt).
    /// </summary>
    private static byte[] Rc4Rounds(byte[] key, byte[] data, bool decrypt)
    {
        var result = data;
        var roundKey = new byte[key.Length];
        for (var step = 0; step < 20; step++)
        {
            var round = decrypt ? 19 - step : step;
            for (var j = 0; j < key.Length; j++)
            {
                roundKey[j] = (byte)(key[j] ^ round);
            }

            result = Rc4(roundKey, result);
        }

        return result;
    }

    private static byte[] Rc4(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        Span<byte> s = stackalloc byte[256];
        for (var i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return output;
    }

    /// <summary>AES-256-CBC with a zero IV and no padding, for <c>/OE</c> and <c>/UE</c>.</summary>
    private static byte[] AesNoPadding(byte[] key, byte[] data, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        ReadOnlySpan<byte> iv = stackalloc byte[16];
        return encrypt ? aes.EncryptCbc(data, iv, PaddingMode.None) : aes.DecryptCbc(data, iv, PaddingMode.None);
    }

    /// <summary>
    /// The key for one object's strings and streams: the file key for revision 6; for revision 4 (Algorithm 1) the MD5
    /// of the file key, the low three bytes of the object number, the low two bytes of the generation and "sAlT".
    /// </summary>
    private byte[] ObjectKey(int objectNumber, int generation)
    {
        if (Revision == 6)
        {
            return FileKey;
        }

        byte[] input =
        [
            .. FileKey,
            (byte)objectNumber, (byte)(objectNumber >> 8), (byte)(objectNumber >> 16),
            (byte)generation, (byte)(generation >> 8),
            .. "sAlT"u8,
        ];
#pragma warning disable CA5351 // ISO 32000 mandates MD5 for revision 4 object keys.
        return MD5.HashData(input);
#pragma warning restore CA5351
    }

    /// <summary>RFC 3454, table C.1.2: non-ASCII space characters (U+200B is left to <see cref="MapsToNothing"/>).</summary>
    private static bool IsNonAsciiSpace(int c) =>
        c is 0x00A0 or 0x1680 or (>= 0x2000 and <= 0x200A) or 0x202F or 0x205F or 0x3000;

    /// <summary>RFC 3454, table B.1: characters commonly mapped to nothing.</summary>
    private static bool MapsToNothing(int c) =>
        c is 0x00AD or 0x034F or 0x1806 or (>= 0x180B and <= 0x180D) or (>= 0x200B and <= 0x200D) or 0x2060
            or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF;

    /// <summary>RFC 4013, 2.3: RFC 3454 tables C.1.2 and C.2.1-C.9 (C.5, surrogates, cannot occur in a <see cref="System.Text.Rune"/>).</summary>
    private static bool IsProhibited(System.Text.Rune rune)
    {
        var c = rune.Value;
        return IsNonAsciiSpace(c)
            || System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.PrivateUse
            || c is 0x06DD or 0x070F or 0x180E or 0x200C or 0x200D or 0x2028 or 0x2029 or (>= 0x2060 and <= 0x2063)
                or (>= 0x206A and <= 0x206F) or 0xFEFF or (>= 0xFFF9 and <= 0xFFFD) or (>= 0x1D173 and <= 0x1D17A)
            || c is (>= 0xE000 and <= 0xF8FF) or (>= 0xF0000 and <= 0xFFFFD) or (>= 0x100000 and <= 0x10FFFD)
            || c is (>= 0xFDD0 and <= 0xFDEF) || (c & 0xFFFE) == 0xFFFE
            || c is (>= 0x2FF0 and <= 0x2FFB)
            || c is 0x0340 or 0x0341 or 0x200E or 0x200F or (>= 0x202A and <= 0x202E)
            || c is 0xE0001 or (>= 0xE0020 and <= 0xE007F);
    }
}
