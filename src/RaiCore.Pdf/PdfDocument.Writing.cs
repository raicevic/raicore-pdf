using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Import;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Security;

namespace RaiCore.Pdf;

// Writing the file: Save writes every page and then the rest; BeginSave/FlushPages/EndSave write pages as they are
// finished. Both go through DocumentWriter, so they write the same objects.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The writer BeginSave keeps is disposed by EndSave; a document abandoned while it is written only leaves a hash to the finalizer.")]
public sealed partial class PdfDocument
{
    /// <summary>
    /// Writes a document: each page's content and resources when the page is written (<see cref="WritePage"/>), and,
    /// in <see cref="Finish"/>, everything that needs all pages: the fonts (with the glyphs of every page), the page
    /// dictionaries with their links (which may point at any page), the page tree, outline, named destinations,
    /// metadata and catalog. The catalog and page tree get the first object numbers, reserved up front, as do the page
    /// dictionaries and fonts when they are first met, so that content written early can refer to them.
    /// </summary>
    private sealed class DocumentWriter : IDisposable
    {
        private readonly PdfDocument _document;
        private readonly PdfFileWriter _writer;
        private readonly StandardSecurityHandler? _security;
        private readonly bool _compress;
        private readonly PdfReference _catalogRef;
        private readonly PdfReference _pageTreeRef;

        /// <summary>Each font's reserved reference and the glyphs used from it on the pages written so far.</summary>
        private readonly Dictionary<PdfFont, (PdfReference Reference, FontUsage Usage)> _fonts = new(ReferenceEqualityComparer.Instance);
        private readonly List<PdfFont> _fontOrder = [];
        private readonly Dictionary<PdfImage, PdfReference> _images = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(double Fill, double Stroke), PdfReference> _extGStates = [];
        private readonly PdfImportWriter _imports;
        private readonly StructureTreeWriter? _structure;

        /// <summary>The pages written, with their reserved references and dictionaries (written by <see cref="Finish"/>).</summary>
        private readonly List<(PdfPage Page, PdfReference Reference, PdfDictionary Dictionary)> _pages = [];
        private readonly Dictionary<PdfPage, PdfReference> _pageRefs = new(ReferenceEqualityComparer.Instance);

        public DocumentWriter(PdfDocument document, Stream stream)
        {
            _document = document;
            _compress = document.CompressContent;

            // Checked first, so invalid passwords, permissions or PDF/A and PDF/UA settings fail before anything is written
            // (for Save, with all pages; page by page, the pages written later are checked as they are written).
            document.CheckPdfA(document._pages);
            document.CheckAttachments();
            document.ValidatePdfUA(document._pages.SelectMany(page => page.Fonts).Select(entry => entry.Font));
            _security = document.Encryption is null ? null : StandardSecurityHandler.Create(document.Encryption);
            _writer = new PdfFileWriter { PdfA = document.PdfAConformance, StandardFontSubstitute = document.StandardFontSubstitute };
            _imports = new PdfImportWriter(_writer, _compress);
            _structure = document.Tagged ? new StructureTreeWriter(_writer, document.StructureRoot) : null;
            _writer.Begin(stream, security: _security);
            _catalogRef = _writer.Reserve();
            _pageTreeRef = _writer.Reserve();
        }

        /// <summary>
        /// Writes a page's content stream and resources (to be flushed), and keeps its dictionary for <see cref="Finish"/>.
        /// With <paramref name="release"/>, the page's content is freed and drawing on it is no longer possible.
        /// </summary>
        public void WritePage(PdfPage page, bool release)
        {
            _document.CheckPdfA([page]);
            _document.ValidatePdfUA(page.Fonts.Select(entry => entry.Font));
            var content = page.Canvas.ToBytes();

            // Each font is written once per document, in Finish, embedding the glyphs used on all pages.
            foreach (var (font, _, usage) in page.Fonts)
            {
                if (!_fonts.TryGetValue(font, out var entry))
                {
                    entry = (_writer.Reserve(), new FontUsage());
                    _fonts[font] = entry;
                    _fontOrder.Add(font);
                }

                entry.Usage.MergeFrom(usage);
            }

            var resources = page.WriteResources(
                _writer, _compress, (font, _) => _fonts[font].Reference, Image, ExtGState, _imports.WritePageForm, _structure is null ? null : _structure.RegisterGroup);
            var pageRef = _writer.Reserve();
            PdfDictionary dictionary;
            if (page.Source is not null)
            {
                dictionary = _imports.WritePage(page, _pageTreeRef, content, resources);
            }
            else
            {
                var contentStream = _compress ? PdfStreamObject.CreateCompressed(content) : new PdfStreamObject(content);
                dictionary = new PdfDictionary
                {
                    ["Type"] = new PdfName("Page"),
                    ["Parent"] = _pageTreeRef,
                    ["MediaBox"] = PdfArray.OfNumbers(0, 0, page.Width, page.Height),
                    ["Resources"] = resources,
                    ["Contents"] = _writer.Add(contentStream),
                };

                // A page with transparency blends in RGB, so that viewers agree on how translucent colors mix.
                if (page.UsesTransparency)
                {
                    dictionary["Group"] = PdfPage.TransparencyGroup(isolated: false);
                }
            }

            _structure?.RegisterPage(page, dictionary);
            if (release)
            {
                page.Release();
            }

            _pages.Add((page, pageRef, dictionary));
            _pageRefs[page] = pageRef;
            _imports.RegisterPage(page, pageRef);
        }

        /// <summary>
        /// Writes what the pages written so far added, unless something was taken from another file: objects copied from
        /// a read file may still be completed by later pages (their annotations), so they are all written at the end.
        /// </summary>
        public void Flush()
        {
            if (!_imports.HasCopied)
            {
                _writer.Flush();
            }
        }

        /// <summary>Writes the fonts, the page dictionaries and everything else that needs all pages, then ends the file.</summary>
        public void Finish()
        {
            _document.CheckAttachments(); // Page by page, attachments may have been added since BeginSave.
            foreach (var font in _fontOrder)
            {
                var (reference, usage) = _fonts[font];
                font.WriteTo(_writer, usage, reference);
            }

            // Links may point at any page, so they are written once every page has its object number.
            var kids = new PdfArray();
            foreach (var (page, reference, dictionary) in _pages)
            {
                var annotations = PdfLinkWriter.WriteAnnotations(
                    _writer, page, _pageRefs, _document.NamedDestinations, _structure is null ? null : _structure.RegisterAnnotation);
                if (_imports.WriteAnnotations(page, annotations) is { } all)
                {
                    dictionary["Annots"] = all;
                }

                _writer.Set(reference, dictionary);
                kids.Items.Add(reference);
            }

            _writer.Set(_pageTreeRef, new PdfDictionary
            {
                ["Type"] = new PdfName("Pages"),
                ["Kids"] = kids,
                ["Count"] = new PdfInteger(_pages.Count),
            });

            var catalog = new PdfDictionary { ["Type"] = new PdfName("Catalog"), ["Pages"] = _pageTreeRef };
            if (_document.Outline.Count > 0)
            {
                catalog["Outlines"] = PdfOutlineWriter.Write(_writer, _document.Outline, _pageRefs);
            }

            var names = new PdfDictionary();
            if (_document.NamedDestinations.Count > 0)
            {
                names["Dests"] = PdfLinkWriter.WriteNames(_writer, _document.NamedDestinations, _pageRefs);
            }

            _document.WriteAttachments(_writer, catalog, names);
            if (names.Count > 0)
            {
                catalog["Names"] = names;
            }

            if (_document.PageMode == PdfPageMode.UseOutlines || (_document.PageMode == PdfPageMode.Automatic && _document.Outline.Count > 0))
            {
                catalog["PageMode"] = new PdfName("UseOutlines");
            }

            _document.WriteOutputIntent(_writer, catalog);
            _structure?.Write(catalog, _pageRefs);
            if (_document.PdfUA)
            {
                catalog["ViewerPreferences"] = new PdfDictionary { ["DisplayDocTitle"] = PdfBoolean.True };
            }

            _security?.AddCatalogEntries(catalog);
            var infoRef = _document.WriteMetadata(_writer, catalog);
            _writer.Set(_catalogRef, catalog);
            _writer.Finish(_catalogRef, infoRef);
        }

        public void Dispose() => _writer.Dispose();

        /// <summary>Each image is written once, however many times and pages it is drawn on.</summary>
        private PdfReference Image(PdfImage image)
        {
            if (!_images.TryGetValue(image, out var reference))
            {
                reference = image.WriteTo(_writer);
                _images[image] = reference;
            }

            return reference;
        }

        /// <summary>Each extended graphics state (a pair of fill and stroke alphas) is written once, when first used.</summary>
        private PdfReference ExtGState((double Fill, double Stroke) alpha)
        {
            if (!_extGStates.TryGetValue(alpha, out var reference))
            {
                reference = _writer.Add(PdfPage.ExtGState(alpha));
                _extGStates[alpha] = reference;
            }

            return reference;
        }
    }
}
