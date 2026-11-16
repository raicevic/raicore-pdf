using System.Diagnostics;
using RaiCore.Pdf.Html;

// Usage: RaiCore.Pdf.Fidelity.Render <corpus folder> <output folder> [document name ...]
// Writes <output>/<name>.pdf for each corpus document (a folder holding index.html), plus <name>.diagnostics.txt
// with what the converter reported, and <name>.error.txt instead when the conversion throws.
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: RaiCore.Pdf.Fidelity.Render <corpus folder> <output folder> [document name ...]");
    return 2;
}

var corpus = Path.GetFullPath(args[0]);
var output = Directory.CreateDirectory(args[1]).FullName;
var only = args.Skip(2).ToHashSet(StringComparer.Ordinal);
var failures = 0;
foreach (var folder in Directory.GetDirectories(corpus).Order(StringComparer.Ordinal))
{
    var name = Path.GetFileName(folder);
    var page = Path.Combine(folder, "index.html");
    if (!File.Exists(page) || (only.Count > 0 && !only.Contains(name)))
    {
        continue;
    }

    var diagnostics = new List<string>();
    var options = new HtmlConverterOptions
    {
        BaseUri = new Uri(folder + Path.DirectorySeparatorChar),
        Diagnostics = d => diagnostics.Add($"{d.Severity} {d.Kind}: {d.Subject}: {d.Message}" + (d.ElementPath is null ? "" : $" [{d.ElementPath}]") + (d.Count > 1 ? $" (x{d.Count})" : "")),
    };

    var watch = Stopwatch.StartNew();
    try
    {
        var pdf = HtmlToPdf.ConvertToBytes(File.ReadAllText(page), options);
        File.WriteAllBytes(Path.Combine(output, name + ".pdf"), pdf);
        File.WriteAllLines(Path.Combine(output, name + ".diagnostics.txt"), diagnostics);
        Console.WriteLine($"{name}: {pdf.Length:N0} bytes, {diagnostics.Count} diagnostics, {watch.ElapsedMilliseconds} ms");
    }
    catch (Exception e)
    {
        failures++;
        File.WriteAllText(Path.Combine(output, name + ".error.txt"), e.ToString());
        Console.Error.WriteLine($"{name}: {e.GetType().Name}: {e.Message}");
    }
}

return failures == 0 ? 0 : 1;
