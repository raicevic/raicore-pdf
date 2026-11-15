using System.Security.Cryptography;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Security;

namespace RaiCore.Pdf.Reading;

/// <summary>
/// Decrypts the objects of an encrypted file as they are loaded (ISO 32000-2, 7.6): the standard security handler's
/// encryption dictionary, the crypt filters for strings and streams, and the objects that stay unencrypted (the
/// encryption dictionary itself, cross-reference streams, the objects inside object streams, unencrypted metadata).
/// </summary>
internal sealed class PdfDecryption
{
    private readonly StandardSecurityHandler _handler;
    private readonly IReadOnlyDictionary<string, CryptMethod> _filters;
    private readonly int _encryptObject;

    private PdfDecryption(StandardSecurityHandler handler, CryptMethod strings, CryptMethod streams, IReadOnlyDictionary<string, CryptMethod> filters, int encryptObject)
    {
        _handler = handler;
        Strings = strings;
        Streams = streams;
        _filters = filters;
        _encryptObject = encryptObject;
    }

    /// <summary>How strings are encrypted (<c>/StrF</c>).</summary>
    public CryptMethod Strings { get; }

    /// <summary>How streams are encrypted (<c>/StmF</c>).</summary>
    public CryptMethod Streams { get; }

    /// <summary>Whether the password given was the owner password.</summary>
    public bool IsOwner => _handler.IsOwner;

    /// <summary>The permission bits of <c>/P</c>.</summary>
    public PdfPermissions Permissions => (PdfPermissions)_handler.Parameters.P & PdfPermissions.All;

    /// <summary>
    /// Reads the trailer's <c>/Encrypt</c> dictionary and checks <paramref name="password"/>; returns <c>null</c> when
    /// the password is neither the owner nor the user password.
    /// </summary>
    /// <exception cref="NotSupportedException">A security handler, revision or crypt filter that is not supported.</exception>
    /// <exception cref="PdfFormatException">The encryption dictionary is malformed.</exception>
    public static PdfDecryption? Open(PdfObjectStore store, string? password)
    {
        var encryptEntry = store.Trailer["Encrypt"];
        var encrypt = store.Resolve(encryptEntry) as PdfDictionary
            ?? throw new PdfFormatException("The /Encrypt entry is not a dictionary.");
        if (encrypt["Filter"] is not PdfName { Value: var filter } || filter != "Standard")
        {
            throw new NotSupportedException(
                $"The PDF file is encrypted with the {(encrypt["Filter"] as PdfName)?.Value ?? "unknown"} security handler; only the standard (password) security handler is supported.");
        }

        var v = Integer(store, encrypt["V"]) ?? 0;
        var revision = Integer(store, encrypt["R"]) ?? throw new PdfFormatException("The encryption dictionary has no /R.");
        if (revision == 5)
        {
            throw new NotSupportedException("Security handler revision 5 (a deprecated AES-256 draft) is not supported; revision 6 is.");
        }

        var filters = new Dictionary<string, CryptMethod>(StringComparer.Ordinal) { ["Identity"] = CryptMethod.None };
        CryptMethod strings, streams;
        int keyLength;
        switch (v)
        {
            case 1 or 2:
                keyLength = revision == 2 ? 5 : (int)Math.Clamp((Integer(store, encrypt["Length"]) ?? 40) / 8, 5, 16);
                strings = streams = CryptMethod.Rc4;
                break;
            case 4 or 5:
                keyLength = v == 5 ? 32 : 16;
                if (store.Resolve(encrypt["CF"]) is PdfDictionary cryptFilters)
                {
                    foreach (var (name, value) in cryptFilters.Entries)
                    {
                        filters[name.Value] = store.Resolve(value) is PdfDictionary cf ? Method(store, cf) : CryptMethod.None;
                    }
                }

                strings = Named(encrypt["StrF"]);
                streams = Named(encrypt["StmF"]);
                break;
            default:
                throw new NotSupportedException($"Encryption algorithm /V {v} is not supported.");
        }

        var parameters = new StandardSecurityParameters(
            (int)revision,
            Bytes(store, encrypt["O"]) ?? throw new PdfFormatException("The encryption dictionary has no /O."),
            Bytes(store, encrypt["U"]) ?? throw new PdfFormatException("The encryption dictionary has no /U."),
            Bytes(store, encrypt["OE"]),
            Bytes(store, encrypt["UE"]),
            Bytes(store, encrypt["Perms"]),
            unchecked((int)(Integer(store, encrypt["P"]) ?? throw new PdfFormatException("The encryption dictionary has no /P."))),
            store.Resolve(store.Trailer["ID"]) is PdfArray { Items: [var first, ..] } && store.Resolve(first) is PdfString id ? id.Bytes.ToArray() : [],
            store.Resolve(encrypt["EncryptMetadata"]) is not PdfBoolean { Value: false },
            keyLength);

        StandardSecurityHandler? handler;
        try
        {
            handler = StandardSecurityHandler.Authenticate(parameters, password);
        }
        catch (InvalidDataException ex)
        {
            throw new PdfFormatException($"The encryption dictionary is malformed: {ex.Message}", ex);
        }

        return handler is null
            ? null
            : new PdfDecryption(handler, strings, streams, filters, encryptEntry is PdfReference reference ? reference.ObjectNumber : 0);

        CryptMethod Named(PdfObject? entry) =>
            store.Resolve(entry) is PdfName { Value: var name }
                ? filters.TryGetValue(name, out var method) ? method : throw new NotSupportedException($"The crypt filter /{name} is not defined.")
                : CryptMethod.None; // The default is /Identity.
    }

    /// <summary>
    /// Decrypts the strings (and stream data) of indirect object <paramref name="number"/>, loaded from the file itself
    /// (not from an object stream). The encryption dictionary, cross-reference streams and unencrypted metadata are
    /// left as they are.
    /// </summary>
    public PdfObject Decrypt(PdfObject obj, int number, int generation)
    {
        if (number == _encryptObject)
        {
            return obj;
        }

        if (obj is PdfStreamObject stream)
        {
            var dictionary = stream.Dictionary;
            if (dictionary["Type"] is PdfName { Value: "XRef" })
            {
                return obj;
            }

            DecryptStrings(dictionary, number, generation);
            var method = StreamMethod(dictionary);
            return method == CryptMethod.None ? stream : new PdfStreamObject(Decrypt(stream.Data, number, generation, method), dictionary);
        }

        return DecryptStrings(obj, number, generation);
    }

    /// <summary>
    /// How a stream is encrypted: by <c>/StmF</c>, by its own <c>/Crypt</c> filter (which is then taken out of its
    /// filters), or not at all for unencrypted metadata.
    /// </summary>
    private CryptMethod StreamMethod(PdfDictionary dictionary)
    {
        if (!_handler.Parameters.EncryptMetadata && dictionary["Type"] is PdfName { Value: "Metadata" })
        {
            return CryptMethod.None;
        }

        var filters = dictionary["Filter"] switch
        {
            PdfName name => [name],
            PdfArray array => array.Items.ToList(),
            _ => [],
        };
        if (filters is not [PdfName { Value: "Crypt" }, ..])
        {
            return Streams;
        }

        // A /Crypt filter comes first and names its crypt filter in its decode parameters (default /Identity).
        var parameters = dictionary["DecodeParms"] switch
        {
            PdfArray array => array.Items.FirstOrDefault(),
            var single => single,
        };
        var cryptName = (parameters as PdfDictionary)?["Name"] is PdfName { Value: var n } ? n : "Identity";
        RemoveFirstFilter(dictionary);
        return _filters.TryGetValue(cryptName, out var method) ? method : CryptMethod.None;
    }

    private static void RemoveFirstFilter(PdfDictionary dictionary)
    {
        if (dictionary["Filter"] is PdfArray { Items.Count: > 1 } filters)
        {
            dictionary["Filter"] = new PdfArray(filters.Items.Skip(1));
            if (dictionary["DecodeParms"] is PdfArray { Items.Count: > 1 } parameters)
            {
                dictionary["DecodeParms"] = new PdfArray(parameters.Items.Skip(1));
            }
            else
            {
                dictionary["DecodeParms"] = null;
            }
        }
        else
        {
            dictionary["Filter"] = null;
            dictionary["DecodeParms"] = null;
        }
    }

    /// <summary>Replaces every string in <paramref name="obj"/> (nested in dictionaries and arrays) by its decryption.</summary>
    private PdfObject DecryptStrings(PdfObject obj, int number, int generation)
    {
        if (Strings == CryptMethod.None)
        {
            return obj;
        }

        switch (obj)
        {
            case PdfString text:
                return new PdfString(Decrypt(text.Bytes, number, generation, Strings), text.IsHex);
            case PdfDictionary dictionary:
                foreach (var (key, value) in dictionary.Entries.ToList())
                {
                    if (value is PdfString or PdfDictionary or PdfArray)
                    {
                        dictionary[key] = DecryptStrings(value, number, generation);
                    }
                }

                return dictionary;
            case PdfArray array:
                for (var i = 0; i < array.Items.Count; i++)
                {
                    if (array.Items[i] is PdfString or PdfDictionary or PdfArray)
                    {
                        array.Items[i] = DecryptStrings(array.Items[i], number, generation);
                    }
                }

                return array;
            default:
                return obj;
        }
    }

    /// <summary>Decrypts data; data that does not decrypt (damaged, or not encrypted after all) is kept as it is.</summary>
    private byte[] Decrypt(ReadOnlySpan<byte> data, int number, int generation, CryptMethod method)
    {
        try
        {
            return _handler.Decrypt(data, number, generation, method);
        }
        catch (CryptographicException)
        {
            return data.ToArray();
        }
    }

    private static CryptMethod Method(PdfObjectStore store, PdfDictionary cryptFilter) =>
        store.Resolve(cryptFilter["CFM"]) switch
        {
            PdfName { Value: "V2" } => CryptMethod.Rc4,
            PdfName { Value: "AESV2" or "AESV3" } => CryptMethod.Aes,
            PdfName { Value: "None" } or null => CryptMethod.None,
            PdfName { Value: var other } => throw new NotSupportedException($"The crypt filter method /{other} is not supported."),
            _ => CryptMethod.None,
        };

    private static long? Integer(PdfObjectStore store, PdfObject? value) => store.Resolve(value) is PdfInteger integer ? integer.Value : null;

    private static byte[]? Bytes(PdfObjectStore store, PdfObject? value) => store.Resolve(value) is PdfString text ? text.Bytes.ToArray() : null;
}
