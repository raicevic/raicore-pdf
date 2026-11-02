using RaiCore.Pdf.Fonts.OpenType;

namespace RaiCore.Pdf.Fonts;

/// <summary>
/// A set of fonts that can be looked up by family name, weight and style, using the CSS font matching rules.
/// Fonts are indexed from their headers and only fully loaded when first used.
/// </summary>
/// <remarks>
/// Output depends on the fonts available: a collection with <see cref="AddSystemFonts"/> renders differently on
/// different machines. For reproducible output, use only fonts you add explicitly (for example the bundled
/// Liberation fonts from the RaiCore.Pdf.Fonts package). This type is thread-safe.
/// </remarks>
public sealed class FontCollection
{
    private readonly Lock _lock = new();
    private readonly List<Face> _faces = [];
    private readonly Dictionary<string, List<string>> _substitutions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Metric-compatible substitutes keep line and page breaks where the document author expected them.
        ["Arial"] = ["Liberation Sans", "Arimo"],
        ["Helvetica"] = ["Liberation Sans", "Arimo", "Arial"],
        ["Arimo"] = ["Liberation Sans"],
        ["Times New Roman"] = ["Liberation Serif", "Tinos"],
        ["Times"] = ["Liberation Serif", "Tinos", "Times New Roman"],
        ["Tinos"] = ["Liberation Serif"],
        ["Courier New"] = ["Liberation Mono", "Cousine"],
        ["Courier"] = ["Liberation Mono", "Cousine", "Courier New"],
        ["Cousine"] = ["Liberation Mono"],

        // CSS generic families.
        ["sans-serif"] = ["Liberation Sans", "Arial", "Helvetica", "DejaVu Sans"],
        ["serif"] = ["Liberation Serif", "Times New Roman", "Times", "DejaVu Serif"],
        ["monospace"] = ["Liberation Mono", "Courier New", "Courier", "DejaVu Sans Mono"],
        ["system-ui"] = ["Segoe UI", "Helvetica Neue", "Ubuntu", "Liberation Sans"],
    };

    /// <summary>Family names in the collection, sorted.</summary>
    public IReadOnlyList<string> Families
    {
        get
        {
            lock (_lock)
            {
                return [.. _faces.Select(f => f.Family).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    /// <summary>Number of faces (individual weight/style combinations) in the collection.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _faces.Count;
            }
        }
    }

    /// <summary>Directories searched by <see cref="AddSystemFonts"/> on the current operating system.</summary>
    public static IReadOnlyList<string> SystemFontDirectories
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsWindows())
            {
                return
                [
                    Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
                ];
            }

            if (OperatingSystem.IsMacOS())
            {
                return ["/System/Library/Fonts", "/Library/Fonts", Path.Combine(home, "Library", "Fonts")];
            }

            return
            [
                "/usr/share/fonts", "/usr/local/share/fonts",
                Path.Combine(home, ".fonts"), Path.Combine(home, ".local", "share", "fonts"),
            ];
        }
    }

    /// <summary>Adds the fonts installed on this machine. Unreadable files are skipped.</summary>
    public FontCollection AddSystemFonts()
    {
        foreach (var directory in SystemFontDirectories)
        {
            AddDirectory(directory);
        }

        return this;
    }

    /// <summary>Adds every font file (<c>.ttf .otf .ttc .otc .woff .woff2</c>) in a directory. Unreadable files are skipped.</summary>
    public FontCollection AddDirectory(string directory, bool recursive = true)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!Directory.Exists(directory))
        {
            return this;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            if (!IsFontFile(file))
            {
                continue;
            }

            try
            {
                AddFile(file);
            }
            catch (Exception ex) when (ex is FontFormatException or IOException or UnauthorizedAccessException)
            {
                // Broken or unsupported fonts are common in system folders; skip them.
            }
        }

        return this;
    }

    /// <summary>Adds all faces in a font file.</summary>
    public FontCollection AddFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        IReadOnlyList<ScannedFace> faces;
        using (var stream = File.OpenRead(path))
        {
            faces = FontFaceScanner.Scan(stream);
        }

        foreach (var face in faces)
        {
            AddFace(face, () => File.ReadAllBytes(path));
        }

        return this;
    }

    /// <summary>Adds all faces in font data held in memory.</summary>
    public FontCollection AddFont(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var stream = new MemoryStream(data, writable: false);
        foreach (var face in FontFaceScanner.Scan(stream))
        {
            AddFace(face, () => data);
        }

        return this;
    }

    /// <summary>
    /// Adds a face whose data is loaded on first use, e.g. from an embedded resource.
    /// The family, weight and style are used for matching instead of reading the font.
    /// </summary>
    public FontCollection AddFont(Func<byte[]> load, string family, int weight = 400, bool italic = false, int faceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentException.ThrowIfNullOrEmpty(family);
        lock (_lock)
        {
            _faces.Add(new Face(family, [family], weight, italic, faceIndex, load));
        }

        return this;
    }

    /// <summary>Adds a font that is already loaded.</summary>
    public FontCollection AddFont(PdfFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        lock (_lock)
        {
            _faces.Add(new Face(font.FamilyName, [font.FamilyName], font.Weight, font.IsItalic, 0, () => []) { Loaded = font });
        }

        return this;
    }

    /// <summary>
    /// Sets the families to try, in order, when <paramref name="family"/> is not in the collection.
    /// Defaults map Arial, Helvetica, Times New Roman, Courier New and the CSS generic families to metric-compatible fonts.
    /// </summary>
    public FontCollection SetSubstitutes(string family, params IEnumerable<string> substitutes)
    {
        ArgumentException.ThrowIfNullOrEmpty(family);
        ArgumentNullException.ThrowIfNull(substitutes);
        lock (_lock)
        {
            _substitutions[family] = [.. substitutes];
        }

        return this;
    }

    /// <summary>
    /// Finds the best face of a family for the requested weight (100–900) and style, falling back to
    /// configured substitutes. Returns <c>null</c> when neither the family nor a substitute is available.
    /// </summary>
    public PdfFont? Find(string family, int weight = 400, bool italic = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(family);
        return Find([family], weight, italic);
    }

    /// <summary>
    /// Finds the first available family in a CSS-style fallback list, e.g. <c>["Inter", "Arial", "sans-serif"]</c>.
    /// </summary>
    public PdfFont? Find(IEnumerable<string> families, int weight = 400, bool italic = false)
    {
        ArgumentNullException.ThrowIfNull(families);
        Face? face;
        lock (_lock)
        {
            face = families.Select(f => FindFace(f.Trim().Trim('"', '\''), weight, italic, depth: 0)).FirstOrDefault(f => f is not null);
        }

        return face is null ? null : Load(face);
    }

    private Face? FindFace(string family, int weight, bool italic, int depth)
    {
        var candidates = _faces.Where(f => f.Names.Contains(family, StringComparer.OrdinalIgnoreCase)).ToList();
        if (candidates.Count > 0)
        {
            return FontMatcher.Match(candidates, f => f.Weight, f => f.Italic, weight, italic);
        }

        if (depth < 3 && _substitutions.TryGetValue(family, out var substitutes))
        {
            foreach (var substitute in substitutes)
            {
                if (FindFace(substitute, weight, italic, depth + 1) is { } match)
                {
                    return match;
                }
            }
        }

        return null;
    }

    private void AddFace(ScannedFace scanned, Func<byte[]> load)
    {
        var family = scanned.Names.Family;
        if (string.IsNullOrEmpty(family))
        {
            return;
        }

        string[] names = string.Equals(family, scanned.Names.LegacyFamily, StringComparison.OrdinalIgnoreCase) || scanned.Names.LegacyFamily.Length == 0
            ? [family]
            : [family, scanned.Names.LegacyFamily];
        lock (_lock)
        {
            _faces.Add(new Face(family, names, scanned.Weight, scanned.IsItalic, scanned.FaceIndex, load));
        }
    }

    private static PdfFont Load(Face face)
    {
        lock (face)
        {
            return face.Loaded ??= PdfFont.Load(face.LoadData(), face.FaceIndex);
        }
    }

    private static bool IsFontFile(string path) =>
        Path.GetExtension(path).ToUpperInvariant() is ".TTF" or ".OTF" or ".TTC" or ".OTC" or ".WOFF" or ".WOFF2";

    private sealed record Face(string Family, string[] Names, int Weight, bool Italic, int FaceIndex, Func<byte[]> LoadData)
    {
        public PdfFont? Loaded { get; set; }
    }
}

/// <summary>The CSS Fonts Level 4 font matching algorithm (section 5.2) for style and weight.</summary>
internal static class FontMatcher
{
    public static T Match<T>(IReadOnlyList<T> faces, Func<T, int> weightOf, Func<T, bool> italicOf, int weight, bool italic)
    {
        // Style: prefer the requested style, otherwise accept the other one (a synthetic oblique comes later).
        var styled = faces.Where(f => italicOf(f) == italic).ToList();
        if (styled.Count == 0)
        {
            styled = [.. faces];
        }

        // Weight: exact; 400 tries 500 first; between 400 and 500 tries up to 500, then lighter, then heavier;
        // lighter than 400 searches downward first; heavier than 500 searches upward first.
        var exact = styled.FindIndex(f => weightOf(f) == weight);
        if (exact >= 0)
        {
            return styled[exact];
        }

        IEnumerable<T> lighter = styled.Where(f => weightOf(f) < weight).OrderByDescending(weightOf);
        IEnumerable<T> heavier = styled.Where(f => weightOf(f) > weight).OrderBy(weightOf);
        if (weight is >= 400 and <= 500)
        {
            var upTo500 = styled.Where(f => weightOf(f) > weight && weightOf(f) <= 500).OrderBy(weightOf);
            return upTo500.Concat(lighter).Concat(heavier.Where(f => weightOf(f) > 500)).First();
        }

        return weight < 400 ? lighter.Concat(heavier).First() : heavier.Concat(lighter).First();
    }
}
