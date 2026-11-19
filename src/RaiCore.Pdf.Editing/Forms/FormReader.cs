using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf.Editing;

/// <summary>Reads a document's interactive form (ISO 32000-2, 12.7): the field tree, values, flags and widgets.</summary>
internal static class FormReader
{
    /// <summary>The deepest field tree followed.</summary>
    private const int MaxDepth = 32;

    /// <summary>The most field tree nodes visited (a guard against loops and hostile files).</summary>
    private const int MaxNodes = 100_000;

    // Field flags (ISO 32000-2, tables 226, 229, 231).
    private const int ReadOnly = 1 << 0;
    private const int Required = 1 << 1;
    private const int Radio = 1 << 15;
    private const int PushButton = 1 << 16;
    private const int Combo = 1 << 17;

    // Annotation flags (ISO 32000-2, table 167).
    private const int Hidden = 1 << 1;
    private const int NoView = 1 << 5;

    public static PdfForm Read(ObjectTable table, PdfDictionary catalog, IReadOnlyList<PdfReference> pages)
    {
        var form = table.Dictionary(catalog["AcroForm"]);
        var xfa = form?["XFA"] is null || table.Resolve(form["XFA"]) is null ? PdfXfaKind.None
            : table.Resolve(catalog["NeedsRendering"]) is PdfBoolean { Value: true } ? PdfXfaKind.Dynamic
            : PdfXfaKind.Static;

        // Widgets are found on pages through their /Annots (a widget's /P is optional and sometimes wrong).
        var pageOf = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < pages.Count; i++)
        {
            foreach (var annotation in table.Array(table.Dictionary(pages[i])!["Annots"])?.Items ?? [])
            {
                if (table.Dictionary(annotation) is { } widget)
                {
                    pageOf.TryAdd(widget, i);
                }
            }
        }

        var fields = new List<PdfFormField>();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var root in table.Array(form?["Fields"])?.Items ?? [])
        {
            Walk(root, null, Inherited.None, 0);
        }

        return new PdfForm(form is not null, xfa, fields);

        void Walk(PdfObject? node, string? parentName, Inherited inherited, int depth)
        {
            if (depth > MaxDepth || table.Dictionary(node) is not { } field || !visited.Add(field) || visited.Count > MaxNodes)
            {
                return;
            }

            var partial = table.Resolve(field["T"]) is PdfString t ? PdfText.Decode(t.Bytes) : null;
            var name = partial is null ? parentName : parentName is null ? partial : parentName + "." + partial;
            inherited = inherited.With(field, table);

            var kids = (table.Array(field["Kids"])?.Items ?? []).Select(k => (Reference: k, Dictionary: table.Dictionary(k))).Where(k => k.Dictionary is not null).ToList();
            var childFields = kids.Where(k => k.Dictionary!["T"] is not null).ToList();
            var widgets = kids.Where(k => k.Dictionary!["T"] is null).Select(k => k.Dictionary!).ToList();
            if (table.Resolve(field["Subtype"]) is PdfName { Value: "Widget" })
            {
                widgets.Insert(0, field);
            }

            foreach (var child in childFields)
            {
                Walk(child.Reference, name, inherited, depth + 1);
            }

            if (childFields.Count > 0 && widgets.Count == 0)
            {
                return; // A non-terminal field: its kids are fields.
            }

            var placed = widgets.Where(pageOf.ContainsKey).Select(w => Widget(table, w, pageOf[w])).ToList();
            if (widgets.Count > 0 && placed.Count == 0)
            {
                return; // Its widgets are all on pages that are not in the document (any more).
            }

            if (inherited.Type is not { } type)
            {
                return; // Not a field: no field type anywhere up the tree.
            }

            var flags = inherited.Flags;
            var fieldType = type switch
            {
                "Tx" => PdfFieldType.Text,
                "Btn" when (flags & PushButton) != 0 => PdfFieldType.PushButton,
                "Btn" when (flags & Radio) != 0 => PdfFieldType.RadioButton,
                "Btn" => PdfFieldType.CheckBox,
                "Ch" when (flags & Combo) != 0 => PdfFieldType.ComboBox,
                "Ch" => PdfFieldType.ListBox,
                "Sig" => PdfFieldType.Signature,
                _ => (PdfFieldType?)null,
            };
            if (fieldType is null)
            {
                return;
            }

            fields.Add(new PdfFormField(
                name ?? string.Empty,
                fieldType.Value,
                Values(table, fieldType.Value, inherited.Value),
                fieldType is PdfFieldType.ListBox or PdfFieldType.ComboBox ? Options(table, field, inherited) : [],
                (flags & ReadOnly) != 0,
                (flags & Required) != 0,
                placed));
        }
    }

    private static PdfFormWidget Widget(ObjectTable table, PdfDictionary widget, int pageIndex)
    {
        var rectangle = table.Array(widget["Rect"]) is { Items.Count: 4 } rect
            && rect.Items.Select(i => Number(table.Resolve(i))).ToArray() is [{ } x1, { } y1, { } x2, { } y2]
                ? new PdfRectangle(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1))
                : default;
        var flags = table.Resolve(widget["F"]) is PdfInteger f ? f.Value : 0;
        var state = table.Resolve(widget["AS"]) is PdfName s ? s.Value : null;
        return new PdfFormWidget(pageIndex, rectangle, (flags & (Hidden | NoView)) != 0, state);
    }

    private static IReadOnlyList<string> Values(ObjectTable table, PdfFieldType type, PdfObject? value) => type switch
    {
        PdfFieldType.PushButton => [],
        PdfFieldType.CheckBox or PdfFieldType.RadioButton => [table.Resolve(value) is PdfName state ? state.Value : "Off"],
        PdfFieldType.Signature => table.Resolve(value) is PdfDictionary ? ["Signed"] : [],
        _ => table.Resolve(value) switch
        {
            PdfString text => [PdfText.Decode(text.Bytes)],
            PdfName name => [name.Value],
            PdfArray array => [.. array.Items.Select(table.Resolve).OfType<PdfString>().Select(s => PdfText.Decode(s.Bytes))],
            PdfStreamObject rich => [System.Text.Encoding.UTF8.GetString(table.DecodeOrEmpty(rich))],
            _ => [],
        },
    };

    /// <summary>The options of a choice field as shown: <c>/Opt</c> entries are strings or <c>[export shown]</c> pairs.</summary>
    private static IReadOnlyList<string> Options(ObjectTable table, PdfDictionary field, Inherited inherited) =>
        table.Array(field["Opt"] ?? inherited.Options) is { } options
            ? [.. options.Items.Select(o => table.Resolve(o) switch
            {
                PdfString text => PdfText.Decode(text.Bytes),
                PdfArray { Items.Count: >= 2 } pair when table.Resolve(pair.Items[1]) is PdfString shown => PdfText.Decode(shown.Bytes),
                _ => null,
            }).OfType<string>()]
            : [];

    private static double? Number(PdfObject? obj) => obj switch
    {
        PdfInteger i => i.Value,
        PdfReal r when double.IsFinite(r.Value) => r.Value,
        _ => null,
    };

    /// <summary>The inheritable field attributes (ISO 32000-2, table 226): type, flags, value and options.</summary>
    private readonly record struct Inherited(string? Type, int Flags, PdfObject? Value, PdfObject? Options)
    {
        public static Inherited None => default;

        public Inherited With(PdfDictionary field, ObjectTable table) => new(
            table.Resolve(field["FT"]) is PdfName type ? type.Value : Type,
            table.Resolve(field["Ff"]) is PdfInteger flags ? (int)flags.Value : Flags,
            field["V"] ?? Value,
            field["Opt"] ?? Options);
    }
}
