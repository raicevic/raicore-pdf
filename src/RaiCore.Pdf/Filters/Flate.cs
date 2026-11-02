using System.IO.Compression;

namespace RaiCore.Pdf.Filters;

/// <summary>The <c>/FlateDecode</c> filter. PDF expects zlib framing (RFC 1950), which <see cref="ZLibStream"/> provides.</summary>
internal static class Flate
{
    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return buffer.ToArray();
    }

    public static byte[] Decode(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}
