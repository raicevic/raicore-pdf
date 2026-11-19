using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf.Editing;

/// <summary>
/// Flattens a document's interactive form (see <see cref="PdfEditor.FlattenForm"/>): draws each widget's normal
/// appearance into its page, making one where there is none (or the form asks viewers to make new ones), and removes the
/// widgets, the fields and the form.
/// </summary>
internal sealed class FormFlattener(ObjectTable table, PdfDictionary catalog, IReadOnlyList<PdfReference> pages)
{
    // Field flags (ISO 32000-2, tables 226, 228, 229, 231).
    private const int Radio = 1 << 15;
    private const int PushButton = 1 << 16;
    private const int Multiline = 1 << 12;
    private const int Password = 1 << 13;
    private const int Comb = 1 << 24;
    private const int Combo = 1 << 17;

    /// <summary>The space between a field's border and its text, in points.</summary>
    private const double Padding = 2;

    /// <summary>The deepest a widget's field parents are followed for inherited attributes.</summary>
    private const int MaxDepth = 32;

    private readonly PdfDictionary? _form = table.Dictionary(catalog["AcroForm"]);

    public PdfFlattenResult Flatten()
    {
        if (_form is null)
        {
            return new PdfFlattenResult(0, 0, 0);
        }

        if (_form["XFA"] is not null && table.Resolve(catalog["NeedsRendering"]) is PdfBoolean { Value: true })
        {
            throw new NotSupportedException(
                "The form is a dynamic XFA form: its pages are laid out from XML by the viewer, which flattening does not do.");
        }

        var regenerate = table.Resolve(_form["NeedAppearances"]) is PdfBoolean { Value: true };
        var result = new Flattener(table, pages).Flatten(
            annotation => table.Resolve(annotation["Subtype"]) is PdfName { Value: "Widget" },
            widget => Appearance(widget, regenerate));

        // The form goes with its fields; a static XFA form goes with it. Viewers have nothing left to fill.
        catalog["AcroForm"] = null;
        catalog["NeedsRendering"] = null;
        return result;
    }

    /// <summary>A widget's appearance: its normal appearance in its current state, or one made from the field's value.</summary>
    private (PdfReference? Form, bool Generated) Appearance(PdfDictionary widget, bool regenerate)
    {
        var field = Field.Of(widget, table);
        var isText = field.Type is "Tx" or "Ch";
        if (!(regenerate && isText) && NormalAppearance(widget, field) is { } existing)
        {
            return (existing, false);
        }

        var made = field.Type switch
        {
            "Tx" or "Ch" => TextAppearance(widget, field),
            "Btn" when (field.Flags & PushButton) == 0 => ButtonAppearance(widget, field),
            _ => null,
        };
        return made is null ? (NormalAppearance(widget, field), false) : (table.Add(made), true);
    }

    /// <summary>The normal appearance (<c>/AP /N</c>): a stream, or a dictionary of states picked by <c>/AS</c> (else the field's value).</summary>
    private PdfReference? NormalAppearance(PdfDictionary widget, Field field)
    {
        var normal = table.Dictionary(widget["AP"])?["N"];
        switch (table.Resolve(normal))
        {
            case PdfStreamObject:
                return normal as PdfReference;
            case PdfDictionary states:
                var state = table.Resolve(widget["AS"]) as PdfName ?? table.Resolve(field.Value) as PdfName;
                return state is not null && states[state] is PdfReference chosen && table.Resolve(chosen) is PdfStreamObject ? chosen : null;
            default:
                return null;
        }
    }

    /// <summary>The appearance of a text field or a choice field (ISO 32000-2, 12.7.4.3): its value in the font of its <c>/DA</c>.</summary>
    private PdfStreamObject? TextAppearance(PdfDictionary widget, Field field)
    {
        if (Size(widget) is not ({ } width, { } height))
        {
            return null;
        }

        var (fontName, fontSize, color) = DefaultAppearance(field.DefaultAppearance ?? (table.Resolve(_form!["DA"]) as PdfString)?.Bytes.ToArray());
        var (fontObject, metrics) = Font(fontName);
        var listBox = field.Type == "Ch" && (field.Flags & Combo) == 0;
        var values = Values(field.Value);
        var text = new StringBuilder();
        Frame(widget, width, height, text);
        text.Append("/Tx BMC\nq\n").Append(Invariant($"{N(Padding)} {N(Padding)} {N(width - (2 * Padding))} {N(height - (2 * Padding))} re W n\n"));

        if (listBox)
        {
            var size = fontSize > 0 ? fontSize : 12;
            var top = table.Resolve(field.Attribute("TI")) is PdfInteger ti ? (int)ti.Value : 0;
            var options = Options(field);
            var y = height - Padding;
            for (var i = Math.Max(0, top); i < options.Count && y - (size * 1.15) > 0; i++)
            {
                y -= size * 1.15;
                if (values.Contains(options[i].Export) || values.Contains(options[i].Shown))
                {
                    text.Append(Invariant($"0.6 0.75 0.86 rg {N(Padding)} {N(y - (size * 0.25))} {N(width - (2 * Padding))} {N(size * 1.15)} re f\n"));
                }

                Line(text, fontName, size, color, options[i].Shown, Padding, y + (size * 0.207));
            }
        }
        else
        {
            var value = values.FirstOrDefault() ?? string.Empty;
            if ((field.Flags & Password) != 0)
            {
                value = new string('*', value.Length);
            }

            var maxLength = table.Resolve(field.Attribute("MaxLen")) is PdfInteger max ? (int)max.Value : 0;
            var alignment = table.Resolve(field.Attribute("Q")) is PdfInteger q ? (int)q.Value : 0;
            if ((field.Flags & Multiline) != 0 && field.Type == "Tx")
            {
                var size = fontSize > 0 ? fontSize : 12;
                var y = height - Padding - (size * 0.718);
                foreach (var line in Wrap(value, width - (2 * Padding), size, metrics))
                {
                    Line(text, fontName, size, color, line, X(line, size), y);
                    y -= size * 1.15;
                }
            }
            else if ((field.Flags & Comb) != 0 && maxLength > 0 && field.Type == "Tx")
            {
                var size = fontSize > 0 ? fontSize : Math.Min((height - (2 * Padding)) * 0.75, 12);
                var cell = width / maxLength;
                for (var i = 0; i < value.Length && i < maxLength; i++)
                {
                    var c = value[i].ToString();
                    Line(text, fontName, size, color, c, (cell * i) + ((cell - Measure(c, size, metrics)) / 2), Baseline(size));
                }
            }
            else
            {
                var size = fontSize > 0 ? fontSize : AutoSize(value, width, height, metrics);
                Line(text, fontName, size, color, value, X(value, size), Baseline(size));
            }

            double X(string line, double size) => alignment switch
            {
                1 => (width - Measure(line, size, metrics)) / 2,
                2 => width - Padding - Measure(line, size, metrics),
                _ => Padding,
            };
        }

        text.Append("Q\nEMC\n");
        return Form(width, height, text.ToString(), fontName, fontObject);

        double Baseline(double size) => ((height - (size * 0.925)) / 2) + (size * 0.207);
    }

    /// <summary>The appearance of a check box or radio button that has none: its check (<c>/MK /CA</c>) when it is on.</summary>
    private PdfStreamObject? ButtonAppearance(PdfDictionary widget, Field field)
    {
        if (Size(widget) is not ({ } width, { } height))
        {
            return null;
        }

        var state = table.Resolve(widget["AS"]) as PdfName ?? table.Resolve(field.Value) as PdfName;
        var text = new StringBuilder();
        Frame(widget, width, height, text);
        if (state is not null && state.Value != "Off")
        {
            var characteristics = table.Dictionary(widget["MK"]);
            var check = table.Resolve(characteristics?["CA"]) is PdfString ca && ca.Bytes.Length > 0
                ? Encoding.Latin1.GetString(ca.Bytes)
                : (field.Flags & Radio) != 0 ? "l" : "4";
            var size = Math.Min(width, height) * 0.8;
            var x = (width - (size * 0.8)) / 2;
            var y = ((height - (size * 0.7)) / 2) + (size * 0.05);
            text.Append(Invariant($"BT /ZaDb {N(size)} Tf 0 g {N(x)} {N(y)} Td ({Escape(Encoding.Latin1.GetBytes(check))}) Tj ET\n"));
        }

        var zapf = new PdfDictionary
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("Type1"),
            ["BaseFont"] = new PdfName("ZapfDingbats"),
        };
        return Form(width, height, text.ToString(), "ZaDb", zapf);
    }

    /// <summary>The background and border of <c>/MK</c> and <c>/BS</c>.</summary>
    private void Frame(PdfDictionary widget, double width, double height, StringBuilder text)
    {
        var characteristics = table.Dictionary(widget["MK"]);
        if (Color(characteristics?["BG"], fill: true) is { } background)
        {
            text.Append(Invariant($"q {background} 0 0 {N(width)} {N(height)} re f Q\n"));
        }

        var borderWidth = table.Resolve(table.Dictionary(widget["BS"])?["W"]) is PdfInteger or PdfReal ? Number(table.Resolve(table.Dictionary(widget["BS"])!["W"])) ?? 1 : 1;
        if (Color(characteristics?["BC"], fill: false) is { } border && borderWidth > 0)
        {
            var half = borderWidth / 2;
            text.Append(Invariant($"q {border} {N(borderWidth)} w {N(half)} {N(half)} {N(width - borderWidth)} {N(height - borderWidth)} re S Q\n"));
        }
    }

    /// <summary>A color array as a fill or stroke operator (1 component gray, 3 RGB, 4 CMYK), or null.</summary>
    private string? Color(PdfObject? value, bool fill)
    {
        if (table.Array(value) is not { } array)
        {
            return null;
        }

        var components = array.Items.Select(i => Number(table.Resolve(i))).ToArray();
        if (components.Any(c => c is null))
        {
            return null;
        }

        var numbers = string.Join(' ', components.Select(c => N(c!.Value)));
        return components.Length switch
        {
            1 => numbers + (fill ? " g" : " G"),
            3 => numbers + (fill ? " rg" : " RG"),
            4 => numbers + (fill ? " k" : " K"),
            _ => null,
        };
    }

    private (double? Width, double? Height) Size(PdfDictionary widget) =>
        table.Array(widget["Rect"]) is { Items.Count: 4 } rect && rect.Items.Select(i => Number(table.Resolve(i))).ToArray() is [{ } x1, { } y1, { } x2, { } y2]
            && Math.Abs(x2 - x1) > 0 && Math.Abs(y2 - y1) > 0
            ? (Math.Abs(x2 - x1), Math.Abs(y2 - y1))
            : (null, null);

    /// <summary>
    /// The font name, size and color of a default appearance string such as <c>/Helv 0 Tf 0 g</c>. Size 0 means "fit
    /// the field".
    /// </summary>
    private static (string Font, double Size, string Color) DefaultAppearance(byte[]? appearance)
    {
        var tokens = Encoding.Latin1.GetString(appearance ?? []).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var (font, size, color) = ("Helv", 0.0, "0 g");
        for (var i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i])
            {
                case "Tf" when i >= 2 && tokens[i - 2].StartsWith('/'):
                    font = tokens[i - 2][1..];
                    size = double.TryParse(tokens[i - 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && double.IsFinite(s) ? Math.Max(0, s) : 0;
                    break;
                case "g" when i >= 1:
                    color = string.Join(' ', tokens[(i - 1)..(i + 1)]);
                    break;
                case "rg" when i >= 3:
                    color = string.Join(' ', tokens[(i - 3)..(i + 1)]);
                    break;
                case "k" when i >= 4:
                    color = string.Join(' ', tokens[(i - 4)..(i + 1)]);
                    break;
            }
        }

        return (font, size, color);
    }

    /// <summary>The font a default appearance names, from the form's default resources (else Helvetica), and the standard font whose widths measure it.</summary>
    private (PdfObject Font, PdfFont Metrics) Font(string name)
    {
        var font = table.Dictionary(_form!["DR"])?["Font"] is { } fonts ? table.Dictionary(fonts)?[name] : null;
        var baseFont = (table.Resolve(table.Dictionary(font)?["BaseFont"]) as PdfName)?.Value ?? "Helvetica";
        var standard = baseFont.Replace(",", "-", StringComparison.Ordinal) switch
        {
            var n when n.Contains("Courier", StringComparison.OrdinalIgnoreCase) => n.Contains("Bold", StringComparison.OrdinalIgnoreCase) ? StandardFont.CourierBold : StandardFont.Courier,
            var n when n.Contains("Times", StringComparison.OrdinalIgnoreCase) => n.Contains("Bold", StringComparison.OrdinalIgnoreCase) ? StandardFont.TimesBold : StandardFont.TimesRoman,
            var n => n.Contains("Bold", StringComparison.OrdinalIgnoreCase) ? StandardFont.HelveticaBold : StandardFont.Helvetica,
        };
        return (font ?? new PdfDictionary
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("Type1"),
            ["BaseFont"] = new PdfName("Helvetica"),
            ["Encoding"] = new PdfName("WinAnsiEncoding"),
        }, PdfFont.Standard(standard));
    }

    private static double Measure(string text, double size, PdfFont metrics) => metrics.MeasureText(text, size);

    /// <summary>The size that fits a single line in the field: by its height, made smaller when the text is too wide.</summary>
    private static double AutoSize(string value, double width, double height, PdfFont metrics)
    {
        var size = Math.Clamp((height - (2 * Padding)) * 0.75, 4, 12);
        var textWidth = Measure(value, size, metrics);
        var room = width - (2 * Padding);
        return textWidth > room && textWidth > 0 ? Math.Max(4, size * room / textWidth) : size;
    }

    private static IEnumerable<string> Wrap(string value, double width, double size, PdfFont metrics)
    {
        foreach (var paragraph in value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\r', '\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(candidate, size, metrics) > width)
                {
                    yield return line.ToString();
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }

            yield return line.ToString();
        }
    }

    private static void Line(StringBuilder text, string font, double size, string color, string line, double x, double y) =>
        text.Append(Invariant($"BT /{font} {N(size)} Tf {color} {N(x)} {N(y)} Td ({Escape(WinAnsiEncoding.Encode(line))}) Tj ET\n"));

    private List<string> Values(PdfObject? value) => table.Resolve(value) switch
    {
        PdfString text => [PdfText.Decode(text.Bytes)],
        PdfName name => [name.Value],
        PdfArray array => [.. array.Items.Select(table.Resolve).OfType<PdfString>().Select(s => PdfText.Decode(s.Bytes))],
        _ => [],
    };

    private List<(string Export, string Shown)> Options(Field field) =>
        [.. (table.Array(field.Attribute("Opt"))?.Items ?? []).Select(o => table.Resolve(o) switch
        {
            PdfString text => (PdfText.Decode(text.Bytes), PdfText.Decode(text.Bytes)),
            PdfArray { Items.Count: >= 2 } pair when table.Resolve(pair.Items[0]) is PdfString export && table.Resolve(pair.Items[1]) is PdfString shown
                => (PdfText.Decode(export.Bytes), PdfText.Decode(shown.Bytes)),
            _ => (string.Empty, string.Empty),
        })];

    private static PdfStreamObject Form(double width, double height, string content, string fontName, PdfObject font) => new(
        Encoding.Latin1.GetBytes(content),
        new PdfDictionary
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Form"),
            ["BBox"] = PdfArray.OfNumbers(0, 0, width, height),
            ["Resources"] = new PdfDictionary { ["Font"] = new PdfDictionary { [fontName] = font } },
        });

    private static string Escape(byte[] bytes)
    {
        var text = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            text.Append(b switch
            {
                (byte)'(' => "\\(",
                (byte)')' => "\\)",
                (byte)'\\' => "\\\\",
                < 32 or > 126 => "\\" + Convert.ToString(b, 8).PadLeft(3, '0'),
                _ => ((char)b).ToString(),
            });
        }

        return text.ToString();
    }

    private static double? Number(PdfObject? obj) => obj switch
    {
        PdfInteger i => i.Value,
        PdfReal r when double.IsFinite(r.Value) => r.Value,
        _ => null,
    };

    private static string N(double value) => PdfNumberFormat.Format(value);

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>A widget's field attributes, inherited from its parents (ISO 32000-2, table 226).</summary>
    private sealed class Field
    {
        private readonly List<PdfDictionary> _chain = [];

        private Field(ObjectTable table)
        {
            Table = table;
        }

        private ObjectTable Table { get; }

        public string? Type => (Table.Resolve(Attribute("FT")) as PdfName)?.Value;

        public int Flags => Table.Resolve(Attribute("Ff")) is PdfInteger flags ? (int)flags.Value : 0;

        public PdfObject? Value => Attribute("V");

        public byte[]? DefaultAppearance => (Table.Resolve(Attribute("DA")) as PdfString)?.Bytes.ToArray();

        public static Field Of(PdfDictionary widget, ObjectTable table)
        {
            var field = new Field(table);
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            for (var node = widget; node is not null && field._chain.Count < MaxDepth && seen.Add(node); node = table.Dictionary(node["Parent"]))
            {
                field._chain.Add(node);
            }

            return field;
        }

        /// <summary>The attribute from the widget or its nearest parent that has it.</summary>
        public PdfObject? Attribute(string key) => _chain.Select(d => d[key]).FirstOrDefault(v => v is not null);
    }
}
