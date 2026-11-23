using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Email.Msg;

/// <summary>
/// The body of an Outlook message from its RTF: the HTML it encapsulates (<c>\fromhtml1</c>, [MS-OXRTF] 2.1.3), or else
/// its text. Not an RTF renderer: formatting of RTF that is not encapsulated HTML is not kept (that is RTF to PDF).
/// </summary>
internal static class RtfBody
{
    /// <summary>The RTF's encapsulated HTML, or <c>null</c> when it encapsulates none.</summary>
    public static string? Html(string rtf) => rtf.Contains("\\fromhtml", StringComparison.Ordinal) ? Run(rtf, html: true) : null;

    /// <summary>The RTF's text: paragraphs and line breaks as newlines, tabs kept, everything else left out.</summary>
    public static string Text(string rtf) => Run(rtf, html: false);

    /// <summary>
    /// Walks the RTF's groups. In <paramref name="html"/> mode, text inside <c>\htmlrtf</c> ... <c>\htmlrtf0</c> is
    /// suppressed and <c>{\*\htmltag ...}</c> groups count as text; otherwise ignorable destinations (<c>{\* ...}</c>)
    /// and the font, color and style tables are skipped.
    /// </summary>
    private static string Run(string rtf, bool html)
    {
        var output = new StringBuilder(rtf.Length / 2);
        var bytes = new List<byte>();
        var encoding = Encoding(1252);
        var groups = new Stack<(bool Skip, int UnicodeSkip, bool HtmlRtf)>();
        var (skip, unicodeSkip, htmlRtf) = (false, 1, false);
        var pendingSkip = 0;
        var i = 0;

        void Flush()
        {
            if (bytes.Count > 0)
            {
                output.Append(encoding.GetString([.. bytes]));
                bytes.Clear();
            }
        }

        bool Emitting() => !skip && !htmlRtf;

        while (i < rtf.Length)
        {
            var c = rtf[i];
            switch (c)
            {
                case '{':
                    Flush();
                    groups.Push((skip, unicodeSkip, htmlRtf));
                    if (groups.Count > 4096)
                    {
                        throw new MessageFormatException("The RTF nests groups more than 4096 deep.");
                    }

                    i++;
                    break;
                case '}':
                    Flush();
                    (skip, unicodeSkip, htmlRtf) = groups.Count > 0 ? groups.Pop() : (skip, unicodeSkip, htmlRtf);
                    i++;
                    break;
                case '\\':
                    i++;
                    if (i >= rtf.Length)
                    {
                        break;
                    }

                    var next = rtf[i];
                    if (next == '\'' && i + 2 < rtf.Length)
                    {
                        // A byte in the document's code page.
                        if (byte.TryParse(rtf.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                        {
                            if (pendingSkip > 0)
                            {
                                pendingSkip--;
                            }
                            else if (Emitting())
                            {
                                bytes.Add(value);
                            }
                        }

                        i += 3;
                        break;
                    }

                    if (!char.IsAsciiLetter(next))
                    {
                        // A control symbol: an escaped character, or \* (ignorable destination), \~ (no-break space).
                        Flush();
                        i++;
                        if (next == '*')
                        {
                            var word = i < rtf.Length && rtf[i] == '\\' ? Word(rtf, i + 1) : default;
                            if (!(html && word.Name == "htmltag"))
                            {
                                skip = true;
                            }
                        }
                        else if (Emitting())
                        {
                            output.Append(next switch
                            {
                                '~' => "\u00A0",
                                '-' or '_' => string.Empty,
                                '\r' or '\n' => html ? string.Empty : "\n",
                                _ => next.ToString(),
                            });
                        }

                        break;
                    }

                    var control = Word(rtf, i);
                    i = control.End;
                    Flush();
                    switch (control.Name)
                    {
                        case "ansicpg" when control.Parameter is { } page:
                            encoding = Encoding(page);
                            break;
                        case "uc" when control.Parameter is { } count:
                            unicodeSkip = Math.Clamp(count, 0, 16);
                            break;
                        case "u" when control.Parameter is { } code:
                            if (Emitting())
                            {
                                output.Append((char)(code < 0 ? code + 65536 : code));
                            }

                            pendingSkip = unicodeSkip;
                            break;
                        case "htmlrtf":
                            htmlRtf = html && control.Parameter != 0;
                            break;
                        case "par" or "line" or "sect" or "page" when Emitting():
                            output.Append(html ? "\r\n" : "\n");
                            break;
                        case "tab" when Emitting():
                            output.Append('\t');
                            break;
                        case "fonttbl" or "colortbl" or "stylesheet" or "info" or "pict" or "object" or "header" or "footer" or "listtable" or "listoverridetable" or "rsidtbl" or "generator" or "xmlnstbl" or "themedata" or "colorschememapping" or "latentstyles" or "datastore" or "mmathPr":
                            skip = true;
                            break;
                        case "htmltag":
                            htmlRtf = false; // Its text is HTML, kept even inside \htmlrtf.
                            break;
                        case "bin" when control.Parameter is > 0 and var length:
                            i = (int)Math.Min(rtf.Length, (long)i + length);
                            break;
                    }

                    break;
                case '\r' or '\n':
                    i++;
                    break;
                default:
                    if (pendingSkip > 0)
                    {
                        pendingSkip--;
                    }
                    else if (Emitting())
                    {
                        Flush();
                        output.Append(c);
                    }

                    i++;
                    break;
            }
        }

        Flush();
        return output.ToString();
    }

    /// <summary>A control word at <paramref name="start"/> (after the backslash): its letters, its optional signed number, and where it ends (after one delimiting space).</summary>
    private static (string Name, int? Parameter, int End) Word(string rtf, int start)
    {
        var i = start;
        while (i < rtf.Length && char.IsAsciiLetter(rtf[i]) && i - start < 32)
        {
            i++;
        }

        var name = rtf[start..i];
        var numberStart = i;
        if (i < rtf.Length && rtf[i] == '-')
        {
            i++;
        }

        while (i < rtf.Length && char.IsAsciiDigit(rtf[i]) && i - numberStart < 10)
        {
            i++;
        }

        int? parameter = i > numberStart && int.TryParse(rtf.AsSpan(numberStart, i - numberStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
        if (i < rtf.Length && rtf[i] == ' ')
        {
            i++;
        }

        return (name, parameter, i);
    }

    private static Encoding Encoding(int codePage) =>
        (codePage is > 0 and < 65535 ? CodePagesEncodingProvider.Instance.GetEncoding(codePage) : null)
        ?? (codePage == 65001 ? System.Text.Encoding.UTF8 : null)
        ?? CodePagesEncodingProvider.Instance.GetEncoding(1252)
        ?? System.Text.Encoding.Latin1;
}
