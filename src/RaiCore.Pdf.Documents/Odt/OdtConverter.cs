using RaiCore.Pdf.Documents.Docx;
using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Documents.Rendering;

namespace RaiCore.Pdf.Documents.Odt;

/// <summary>Reads an OpenDocument text document into the word-processing model and renders it.</summary>
internal static class OdtConverter
{
    /// <summary>Converts a document within the options' limits.</summary>
    public static PdfDocument Convert(byte[] data, OdtConverterOptions options, CancellationToken cancellationToken)
    {
        DocxConverter.Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        using (options.Limits.Enforce(cancellationToken))
        {
            var context = DocxConverter.Context(options, cancellationToken);
            var document = Read(data, context);
            return new DocumentRenderer(document, options, context).Render(DocxConverter.Creator);
        }
    }

    /// <summary>Reads a document into the model (also for tests).</summary>
    internal static WordDocument Read(byte[] data, ConversionContext context)
    {
        using var package = OdfPackage.Open(data, context);
        return OdtReader.Read(package, context);
    }
}
