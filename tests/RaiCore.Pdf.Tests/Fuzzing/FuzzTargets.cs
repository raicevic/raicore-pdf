using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Fonts.OpenType;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Images;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Tests.Fuzzing;

/// <summary>
/// The fuzz targets: each feeds one input to a parser or decoder and uses the result the way the library does
/// (layout, embedding, writing). Shared by the fuzzer (tests/RaiCore.Pdf.Fuzz) and the corpus replay test.
/// </summary>
/// <remarks>
/// An input may make a target fail only with a documented exception (<see cref="IsExpected"/>): malformed data,
/// an exceeded resource limit or cancellation. Anything else (an <see cref="IndexOutOfRangeException"/>, a
/// <see cref="NullReferenceException"/>, an <see cref="OverflowException"/>, running out of memory, a hang or a stack
/// overflow) is a bug.
/// </remarks>
public static class FuzzTargets
{
    /// <summary>Tight limits, so that one input stays fast: hostile input must hit them, not stall the fuzzer.</summary>
    public static readonly ResourceLimits Limits = ResourceLimits.Default with
    {
        MaxElements = 20_000,
        MaxTableCells = 100_000,
        MaxImagePixels = 4_000_000,
        MaxResourceBytes = 4 * 1024 * 1024,
        MaxFontBytes = 16 * 1024 * 1024,
        MaxDecompressedBytes = 32 * 1024 * 1024,
        MaxPages = 50,
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>The targets by name: the name is also the folder of their seed corpus.</summary>
    public static IReadOnlyDictionary<string, Action<byte[]>> All { get; } = new Dictionary<string, Action<byte[]>>(StringComparer.Ordinal)
    {
        ["html"] = Html,
        ["css"] = Css,
        ["png"] = input => Image(WithValidPngCrcs(input)),
        ["jpeg"] = Image,
        ["gif"] = Image,
        ["bmp"] = Image,
        ["webp"] = Image,
        ["svg"] = Image,
        ["font"] = Font,
        ["woff"] = Font,
        ["woff2"] = Font,
        ["pdf"] = Pdf,
    };

    /// <summary>Runs a target on one input; expected exceptions are swallowed, anything else is thrown.</summary>
    public static void Run(string target, byte[] input)
    {
        try
        {
            using (Limits.Enforce(CancellationToken.None))
            {
                All[target](input);
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
        }
    }

    /// <summary>Whether an exception is one a target may document for malformed or hostile input.</summary>
    public static bool IsExpected(Exception exception) => exception is ImageFormatException or FontFormatException
        or PdfFormatException or ResourceLimitExceededException or OperationCanceledException or NotSupportedException;

    /// <summary>HTML conversion, with no resources loaded (data: URIs only).</summary>
    public static void Html(byte[] input) => Convert(Encoding.UTF8.GetString(input));

    /// <summary>CSS: the input as a style sheet for a document that uses most kinds of boxes.</summary>
    public static void Css(byte[] input) => Convert($"""
        <style>{Encoding.UTF8.GetString(input)}</style>
        <h1 id="t">Title</h1>
        <div class="a"><p class="b">Some <span>inline <b>text</b></span> and <a href="#t">a link</a>.</p>
        <ul><li>one</li><li class="c">two</li></ul>
        <table><tr><th>H</th><td>1</td></tr><tr><td colspan="2">2</td></tr></table>
        <div style="display: flex"><div>a</div><div>b</div></div>
        <span class="d" style="display: inline-block">box</span></div>
        """);

    /// <summary>An image of any supported format, drawn on a page and written.</summary>
    public static void Image(byte[] input)
    {
        var image = PdfImage.Load(input);
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).Canvas.DrawImage(image, 10, 10, 100, 100);
        document.ToArray();
    }

    /// <summary>A font (TrueType, OpenType, TTC, WOFF, WOFF2): shaped, measured, embedded as a subset and written.</summary>
    public static void Font(byte[] input)
    {
        var font = PdfFont.Load(input);
        if (font is OpenTypePdfFont { Font.EmbeddingRestricted: true })
        {
            return; // Drawing it throws InvalidOperationException by design: its license forbids embedding.
        }

        TextShaper.Shape("Hello, w\u00F6rld! fi 123", font);
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).Canvas.DrawText("Hello, w\u00F6rld! fi 123", font, 12, 10, 10);
        document.ToArray();
    }

    /// <summary>A PDF file: read, every page's content decoded, imported into a new document and written.</summary>
    public static void Pdf(byte[] input)
    {
        var reader = PdfReader.Open(input);
        foreach (var page in reader.Pages)
        {
            page.GetContent();
        }

        var document = new PdfDocument();
        document.ImportPages(reader);
        document.ToArray();
    }

    /// <summary>
    /// The PNG with the CRC of every chunk corrected, so that mutated data gets past the CRC check to the decoder
    /// (random changes would almost always be rejected there).
    /// </summary>
    public static byte[] WithValidPngCrcs(byte[] input)
    {
        var data = (byte[])input.Clone();
        var pos = 8;
        while (pos + 12 <= data.Length)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (length > data.Length - pos - 12)
            {
                break;
            }

            var end = pos + 8 + (int)length;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(end), Crc32(data.AsSpan(pos + 4, 4 + (int)length)));
            pos = end + 4;
        }

        return data;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    private static void Convert(string html) =>
        HtmlToPdf.ConvertToBytes(html, new HtmlConverterOptions { Limits = Limits, ResourceLoader = _ => null });
}
