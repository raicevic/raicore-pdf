using System.Xml.Linq;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf.Xfa.Packets;

/// <summary>
/// The XFA packets of a form (XFA 3.3, chapter "XDP"): the template, the data (datasets), the configuration and the
/// locale set, read from a PDF file's <c>/AcroForm /XFA</c> (a stream, or an array of packet names and streams whose
/// contents together are one XDP document) or from an XDP file; plus what the PDF file adds (its title, and the fonts it
/// embeds for the form).
/// </summary>
internal sealed class XfaPackets
{
    /// <summary>The most embedded fonts read from a PDF file.</summary>
    private const int MaxEmbeddedFonts = 64;

    public required XElement Template { get; init; }

    public XElement? Datasets { get; init; }

    public XElement? Config { get; init; }

    public XElement? LocaleSet { get; init; }

    /// <summary>The PDF file's title, if it has one.</summary>
    public string? Title { get; init; }

    /// <summary>The TrueType and OpenType fonts the PDF file embeds in the AcroForm's default resources (not subsets).</summary>
    public IReadOnlyList<PdfFont> Fonts { get; init; } = [];

    /// <summary>Reads the packets of a PDF or XDP file.</summary>
    /// <exception cref="XfaFormatException">The file has no XFA form, or its XFA is malformed.</exception>
    public static XfaPackets Read(byte[] data, XfaXml xml, XfaConverterOptions options, Action<XfaDiagnosticKind, string>? report)
    {
        if (IsPdf(data))
        {
            return ReadPdf(data, xml, options, report);
        }

        return FromXdp(xml.Load(data), null, []);
    }

    /// <summary>Whether data is a PDF file: <c>%PDF-</c> in its first kilobyte.</summary>
    internal static bool IsPdf(byte[] data)
    {
        ReadOnlySpan<byte> marker = "%PDF-"u8;
        return data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf(marker) >= 0;
    }

    private static XfaPackets ReadPdf(byte[] data, XfaXml xml, XfaConverterOptions options, Action<XfaDiagnosticKind, string>? report)
    {
        var reader = PdfReader.Open(data, options.Password);
        var store = reader.Store;
        var form = reader.Resolve(reader.Catalog["AcroForm"]) as PdfDictionary;
        var xfa = form is null ? null : reader.Resolve(form["XFA"]);
        var limit = options.Limits.MaxDecompressedBytes;
        byte[] xdp;
        switch (xfa)
        {
            case PdfStreamObject stream:
                xdp = store.Decode(stream);
                break;
            case PdfArray array:
            {
                using var buffer = new MemoryStream();
                for (var i = 1; i < array.Items.Count; i += 2)
                {
                    if (reader.Resolve(array.Items[i]) is PdfStreamObject packet)
                    {
                        var bytes = store.Decode(packet);
                        if (buffer.Length + bytes.Length > limit)
                        {
                            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxDecompressedBytes), limit,
                                $"The XFA packets decompress to more than ResourceLimits.MaxDecompressedBytes ({limit}).");
                        }

                        buffer.Write(bytes);
                    }
                }

                xdp = buffer.ToArray();
                break;
            }

            default:
                throw new XfaFormatException("The PDF file has no XFA form (the AcroForm has no /XFA).");
        }

        var title = reader.Trailer["Info"] is { } info && reader.Resolve(info) is PdfDictionary dictionary
            && reader.Resolve(dictionary["Title"]) is PdfString text ? PdfText.Decode(text.Bytes) : null;
        var fonts = options.UseEmbeddedFonts && form is not null ? EmbeddedFonts(reader, form, options.Limits, report) : [];
        return FromXdp(xml.Load(xdp), string.IsNullOrWhiteSpace(title) ? null : title.Trim(), fonts);
    }

    /// <summary>The whole TrueType and OpenType fonts in the AcroForm's default resources (<c>/DR /Font</c>).</summary>
    private static List<PdfFont> EmbeddedFonts(PdfReader reader, PdfDictionary form, ResourceLimits limits, Action<XfaDiagnosticKind, string>? report)
    {
        var fonts = new List<PdfFont>();
        if (reader.Resolve(form["DR"]) is not PdfDictionary resources || reader.Resolve(resources["Font"]) is not PdfDictionary dictionary)
        {
            return fonts;
        }

        foreach (var (_, value) in dictionary.Entries.Take(MaxEmbeddedFonts))
        {
            if (reader.Resolve(value) is not PdfDictionary font)
            {
                continue;
            }

            if (reader.Resolve(font["DescendantFonts"]) is PdfArray { Items.Count: > 0 } descendants && reader.Resolve(descendants.Items[0]) is PdfDictionary descendant)
            {
                font = descendant;
            }

            if (reader.Resolve(font["FontDescriptor"]) is not PdfDictionary descriptor
                || (reader.Resolve(descriptor["FontFile2"]) ?? reader.Resolve(descriptor["FontFile3"])) is not PdfStreamObject file)
            {
                continue;
            }

            var name = reader.Resolve(descriptor["FontName"]) is PdfName n ? n.Value : string.Empty;
            if (name.Length > 7 && name[6] == '+')
            {
                continue; // A subset has only the glyphs of the placeholder pages.
            }

            try
            {
                var bytes = reader.Store.Decode(file);
                if (bytes.Length > limits.MaxFontBytes)
                {
                    continue;
                }

                fonts.Add(PdfFont.Load(bytes));
            }
            catch (Exception e) when (e is PdfException or ArgumentException or InvalidDataException)
            {
                report?.Invoke(XfaDiagnosticKind.FontSubstituted, $"The embedded font {name} cannot be read: {e.Message}");
            }
        }

        return fonts;
    }

    /// <summary>The packets of an XDP document (or of a bare template).</summary>
    internal static XfaPackets FromXdp(XElement root, string? title, IReadOnlyList<PdfFont> fonts)
    {
        if (root.Name.LocalName == "template")
        {
            return new XfaPackets { Template = root, Title = title, Fonts = fonts };
        }

        XElement? Packet(string name) => root.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        var template = Packet("template") ?? throw new XfaFormatException("The XFA form has no template packet.");
        return new XfaPackets
        {
            Template = template,
            Datasets = Packet("datasets"),
            Config = Packet("config"),
            LocaleSet = Packet("localeSet"),
            Title = title,
            Fonts = fonts,
        };
    }
}
