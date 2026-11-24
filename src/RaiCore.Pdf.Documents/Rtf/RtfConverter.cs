using RaiCore.Pdf.Documents.Docx;
using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Documents.Rendering;

namespace RaiCore.Pdf.Documents.Rtf;

/// <summary>Reads an RTF document into the word-processing model and renders it.</summary>
internal static class RtfConverter
{
    /// <summary>Converts a document within the options' limits.</summary>
    public static PdfDocument Convert(byte[] data, RtfConverterOptions options, CancellationToken cancellationToken)
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
    internal static WordDocument Read(byte[] data, ConversionContext context) => RtfReader.Read(data, context);
}
