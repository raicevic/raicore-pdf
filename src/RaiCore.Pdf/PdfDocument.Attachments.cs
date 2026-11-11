using System.Security.Cryptography;
using System.Text;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf;

// Attached files: embedded file streams, their file specifications in the /EmbeddedFiles name tree, and the
// document's associated files (/AF), as PDF 2.0 and PDF/A-3 describe them.
public sealed partial class PdfDocument
{
    /// <summary>
    /// Files attached to the document, shown in the viewer's attachments panel. Each is written as an embedded file
    /// (Flate-compressed, with its MIME type, size, dates and MD5 checksum) in the catalog's <c>/EmbeddedFiles</c> name
    /// tree and as an associated file of the document (<c>/AF</c>) with its <see cref="PdfAttachment.Relationship"/>.
    /// File names must be unique. PDF/A-2 does not allow attachments (except PDF/A files, which are not checked here):
    /// use <see cref="PdfAConformance.PdfA3B"/>, which allows any file.
    /// </summary>
    public IList<PdfAttachment> Attachments { get; } = new List<PdfAttachment>();

    /// <summary>Checks the attachments before anything is written: unique names, and none under PDF/A-2.</summary>
    private void CheckAttachments()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attachment in Attachments)
        {
            ArgumentNullException.ThrowIfNull(attachment, nameof(Attachments));
            if (!names.Add(attachment.FileName))
            {
                throw new InvalidOperationException($"Two attachments are named \"{attachment.FileName}\"; attachment names must be unique.");
            }
        }

        if (Attachments.Count > 0 && PdfAConformance == PdfAConformance.PdfA2B)
        {
            throw new InvalidOperationException(
                "PDF/A-2 only allows attaching files that are PDF/A themselves, which cannot be checked here: use PDF/A-3 (PdfAConformance.PdfA3B), which allows any file.");
        }
    }

    /// <summary>
    /// Writes the attachments: adds <c>/EmbeddedFiles</c> to <paramref name="names"/> (the catalog's name dictionary)
    /// and <c>/AF</c> to the catalog.
    /// </summary>
    private void WriteAttachments(PdfFileWriter writer, PdfDictionary catalog, PdfDictionary names)
    {
        if (Attachments.Count == 0)
        {
            return;
        }

        var defaultDate = Info.ModificationDate ?? Info.CreationDate ?? DateTimeOffset.Now;
        var tree = new List<(PdfString Key, PdfReference FileSpec)>();
        var associated = new PdfArray();
        foreach (var attachment in Attachments)
        {
            var data = attachment.Data.Span;
            var parameters = new PdfDictionary
            {
                ["Size"] = new PdfInteger(data.Length),
                ["ModDate"] = Date(attachment.ModificationDate ?? defaultDate),
#pragma warning disable CA5351 // MD5 is the checksum the PDF format defines (ISO 32000-1, Table 46), not a security measure.
                ["CheckSum"] = new PdfString(MD5.HashData(data), hex: true),
#pragma warning restore CA5351
            };
            if (attachment.CreationDate is { } created)
            {
                parameters["CreationDate"] = Date(created);
            }

            var file = PdfStreamObject.CreateCompressed(data.ToArray());
            file.Dictionary["Type"] = new PdfName("EmbeddedFile");
            file.Dictionary["Subtype"] = new PdfName(attachment.MediaType);
            file.Dictionary["Params"] = parameters;
            var fileRef = writer.Add(file);

            var spec = new PdfDictionary
            {
                ["Type"] = new PdfName("Filespec"),
                ["F"] = PdfString.FromText(attachment.FileName),
                ["UF"] = PdfString.FromText(attachment.FileName),
                ["EF"] = new PdfDictionary { ["F"] = fileRef, ["UF"] = fileRef },
                ["AFRelationship"] = new PdfName(attachment.Relationship.ToString()),
            };
            if (!string.IsNullOrEmpty(attachment.Description))
            {
                spec["Desc"] = PdfString.FromText(attachment.Description);
            }

            var specRef = writer.Add(spec);
            tree.Add((PdfString.FromText(attachment.FileName), specRef));
            associated.Items.Add(specRef);
        }

        // A name tree with a single leaf, keys sorted by their bytes.
        var entries = new PdfArray();
        foreach (var (key, spec) in tree.OrderBy(entry => entry.Key.Bytes.ToArray(), Comparer<byte[]>.Create((x, y) => x.AsSpan().SequenceCompareTo(y))))
        {
            entries.Items.Add(key);
            entries.Items.Add(spec);
        }

        names["EmbeddedFiles"] = writer.Add(new PdfDictionary { ["Names"] = entries });
        catalog["AF"] = associated;
    }

    private static PdfString Date(DateTimeOffset date) => new(Encoding.ASCII.GetBytes(PdfDocumentInfo.FormatDate(date)));
}
