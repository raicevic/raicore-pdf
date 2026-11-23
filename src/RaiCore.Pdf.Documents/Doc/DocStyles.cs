using System.Buffers.Binary;
using System.Text;

namespace RaiCore.Pdf.Documents.Doc;

/// <summary>
/// The style sheet ([MS-DOC] 2.9.271 STSH) and font table ([MS-DOC] 2.9.284 SttbfFfn): paragraph and character styles
/// with their bases (<c>istdBase</c>), resolved to properties on first use, and the fonts' names.
/// </summary>
internal sealed class DocStyles
{
    /// <summary>The most styles in a chain of bases that are followed.</summary>
    private const int MaxChain = 32;

    private readonly List<Style?> _styles = [];
    private readonly Dictionary<int, (DocPap Pap, DocChp Chp)> _resolved = [];
    private readonly List<string> _fonts = [];
    private readonly List<bool> _symbolFonts = [];

    /// <summary>A style: its kind, base, name and property modifiers.</summary>
    private sealed record Style(int Kind, int Base, int Sti, string Name, byte[] Papx, byte[] Chpx);

    private DocStyles(DocFile file)
    {
        ReadFonts(file.TableData(15));
        ReadStyles(file.TableData(1));
    }

    /// <summary>The default font (<c>rgftcStandardChpStsh[0]</c>).</summary>
    public int DefaultFont { get; private set; }

    public static DocStyles Read(DocFile file) => new(file);

    /// <summary>A font's name, or <c>null</c>.</summary>
    public string? FontName(int font) => font >= 0 && font < _fonts.Count && _fonts[font].Length > 0 ? _fonts[font] : null;

    /// <summary>Whether a font is a symbol font (Symbol, Wingdings and the like, or with the symbol character set).</summary>
    public bool IsSymbolFont(int font) => font >= 0 && font < _symbolFonts.Count && _symbolFonts[font];

    /// <summary>A style's name, or <c>null</c>.</summary>
    public string? Name(int istd) => StyleAt(istd)?.Name;

    /// <summary>The built-in style identifier (<c>sti</c>; 1 to 9 are the headings), or -1.</summary>
    public int Sti(int istd) => StyleAt(istd)?.Sti ?? -1;

    /// <summary>The properties of a paragraph style (with its bases'), or the defaults when it is not defined.</summary>
    public (DocPap Pap, DocChp Chp) Paragraph(int istd)
    {
        if (_resolved.TryGetValue(istd, out var cached))
        {
            return cached;
        }

        var chain = Chain(istd, kind: 1);
        var pap = DocPap.Default;
        var chp = DocChp.Default(DefaultFont);
        foreach (var style in chain)
        {
            DocProperties.ApplyParagraph(ref pap, style.Papx);
            var basis = chp;
            DocProperties.ApplyCharacter(ref chp, style.Chpx, basis);
        }

        pap.Style = istd;
        _resolved[istd] = (pap, chp);
        return (pap, chp);
    }

    /// <summary>Applies a character style (with its bases') to the properties of a paragraph's style.</summary>
    public void ApplyCharacterStyle(ref DocChp chp, int istd)
    {
        foreach (var style in Chain(istd, kind: 2))
        {
            var basis = chp;
            DocProperties.ApplyCharacter(ref chp, style.Chpx, basis);
        }

        chp.CharacterStyle = istd;
    }

    private Style? StyleAt(int istd) => istd >= 0 && istd < _styles.Count ? _styles[istd] : null;

    /// <summary>The styles from the root base to a style of a kind, without loops.</summary>
    private List<Style> Chain(int istd, int kind)
    {
        var chain = new List<Style>();
        var seen = new HashSet<int>();
        while (chain.Count < MaxChain && seen.Add(istd) && StyleAt(istd) is { } style && style.Kind == kind)
        {
            chain.Add(style);
            istd = style.Base;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>The style sheet: its header (the default fonts), then each style's STD.</summary>
    private void ReadStyles(ReadOnlySpan<byte> stsh)
    {
        if (stsh.Length < 2)
        {
            return;
        }

        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(stsh);
        if (headerSize < 14 || 2 + headerSize > stsh.Length)
        {
            return;
        }

        var header = stsh.Slice(2, headerSize);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var baseSize = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        DefaultFont = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);

        var position = 2 + headerSize;
        for (var i = 0; i < count && position + 2 <= stsh.Length; i++)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(stsh[position..]);
            position += 2;
            if (size > stsh.Length - position)
            {
                break;
            }

            _styles.Add(size == 0 ? null : ReadStyle(stsh.Slice(position, size), baseSize));
            position += size;
        }
    }

    /// <summary>A style ([MS-DOC] 2.9.260 STD): StdfBase, its name (Xstz), and its UPXs by kind.</summary>
    private static Style? ReadStyle(ReadOnlySpan<byte> std, int baseSize)
    {
        if (std.Length < 10 || baseSize < 10 || baseSize > std.Length)
        {
            return null;
        }

        var sti = BinaryPrimitives.ReadUInt16LittleEndian(std) & 0x0FFF;
        var kindAndBase = BinaryPrimitives.ReadUInt16LittleEndian(std[2..]);
        var kind = kindAndBase & 0x000F;
        var istdBase = kindAndBase >> 4;
        var upxCount = BinaryPrimitives.ReadUInt16LittleEndian(std[4..]) & 0x000F;
        var position = baseSize;
        var name = string.Empty;
        if (position + 2 <= std.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(std[position..]);
            position += 2;
            if (2 * length <= std.Length - position)
            {
                name = Encoding.Unicode.GetString(std.Slice(position, 2 * length));
                position += 2 * (length + 1);
            }
        }

        byte[] papx = [], chpx = [];
        for (var u = 0; u < upxCount; u++)
        {
            position += position & 1;
            if (position + 2 > std.Length)
            {
                break;
            }

            var size = BinaryPrimitives.ReadUInt16LittleEndian(std[position..]);
            position += 2;
            if (size > std.Length - position)
            {
                break;
            }

            var upx = std.Slice(position, size);
            position += size;

            // Paragraph styles: UpxPapx (istd, then modifiers) and UpxChpx; character styles: UpxChpx; table styles:
            // UpxTapx, UpxPapx and UpxChpx.
            var role = (kind, u) switch
            {
                (1, 0) or (3, 1) => 1,
                (1, 1) or (2, 0) or (3, 2) => 2,
                _ => 0,
            };
            if (role == 1 && upx.Length >= 2)
            {
                papx = upx[2..].ToArray();
            }
            else if (role == 2)
            {
                chpx = upx.ToArray();
            }
        }

        return new Style(kind, istdBase, sti, name, papx, chpx);
    }

    /// <summary>The font table: each FFN's name after its fixed part (39 bytes), and whether it is a symbol font.</summary>
    private void ReadFonts(ReadOnlySpan<byte> sttb)
    {
        if (sttb.Length < 4)
        {
            return;
        }

        var extended = BinaryPrimitives.ReadUInt16LittleEndian(sttb) == 0xFFFF;
        var position = extended ? 2 : 0;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(sttb[position..]);
        position += 4;
        for (var i = 0; i < count && position < sttb.Length; i++)
        {
            var size = sttb[position++];
            if (size > sttb.Length - position)
            {
                break;
            }

            var ffn = sttb.Slice(position, size);
            position += size;
            var name = string.Empty;
            var charset = ffn.Length > 3 ? ffn[3] : 0;
            if (ffn.Length > 39)
            {
                var chars = ffn[39..];
                var length = 0;
                while (2 * length + 1 < chars.Length && BinaryPrimitives.ReadUInt16LittleEndian(chars[(2 * length)..]) != 0)
                {
                    length++;
                }

                name = Encoding.Unicode.GetString(chars[..(2 * length)]);
            }

            _fonts.Add(name);
            _symbolFonts.Add(charset == 2 || name.Equals("Symbol", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Webdings", StringComparison.OrdinalIgnoreCase));
        }
    }
}
