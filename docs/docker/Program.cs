// Converts a small HTML document to PDF and writes it to standard output (or to the file named as the argument).
using System.Runtime.InteropServices;
using RaiCore.Pdf.Html;

var pdf = HtmlToPdf.Convert($$"""
    <style>
      @page { size: A4; margin: 20mm; }
      body { font-family: Arial, sans-serif; }
    </style>
    <h1>Hello from a container</h1>
    <p>Made with RaiCore.Pdf on {{RuntimeInformation.OSDescription}} ({{RuntimeInformation.RuntimeIdentifier}}).</p>
    """);

if (args.Length > 0)
{
    pdf.Save(args[0]);
}
else
{
    using var stdout = Console.OpenStandardOutput();
    pdf.Save(stdout);
}
