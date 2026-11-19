using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace RaiCore.Pdf.Email.Mime;

/// <summary>
/// Text in the charsets messages use: Unicode, the ISO-8859 and Windows code pages, and the Asian multi-byte ones
/// (ISO-2022-JP, Shift_JIS, EUC-JP, GBK, GB18030, Big5, EUC-KR). The code page tables come with .NET
/// (<see cref="CodePagesEncodingProvider"/>, asked directly so nothing is registered for the whole process) and work
/// with invariant globalization.
/// </summary>
internal static partial class Charsets
{
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;

    /// <summary>
    /// Decodes text in the named charset. Text that names none, an unknown one or ASCII while it has 8-bit bytes is
    /// decoded as UTF-8 if it is valid UTF-8, else as Windows-1252 (which also stands for ISO-8859-1 and ASCII, as
    /// browsers read them). For HTML without a charset, a <c>&lt;meta charset&gt;</c> in its first kilobytes counts.
    /// A byte order mark is removed; bytes the charset cannot decode become U+FFFD.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> data, string? charset, bool html)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(data[3..]);
        }

        if (string.IsNullOrWhiteSpace(charset) && html)
        {
            charset = MetaCharset(data);
        }

        var encoding = Find(charset);
        if (encoding is null || (encoding.CodePage == 1252 && IsAscii(charset)))
        {
            encoding = Utf8.IsValid(data) ? Encoding.UTF8 : Windows1252;
        }

        var text = encoding.GetString(data);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>The encoding for a charset name, or <c>null</c> if it is unknown.</summary>
    public static Encoding? Find(string? charset)
    {
        var name = charset?.Trim().Trim('"', '\'').ToLowerInvariant();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        switch (name)
        {
            case "utf-8" or "utf8" or "unicode-1-1-utf-8" or "x-unicode20utf8":
                return Encoding.UTF8;
            case "us-ascii" or "ascii" or "iso-8859-1" or "iso8859-1" or "iso_8859-1" or "latin1" or "l1" or "cp819" or "ibm819" or "x-user-defined":
                return Windows1252; // A superset, as browsers read them: mislabeled Windows-1252 text is common.
            case "utf-7" or "unicode-1-1-utf-7" or "csunicode11utf7":
#pragma warning disable SYSLIB0001 // UTF-7 is obsolete, but some old messages use it.
                return new UTF7Encoding();
#pragma warning restore SYSLIB0001
            case "gb2312" or "gbk" or "x-gbk" or "cp936" or "csgb2312" or "chinese":
                name = "gb18030"; // A superset of both.
                break;
            case "euc-kr" or "ks_c_5601-1987" or "ks_c_5601" or "x-windows-949" or "cp949":
                name = "ks_c_5601-1987"; // Windows-949, a superset of EUC-KR.
                break;
            case "shift-jis" or "sjis" or "ms_kanji" or "cp932" or "windows-31j" or "x-sjis":
                name = "shift_jis";
                break;
        }

        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(name) ?? Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsAscii(string? charset) =>
        charset?.Trim().Trim('"', '\'').ToLowerInvariant() is "us-ascii" or "ascii";

    /// <summary>The charset a <c>&lt;meta&gt;</c> element in the first kilobytes of an HTML document declares, or <c>null</c>.</summary>
    private static string? MetaCharset(ReadOnlySpan<byte> data)
    {
        var head = Encoding.Latin1.GetString(data[..Math.Min(data.Length, 4096)]);
        var match = MetaCharsetRegex().Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""<meta\b[^>]{0,512}?charset\s*=\s*["']?([A-Za-z0-9_:.()-]{1,40})""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaCharsetRegex();
}
