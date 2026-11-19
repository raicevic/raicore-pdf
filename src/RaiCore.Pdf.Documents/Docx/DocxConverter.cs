using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Documents.Rendering;

namespace RaiCore.Pdf.Documents.Docx;

/// <summary>Reads a Word document into the word-processing model and renders it.</summary>
internal static class DocxConverter
{
    /// <summary>The creator the PDFs name.</summary>
    public const string Creator = "RaiCore.Pdf.Documents";

    /// <summary>Converts a document within the options' limits.</summary>
    public static PdfDocument Convert(byte[] data, DocxConverterOptions options, CancellationToken cancellationToken)
    {
        Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        using (options.Limits.Enforce(cancellationToken))
        {
            var context = new ConversionContext(options.Limits, options.Diagnostics, cancellationToken);
            var document = Read(data, context);
            return new DocumentRenderer(document, options, context).Render(Creator);
        }
    }

    /// <summary>Reads a document into the model (also for tests).</summary>
    internal static WordDocument Read(byte[] data, ConversionContext context)
    {
        using var package = DocxPackage.Open(data, context);
        return DocxReader.Read(package, context);
    }

    /// <summary>Checks the options the converters share.</summary>
    internal static void Validate(DocumentConverterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Limits, nameof(options));
        ArgumentNullException.ThrowIfNull(options.FontFamily, nameof(options));
        ArgumentNullException.ThrowIfNull(options.FallbackFontFamilies, nameof(options));
    }
}
