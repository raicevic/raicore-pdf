using System.Reflection;

namespace RaiCore.Pdf.Fonts;

/// <summary>
/// Open fonts shipped inside this package, so output looks the same on every machine.
/// Liberation Sans, Serif and Mono (SIL Open Font License 1.1) have the same character widths as
/// Arial/Helvetica, Times New Roman and Courier New, so text breaks into lines exactly as with those fonts.
/// </summary>
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

    /// <summary>Adds Liberation Sans, Serif and Mono (4 styles each). Font data is loaded on first use.</summary>
    public static FontCollection AddBundledFonts(this FontCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        foreach (var (family, file) in Families)
        {
            foreach (var (suffix, weight, italic) in Styles)
            {
                var resource = $"RaiCore.Pdf.Fonts.Liberation.{file}-{suffix}.ttf";
                collection.AddFont(() => ReadResource(resource), family, weight, italic);
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
