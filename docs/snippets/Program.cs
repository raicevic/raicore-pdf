// Runs every code sample of the documentation site. Samples write their PDFs to the current directory, which is set to
// the output directory first.
// Usage: dotnet run --project docs/snippets [output-directory]
using Snippets;

var output = Directory.CreateDirectory(args.Length > 0 ? args[0] : "snippets-output").FullName;
Directory.SetCurrentDirectory(output);

var samples = new (string Name, Func<Task> Run)[]
{
    ("getting started", GettingStarted.RunAsync),
    ("CSS support", CssSupport.RunAsync),
    ("paged media", PagedMedia.RunAsync),
    ("fonts", Fonts.RunAsync),
    ("images", Images.RunAsync),
    ("tables and pagination", Tables.RunAsync),
    ("flexbox", Flexbox.RunAsync),
    ("Razor", Razor.RunAsync),
    ("layout API", LayoutApi.RunAsync),
    ("PDF/A and accessibility", Standards.RunAsync),
    ("text extraction", TextExtraction.RunAsync),
    ("image extraction", ImageExtraction.RunAsync),
    ("editing", Editing.RunAsync),
};

foreach (var (name, run) in samples)
{
    await run();
    Console.WriteLine($"ok {name}");
}

var pdfs = Directory.GetFiles(output, "*.pdf");
Console.WriteLine($"{pdfs.Length} PDFs in {output}");
return pdfs.All(p => File.ReadAllBytes(p).AsSpan().StartsWith("%PDF-"u8)) ? 0 : 1;
