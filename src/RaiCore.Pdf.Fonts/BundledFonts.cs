namespace RaiCore.Pdf.Fonts;

/// <summary>
/// Open fonts shipped inside this package, so output looks the same on every machine.
/// Liberation Sans, Serif and Mono (SIL Open Font License 1.1) have the same character widths as
/// Arial/Helvetica, Times New Roman and Courier New, so text breaks into lines exactly as with those fonts.
/// </summary>
/// <remarks>
/// Thread-safe. Each bundled font is read from the assembly and parsed once per process, the first time any collection
/// uses it; every collection made with <see cref="AddBundledFonts"/> or <see cref="CreateCollection"/> then returns that
/// same <see cref="PdfFont"/> instance. Creating a collection per document is therefore cheap, and documents converted
/// at the same time on different threads share the font data instead of each holding a copy.
/// </remarks>
public static class BundledFonts
{
    private static readonly (string Family, string File)[] Families =
    [
        ("Liberation Sans", "LiberationSans"),
        ("Liberation Serif", "LiberationSerif"),
        ("Liberation Mono", "LiberationMono"),
    ];

    private static readonly (string Suffix, int Weight, bool Italic)[] Styles =
    [
        ("Regular", 400, false),
        ("Bold", 700, false),
        ("Italic", 400, true),
        ("BoldItalic", 700, true),
    ];

    /// <summary>The bundled faces, each family of <see cref="Families"/> in each style of <see cref="Styles"/>, in order. Each is loaded once, on first use, and then shared by every collection.</summary>
    private static readonly Lazy<PdfFont>[] Faces =
    [
        .. Families.SelectMany(family => Styles.Select(style =>
        {
            var resource = $"RaiCore.Pdf.Fonts.Liberation.{family.File}-{style.Suffix}.ttf";
            return new Lazy<PdfFont>(() => PdfFont.Load(ReadResource(resource)), LazyThreadSafetyMode.ExecutionAndPublication);
        })),
    ];

    /// <summary>Adds Liberation Sans, Serif and Mono (4 styles each). Font data is loaded on first use and shared by all collections.</summary>
    public static FontCollection AddBundledFonts(this FontCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var i = 0;
        foreach (var (family, _) in Families)
        {
            foreach (var (_, weight, italic) in Styles)
            {
                var face = Faces[i++];
                collection.AddFont(() => face.Value, family, weight, italic);
            }
        }

        return collection;
    }

    /// <summary>A collection containing only the bundled fonts: identical output on every machine.</summary>
    public static FontCollection CreateCollection() => new FontCollection().AddBundledFonts();

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(BundledFonts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded font '{name}' is missing from {typeof(BundledFonts).Assembly.GetName().Name}.");
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }
}
