using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RaiCore.Pdf.Security;

namespace RaiCore.Pdf.IO;

/// <summary>
/// Byte-level output sink for PDF serialization. Tracks the current offset (needed for the
/// cross-reference table) and feeds everything written into a hash used for the document /ID.
/// </summary>
internal sealed class PdfOutput : IDisposable
{
    private readonly Stream _stream;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public PdfOutput(Stream stream)
    {
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _stream = stream;
    }

    /// <summary>Number of bytes written so far.</summary>
    public long Position { get; private set; }

    /// <summary>
    /// The security handler that encrypts the strings and streams of the indirect object being written, or <c>null</c>
    /// when they are written as they are (no encryption, the <c>/Encrypt</c> dictionary itself, or the trailer).
    /// </summary>
    public StandardSecurityHandler? Encryption { get; private set; }

    /// <summary>The number of the indirect object being written; with <see cref="Generation"/> it selects the object key.</summary>
    public int ObjectNumber { get; private set; }

    /// <summary>The generation of the indirect object being written.</summary>
    public int Generation { get; private set; }

    /// <summary>Starts writing indirect object <paramref name="objectNumber"/>, encrypted with <paramref name="encryption"/> if not <c>null</c>.</summary>
    public void BeginObject(int objectNumber, int generation, StandardSecurityHandler? encryption)
    {
        ObjectNumber = objectNumber;
        Generation = generation;
        Encryption = encryption;
    }

    /// <summary>Ends the indirect object: what follows (such as the trailer) is not encrypted.</summary>
    public void EndObject() => BeginObject(0, 0, null);

    public void Write(ReadOnlySpan<byte> bytes)
    {
        _stream.Write(bytes);
        _hash.AppendData(bytes);
        Position += bytes.Length;
    }

    public void WriteByte(byte value) => Write([value]);

    /// <summary>Writes ASCII text. Callers must only pass characters in the 0–127 range.</summary>
    public void WriteAscii(string text)
    {
        Span<byte> buffer = text.Length <= 256 ? stackalloc byte[text.Length] : new byte[text.Length];
        Encoding.ASCII.GetBytes(text, buffer);
        Write(buffer);
    }

    public void WriteInteger(long value) => WriteAscii(value.ToString(CultureInfo.InvariantCulture));

    public void WriteReal(double value) => WriteAscii(PdfNumberFormat.Format(value));

    /// <summary>Hash of every byte written so far, without finalizing the running hash.</summary>
    public byte[] PeekHash() => _hash.GetCurrentHash();

    public void Dispose() => _hash.Dispose();
}

/// <summary>Formats numbers the way PDF expects them: invariant culture, no exponent, at most 5 decimals.</summary>
internal static class PdfNumberFormat
{
    public static string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "PDF numbers must be finite.");
        }

        var text = Math.Round(value, 5).ToString("0.#####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }
}
