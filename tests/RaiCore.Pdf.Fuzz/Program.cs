using System.Diagnostics;
using RaiCore.Pdf.Tests.Fuzzing;
using SharpFuzz;

// Fuzzing entry point.
//   <target>                                            run under libfuzzer-dotnet (assemblies instrumented with sharpfuzz)
//   local <target> <iterations> <seed> <out> <corpus>... a managed mutation loop, no native tools needed
//   replay <target> <file or folder>...                  run inputs once, e.g. a crash libFuzzer found
//   minimize <target> <file> <out>                      shrink a failing input while it fails the same way
// Targets: see FuzzTargets.All (html, css, png, jpeg, gif, bmp, webp, svg, font, woff, woff2, pdf); seed-corpus.sh
// collects the seeds of one. Locally, e.g.:
//   tests/RaiCore.Pdf.Fuzz/seed-corpus.sh png /tmp/png
//   dotnet run -c Release --project tests/RaiCore.Pdf.Fuzz -- local png 100000 1 /tmp/png-out /tmp/png
// An input that fails becomes a regression input in tests/RaiCore.Pdf.Tests/Fixtures/Fuzz/Crashes/<target>.
// Under libFuzzer, nothing may run library code (which is instrumented) before LibFuzzer.Run sets up the coverage map:
// the target is chosen by name only, and everything else happens inside the callback.
if (args.Length == 1 && FuzzTargets.All.ContainsKey(args[0]))
{
    var name = args[0];
    Fuzzer.LibFuzzer.Run(span => FuzzTargets.Run(name, span.ToArray()));
    return 0;
}

if (args.Length < 2 || !FuzzTargets.All.ContainsKey(args[1]))
{
    Console.Error.WriteLine($"usage: <target> | local|replay <target> ...; targets: {string.Join(", ", FuzzTargets.All.Keys)}");
    return 2;
}

var target = args[1];
switch (args[0])
{
    case "replay":
        foreach (var file in args.Skip(2).SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p) : [p]))
        {
            Console.WriteLine(file);
            FuzzTargets.Run(target, File.ReadAllBytes(file));
        }

        return 0;
    case "minimize":
        return LocalFuzzer.Minimize(target, args[2], args[3]);
    case "local":
        return LocalFuzzer.Run(target, long.Parse(args[2]), int.Parse(args[3]), args[4], args[5..]);
    default:
        Console.Error.WriteLine($"Unknown mode {args[0]}.");
        return 2;
}

/// <summary>
/// A managed mutation fuzzer for machines without libFuzzer: random byte flips, inserts, deletions, duplications,
/// truncations, interesting integers and splices over the seed corpus (stacked a few at a time), with a fixed seed so
/// a run can be repeated. Each input runs with a time limit; the input being run is written to disk first, so one that
/// crashes the process (a stack overflow) is kept. Failures are saved to the output folder and the run goes on.
/// </summary>
internal static class LocalFuzzer
{
    private static readonly byte[][] Tokens =
    [
        .. new[]
        {
            "<div>", "</div>", "<table><tr><td>", "<span style='display:inline-block'>", "<img src='data:image/png;base64,'>",
            "<svg><rect width='10' height='10'/></svg>", "calc(", "100%", "1e308", "-1e308", "999999999", "NaN", "0", "-0",
            "inherit", "!important", "\u0000", "\uFFFF", "\uD800", "&#0;", "&amp;", "var(--x)", "counter(c)", "attr(x)",
            "::before{content:'x'}", "@page{size:1px}", "@font-face{", "url(", ")", "}", "{", ";", ":", ",", "'", "\"",
            "obj", "endobj", "stream\n", "endstream", "R", "<<", ">>", "[", "]", "/Length", "/Filter /FlateDecode",
            "xref", "trailer", "startxref", "%%EOF", "/Kids", "/Prev", "-2147483648", "4294967295",
        }.Select(System.Text.Encoding.UTF8.GetBytes),
    ];

    private static readonly long[] Interesting = [0, 1, -1, 0x7F, 0x80, 0xFF, 0x7FFF, 0x8000, 0xFFFF, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 16384, 65535, 65536];

    public static int Run(string target, long iterations, int seed, string output, string[] corpora)
    {
        var seeds = corpora.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*", SearchOption.AllDirectories) : [p])
            .Order(StringComparer.Ordinal).Select(File.ReadAllBytes).Where(s => s.Length > 0).ToList();
        if (seeds.Count == 0)
        {
            Console.Error.WriteLine("No seeds.");
            return 2;
        }

        Directory.CreateDirectory(output);
        var current = Path.Combine(output, "current.bin");
        var random = new Random(seed);
        var failures = new HashSet<string>(StringComparer.Ordinal);
        var watch = Stopwatch.StartNew();
        var progress = Math.Max(1, Math.Min(10_000, iterations / 20));
        for (long i = 0; i < iterations; i++)
        {
            var input = Mutate(random, seeds[random.Next(seeds.Count)], seeds);
            File.WriteAllBytes(current, input);
            var (error, elapsed) = RunWithTimeout(target, input, TimeSpan.FromSeconds(30));
            if (error is not null || elapsed > TimeSpan.FromSeconds(10))
            {
                var kind = error is null ? $"slow{(int)elapsed.TotalSeconds}s" : Signature(error);
                if (failures.Add(kind))
                {
                    var name = Path.Combine(output, $"{target}-{i}-{Sanitize(kind)}.bin"); // The first input of each kind.
                    File.WriteAllBytes(name, input);
                    Console.WriteLine($"[{i}] {kind}: {error?.Message ?? elapsed.ToString()} -> {name}");
                    if (error is not null)
                    {
                        Console.WriteLine(error.StackTrace?.Split('\n').Take(6).Aggregate((a, b) => a + "\n" + b));
                    }
                }

                if (error is TimeoutException)
                {
                    Console.WriteLine("Hang: stopping (the thread cannot be aborted).");
                    return 1;
                }
            }

            if (i % progress == 0)
            {
                Console.WriteLine($"{target}: {i} inputs, {watch.Elapsed:hh\\:mm\\:ss}, {failures.Count} distinct failures, {Environment.WorkingSet / (1024 * 1024)} MB");
            }
        }

        File.Delete(current);
        Console.WriteLine($"{target}: done, {iterations} inputs, {failures.Count} distinct failures");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Shrinks a failing input (delta debugging over byte ranges, then single bytes) as long as it fails with the same
    /// exception type at the same place.
    /// </summary>
    public static int Minimize(string target, string file, string output)
    {
        var input = File.ReadAllBytes(file);
        var (error, _) = RunWithTimeout(target, input, TimeSpan.FromSeconds(30));
        if (error is null)
        {
            Console.Error.WriteLine("The input does not fail.");
            return 1;
        }

        var signature = Signature(error);
        Console.WriteLine($"Minimizing {input.Length} bytes failing with {signature}.");
        for (var chunk = Math.Max(1, input.Length / 2); chunk >= 1; chunk /= 2)
        {
            for (var start = 0; start < input.Length;)
            {
                var candidate = input[..start].Concat(input[Math.Min(input.Length, start + chunk)..]).ToArray();
                var (candidateError, _) = RunWithTimeout(target, candidate, TimeSpan.FromSeconds(30));
                if (candidateError is not null && Signature(candidateError) == signature)
                {
                    input = candidate;
                }
                else
                {
                    start += chunk;
                }
            }
        }

        File.WriteAllBytes(output, input);
        Console.WriteLine($"{input.Length} bytes -> {output}");
        return 0;
    }

    private static string Signature(Exception error) => $"{error.GetType().Name}@{Frame(error)}";

    private static (Exception? Error, TimeSpan Elapsed) RunWithTimeout(string target, byte[] input, TimeSpan timeout)
    {
        Exception? error = null;
        var watch = Stopwatch.StartNew();
        var thread = new Thread(
            () =>
            {
                try
                {
                    FuzzTargets.Run(target, input);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            },
            16 * 1024 * 1024) { IsBackground = true };
        thread.Start();
        if (!thread.Join(timeout))
        {
            return (new TimeoutException($"The input ran for more than {timeout}."), watch.Elapsed);
        }

        return (error, watch.Elapsed);
    }

    private static string Frame(Exception error) =>
        error.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("RaiCore", StringComparison.Ordinal)) is { } line
            ? line[3..Math.Min(line.Length, line.IndexOf('(') is var p and > 3 ? p : line.Length)]
            : "?";

    private static string Sanitize(string text) =>
        new([.. text.Select(c => char.IsLetterOrDigit(c) || c is '.' or '@' or '-' ? c : '_').Take(100)]);

    private static byte[] Mutate(Random random, byte[] seed, List<byte[]> seeds)
    {
        var data = new List<byte>(seed);
        var count = 1 + random.Next(random.Next(2) == 0 ? 3 : 12);
        for (var m = 0; m < count; m++)
        {
            var at = data.Count == 0 ? 0 : random.Next(data.Count);
            switch (random.Next(11))
            {
                case 0 when data.Count > 0:
                    data[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when data.Count > 0:
                    data[at] = (byte)random.Next(256);
                    break;
                case 2:
                    data.InsertRange(at, Enumerable.Range(0, 1 + random.Next(8)).Select(_ => (byte)random.Next(256)));
                    break;
                case 3 when data.Count > 1:
                    data.RemoveRange(at, Math.Min(data.Count - at, 1 + random.Next(Math.Min(64, data.Count))));
                    break;
                case 4 when data.Count > 1:
                    var length = Math.Min(data.Count - at, 1 + random.Next(64));
                    var copy = data.GetRange(at, length);
                    var repeat = random.Next(4) == 0 ? 1 + random.Next(64) : 1;
                    for (var r = 0; r < repeat; r++)
                    {
                        data.InsertRange(random.Next(data.Count), copy);
                    }

                    break;
                case 5 when data.Count > 1:
                    data.RemoveRange(at, data.Count - at); // Truncate.
                    break;
                case 6 when data.Count >= 4:
                    var value = Interesting[random.Next(Interesting.Length)] + (random.Next(3) - 1);
                    var bytes = random.Next(2) == 0 ? BitConverter.GetBytes((uint)value) : BitConverter.GetBytes((uint)value).Reverse().ToArray();
                    var width = random.Next(3) switch { 0 => 1, 1 => 2, _ => 4 };
                    var start = Math.Min(at, data.Count - width);
                    for (var b = 0; b < width; b++)
                    {
                        data[start + b] = bytes[random.Next(2) == 0 ? b : 4 - width + b];
                    }

                    break;
                case 7:
                    var other = seeds[random.Next(seeds.Count)];
                    if (other.Length > 0)
                    {
                        var from = random.Next(other.Length);
                        data.InsertRange(at, other.Skip(from).Take(1 + random.Next(Math.Min(256, other.Length - from))));
                    }

                    break;
                case 8:
                case 9:
                    data.InsertRange(at, Tokens[random.Next(Tokens.Length)]);
                    break;
                default:
                    if (data.Count > 0)
                    {
                        data[at] = (byte)"0123456789-.e"[random.Next(13)];
                    }

                    break;
            }
        }

        return [.. data.Take(1 << 20)];
    }
}
