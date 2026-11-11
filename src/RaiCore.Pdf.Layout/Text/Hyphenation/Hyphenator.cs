using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace RaiCore.Pdf.Layout.Text;

/// <summary>
/// Finds where words may be hyphenated, with Frank Liang's algorithm (the one TeX uses) and a language's hyphenation
/// patterns. Patterns for several languages are bundled (see <see cref="BundledLanguages"/>); others can be added with
/// <see cref="Register"/>.
/// </summary>
/// <remarks>
/// Each pattern is a fragment of a word with digits between its letters, such as <c>.ach4</c> or <c>a1b</c> (a dot
/// marks the start or end of the word). Every pattern found in a word votes on the gaps between its letters; where the
/// highest vote is odd, the word may be hyphenated. Exceptions list whole words with their hyphens, such as
/// <c>ta-ble</c>.
/// <para>
/// Thread-safe: a hyphenator can be used from any number of threads at once. Bundled patterns are loaded once per
/// process, the first time a language needs them, and shared by all documents. <see cref="Register"/> may be called
/// at any time; lookups that start after it returns see the registration.
/// </para>
/// </remarks>
public sealed class Hyphenator
{
    private const int CacheLimit = 20_000;

    private static readonly string[] Bundled = ["da", "de", "en-gb", "en-us", "es", "fr", "it", "nb", "nl", "nn", "pl", "pt"];

    /// <summary>Tags that use another language's patterns (after lowercasing; subtags are dropped one by one first).</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["en"] = "en-us",
        ["en-au"] = "en-gb",
        ["en-ie"] = "en-gb",
        ["en-in"] = "en-gb",
        ["en-nz"] = "en-gb",
        ["en-za"] = "en-gb",
        ["de-1996"] = "de",
        ["no"] = "nb",
    };

    private static readonly ConcurrentDictionary<string, Lazy<Hyphenator>> Loaded = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Hyphenator> Registered = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Hyphenator?> Resolved = new(StringComparer.Ordinal);

    /// <summary>Guards <see cref="Registered"/> changes against a <see cref="ForLanguage"/> caching what it resolved before them.</summary>
    private static readonly Lock RegistrationLock = new();

    /// <summary>Counts <see cref="Register"/> calls, so a resolution that raced with one is not cached.</summary>
    private static int _registrations;

    private readonly PatternTable _patterns;
    private readonly Dictionary<string, int[]> _exceptions;
    private readonly ConcurrentDictionary<string, int[]> _cache = new(StringComparer.Ordinal);

    private Hyphenator(PatternTable patterns, Dictionary<string, int[]> exceptions, int minBefore, int minAfter)
    {
        _patterns = patterns;
        _exceptions = exceptions;
        MinBefore = minBefore;
        MinAfter = minAfter;
    }

    /// <summary>The languages with bundled patterns, as lowercase BCP 47 tags (<c>en-us</c>, <c>de</c>, ...).</summary>
    public static IReadOnlyList<string> BundledLanguages => Bundled;

    /// <summary>The fewest letters a word keeps before a hyphen, for the language the patterns were made for (TeX's <c>\lefthyphenmin</c>).</summary>
    public int MinBefore { get; }

    /// <summary>The fewest letters a word keeps after a hyphen (TeX's <c>\righthyphenmin</c>).</summary>
    public int MinAfter { get; }

    /// <summary>
    /// Creates a hyphenator from Liang patterns, in the format of TeX's <c>\patterns</c> and <c>\hyphenation</c>
    /// (as in the hyph-utf8 project's <c>.pat.txt</c> and <c>.hyp.txt</c> files).
    /// </summary>
    /// <param name="patterns">Patterns such as <c>.ach4</c>, in lowercase.</param>
    /// <param name="exceptions">Words hyphenated by hand, such as <c>ta-ble</c>; a word without hyphens is never hyphenated.</param>
    /// <param name="minBefore">See <see cref="MinBefore"/>.</param>
    /// <param name="minAfter">See <see cref="MinAfter"/>.</param>
    public static Hyphenator FromPatterns(IEnumerable<string> patterns, IEnumerable<string>? exceptions = null, int minBefore = 2, int minAfter = 3)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentOutOfRangeException.ThrowIfLessThan(minBefore, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minAfter, 1);
        return new Hyphenator(PatternTable.Create(patterns), ParseExceptions(exceptions ?? []), minBefore, minAfter);
    }

    /// <summary>
    /// The hyphenator for a language (a BCP 47 tag such as <c>en-US</c>, <c>de-CH</c> or <c>nb</c>, as in HTML
    /// <c>lang</c>), or <c>null</c> when there are no patterns for it. Subtags are dropped from the end until a
    /// registered or bundled language matches: <c>de-CH</c> uses <c>de</c>; <c>en</c> is <c>en-us</c>, and
    /// <c>en-AU</c>, <c>en-IE</c>, <c>en-IN</c>, <c>en-NZ</c> and <c>en-ZA</c> are <c>en-gb</c>. Bundled patterns
    /// are loaded the first time they are needed.
    /// </summary>
    public static Hyphenator? ForLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        if (Resolved.TryGetValue(language, out var cached))
        {
            return cached;
        }

        var registrations = Volatile.Read(ref _registrations);
        var resolved = Resolve(language);
        lock (RegistrationLock)
        {
            // A registration made while this one was resolving may change the answer; then leave it uncached.
            if (registrations == _registrations)
            {
                Resolved.TryAdd(language, resolved);
            }
        }

        return resolved;
    }

    private static Hyphenator? Resolve(string tag)
    {
        for (var candidate = tag.Trim().Replace('_', '-').ToLowerInvariant(); candidate.Length > 0; candidate = candidate[..Math.Max(0, candidate.LastIndexOf('-'))])
        {
            if (Registered.TryGetValue(candidate, out var registered))
            {
                return registered;
            }

            var bundled = Aliases.GetValueOrDefault(candidate, candidate);
            if (Array.IndexOf(Bundled, bundled) >= 0)
            {
                return LoadBundled(bundled);
            }
        }

        return null;
    }

    /// <summary>
    /// Makes <paramref name="hyphenator"/> the one used for <paramref name="language"/> (and its subtags, unless they
    /// have their own), in place of the bundled patterns if there are any; <c>null</c> removes a registration.
    /// </summary>
    public static void Register(string language, Hyphenator? hyphenator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var tag = language.Trim().Replace('_', '-').ToLowerInvariant();
        lock (RegistrationLock)
        {
            if (hyphenator is null)
            {
                Registered.TryRemove(tag, out _);
            }
            else
            {
                Registered[tag] = hyphenator;
            }

            _registrations++;
            Resolved.Clear();
        }
    }

    /// <summary>
    /// The positions in <paramref name="word"/> where it may be hyphenated: a position <c>i</c> means a hyphen between
    /// <c>word[i - 1]</c> and <c>word[i]</c>. At least <see cref="MinBefore"/> letters stay before the first and
    /// <see cref="MinAfter"/> after the last.
    /// </summary>
    public IReadOnlyList<int> Hyphenate(string word) => Hyphenate(word, MinBefore, MinAfter);

    /// <summary>
    /// The positions where <paramref name="word"/> may be hyphenated, keeping at least <paramref name="minBefore"/>
    /// characters before and <paramref name="minAfter"/> after each hyphen (CSS <c>hyphenate-limit-chars</c>).
    /// Never between a character and a combining mark that follows it.
    /// </summary>
    public IReadOnlyList<int> Hyphenate(string word, int minBefore, int minAfter)
    {
        ArgumentNullException.ThrowIfNull(word);
        minBefore = Math.Max(1, minBefore);
        minAfter = Math.Max(1, minAfter);
        if (word.Length < minBefore + minAfter)
        {
            return [];
        }

        var points = _cache.TryGetValue(word, out var cached) ? cached : Cache(word, AllPoints(word));
        var result = new List<int>(points.Length);
        foreach (var p in points)
        {
            if (p >= minBefore && p <= word.Length - minAfter)
            {
                result.Add(p);
            }
        }

        return result;
    }

    /// <summary>Whether <paramref name="c"/> belongs in words: a letter, a combining mark, or an apostrophe the patterns use (as French ones do).</summary>
    internal bool IsWordCharacter(char c) =>
        char.IsLetter(c) || IsMark(c) || _patterns.Alphabet.Contains(c);

    private int[] Cache(string word, int[] points)
    {
        if (_cache.Count >= CacheLimit)
        {
            _cache.Clear();
        }

        _cache[word] = points;
        return points;
    }

    /// <summary>Every position the exceptions or the patterns allow, before minimum lengths.</summary>
    private int[] AllPoints(string word)
    {
        var lower = word.ToLowerInvariant();
        if (_exceptions.TryGetValue(lower, out var exception))
        {
            return exception;
        }

        // Liang: ".word." with a vote in every gap; each pattern found at each position raises the votes it covers.
        var dotted = $".{lower}.";
        var votes = new byte[dotted.Length + 1];
        var lookup = _patterns.Lookup;
        for (var start = 0; start < dotted.Length; start++)
        {
            var longest = Math.Min(_patterns.MaxLength, dotted.Length - start);
            for (var length = 1; length <= longest; length++)
            {
                if (lookup.TryGetValue(dotted.AsSpan(start, length), out var values))
                {
                    for (var k = 0; k < values.Length; k++)
                    {
                        votes[start + k] = Math.Max(votes[start + k], values[k]);
                    }
                }
            }
        }

        // The gap before word[p] is the one before dotted[p + 1]. Odd votes allow a hyphen, even ones forbid it.
        var points = new List<int>();
        for (var p = 1; p < word.Length; p++)
        {
            if ((votes[p + 1] & 1) == 1 && !IsMark(word[p]) && !char.IsLowSurrogate(word[p]))
            {
                points.Add(p);
            }
        }

        return [.. points];
    }

    private static bool IsMark(char c) => CharUnicodeInfo.GetUnicodeCategory(c) is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static Dictionary<string, int[]> ParseExceptions(IEnumerable<string> exceptions)
    {
        var result = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var exception in exceptions)
        {
            var entry = exception.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            var word = new StringBuilder(entry.Length);
            var points = new List<int>();
            foreach (var c in entry)
            {
                if (c == '-')
                {
                    points.Add(word.Length);
                }
                else
                {
                    word.Append(c);
                }
            }

            result[word.ToString().ToLowerInvariant()] = [.. points.Where(p => p > 0 && p < word.Length).Distinct()];
        }

        return result;
    }

    private static Hyphenator LoadBundled(string tag) =>
        Loaded.GetOrAdd(tag, static t => new Lazy<Hyphenator>(() => ReadResource(t))).Value;

    /// <summary>Reads a bundled pattern file (see tools/generate_hyphenation_patterns.py for the format).</summary>
    private static Hyphenator ReadResource(string tag)
    {
        using var stream = typeof(Hyphenator).Assembly.GetManifestResourceStream($"RaiCore.Pdf.Layout.Hyphenation.{tag}.hyph.gz")
            ?? throw new InvalidOperationException($"The hyphenation patterns for '{tag}' are missing from the assembly.");
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress), Encoding.UTF8);
        var (minBefore, minAfter) = (2, 3);
        PatternTable? shared = null;
        var patterns = new List<string>();
        var exceptions = new List<string>();
        List<string>? section = null;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '%')
            {
                continue;
            }

            if (line.StartsWith("@hyphenmins ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                (minBefore, minAfter) = (int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
            }
            else if (line.StartsWith("@patterns-from ", StringComparison.Ordinal))
            {
                shared = LoadBundled(line["@patterns-from ".Length..].Trim())._patterns;
            }
            else if (line == "@patterns")
            {
                section = patterns;
            }
            else if (line == "@exceptions")
            {
                section = exceptions;
            }
            else
            {
                section?.Add(line);
            }
        }

        return new Hyphenator(shared ?? PatternTable.Create(patterns), ParseExceptions(exceptions), minBefore, minAfter);
    }

    /// <summary>Patterns by their letters, with the votes before, between and after them.</summary>
    private sealed class PatternTable
    {
        private PatternTable(Dictionary<string, byte[]> patterns, int maxLength, HashSet<char> alphabet)
        {
            Lookup = patterns.GetAlternateLookup<ReadOnlySpan<char>>();
            MaxLength = maxLength;
            Alphabet = alphabet;
        }

        public Dictionary<string, byte[]>.AlternateLookup<ReadOnlySpan<char>> Lookup { get; }

        public int MaxLength { get; }

        /// <summary>The apostrophes the patterns use, which then belong in words.</summary>
        public HashSet<char> Alphabet { get; }

        public static PatternTable Create(IEnumerable<string> patterns)
        {
            var table = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var alphabet = new HashSet<char>();
            var maxLength = 0;
            var letters = new StringBuilder();
            var values = new List<byte>();
            foreach (var raw in patterns)
            {
                var pattern = raw.Trim();
                if (pattern.Length == 0)
                {
                    continue;
                }

                letters.Clear();
                values.Clear();
                values.Add(0);
                foreach (var c in pattern)
                {
                    if (c is >= '0' and <= '9')
                    {
                        values[^1] = (byte)(c - '0');
                    }
                    else
                    {
                        letters.Append(char.ToLowerInvariant(c));
                        values.Add(0);
                        if (c is '\'' or '\u2019')
                        {
                            alphabet.Add(c);
                        }
                    }
                }

                if (letters.Length == 0)
                {
                    continue;
                }

                // Votes past the last nonzero one change nothing; keep the arrays short.
                var count = values.Count;
                while (count > 1 && values[count - 1] == 0)
                {
                    count--;
                }

                table[letters.ToString()] = [.. values.Take(count)];
                maxLength = Math.Max(maxLength, letters.Length);
            }

            return new PatternTable(table, maxLength, alphabet);
        }
    }
}
