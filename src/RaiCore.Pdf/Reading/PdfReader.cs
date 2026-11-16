using System.Diagnostics.CodeAnalysis;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf;

/// <summary>
/// An existing PDF file, read to import its pages into a <see cref="PdfDocument"/>
/// (<see cref="PdfDocument.ImportPages(PdfReader, PdfImportOptions?)"/>) or to draw them on a page as a whole
/// (<see cref="PdfCanvas.DrawPage(PdfReaderPage, double, double)"/>), e.g. to add a cover page, append attachments,
/// merge documents or print on a letterhead.
/// </summary>
/// <remarks>
/// <para>
/// The reader handles classic cross-reference tables, cross-reference streams, object streams, hybrid files and
/// incremental updates (the newest revision wins). A file whose cross-reference data is missing or wrong is repaired by
/// scanning it for objects (<see cref="IsRepaired"/>). The whole file is kept in memory, and objects are parsed when
/// they are first needed.
/// </para>
/// <para>
/// A reader is not thread-safe, and documents that import its pages or draw them read it again when they are saved: use
/// a reader, and the documents made from it, on one thread at a time. To merge the same file into documents built in
/// parallel, open a reader per thread.
/// </para>
/// <para>
/// Encrypted files are opened with the standard (password) security handler: AES-256 (revision 6), AES-128 and RC4
/// (revisions 2 to 4). Pass the user or owner password to <see cref="Open(byte[], string?)"/>; without one, the empty
/// user password is tried, which opens files that are only restricted by permissions. Strings and streams are
/// decrypted as they are read, so pages import and draw as from any other file. <see cref="Permissions"/> says what
/// the author allowed; honoring it is up to the caller.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var terms = PdfReader.Open("terms.pdf");
/// var doc = new PdfDocument();
/// var cover = doc.AddPage(PdfPageSize.A4);
/// cover.Canvas.DrawText("Contract", StandardFont.HelveticaBold, 32, 72, 700);
/// doc.ImportPages(terms);
/// doc.Save("contract.pdf");
/// </code>
/// </example>
public sealed class PdfReader
{
    private readonly PdfObjectStore _store;
    private readonly Dictionary<int, PdfReaderPage> _pagesByObject = [];
    private Dictionary<string, PdfObject>? _destinationNames;

    private PdfReader(byte[] data, string? password)
    {
        _store = new PdfObjectStore(data);
        if (_store.Trailer["Encrypt"] is not null)
        {
            _store.Decryption = PdfDecryption.Open(_store, password) ?? throw new PdfPasswordException(
                string.IsNullOrEmpty(password)
                    ? "The PDF file is encrypted with a password. Pass the user or owner password to PdfReader.Open."
                    : "The password is neither the user password nor the owner password of the PDF file.");
        }

        Catalog = _store.Resolve(_store.Trailer["Root"]) as PdfDictionary
            ?? throw new PdfFormatException("The PDF file has no document catalog (/Root).");
        Pages = PdfPageTree.Read(this, Catalog, PageTreeNodes);
        if (Pages.Count == 0)
        {
            throw new PdfFormatException("The PDF file has no pages.");
        }

        foreach (var page in Pages)
        {
            if (page.Reference is { } reference)
            {
                _pagesByObject.TryAdd(reference.ObjectNumber, page);
            }
        }

        var version = _store.HeaderVersion ?? "1.4";
        if (_store.Resolve(Catalog["Version"]) is PdfName catalogVersion
            && string.CompareOrdinal(catalogVersion.Value, version) > 0 && catalogVersion.Value.Length <= 8)
        {
            version = catalogVersion.Value; // An incremental update may raise the version in the catalog.
        }

        Version = version;
    }

    /// <summary>The PDF version, such as <c>1.7</c>: the header's, or the catalog's <c>/Version</c> when that is newer.</summary>
    public string Version { get; }

    /// <summary>
    /// Whether the file was damaged: its cross-reference data was missing or pointed at the wrong places, and the objects
    /// were found by scanning the file instead.
    /// </summary>
    public bool IsRepaired => _store.IsRepaired;

    /// <summary>The pages in order, with their inherited attributes resolved.</summary>
    public IReadOnlyList<PdfReaderPage> Pages { get; }

    /// <summary>Whether the file is encrypted (it has an <c>/Encrypt</c> dictionary).</summary>
    public bool IsEncrypted => _store.Decryption is not null;

    /// <summary>
    /// Whether an encrypted file was opened with its owner password, which grants every permission. <c>false</c> for an
    /// unencrypted file.
    /// </summary>
    public bool IsOpenedWithOwnerPassword => _store.Decryption?.IsOwner ?? false;

    /// <summary>
    /// What the author allows a user without the owner password to do (the encryption dictionary's <c>/P</c>);
    /// <see cref="PdfPermissions.All"/> for an unencrypted file. The reader itself does not enforce them: importing
    /// pages, for example, works whatever they say.
    /// </summary>
    public PdfPermissions Permissions => _store.Decryption?.Permissions ?? PdfPermissions.All;

    /// <summary>The trailer dictionary (merged over incremental updates). References in it are to objects of this file.</summary>
    [Experimental(ExperimentalApis.PdfObjects, UrlFormat = ExperimentalApis.UrlFormat)]
    public PdfDictionary Trailer => _store.Trailer;

    /// <summary>The document catalog (<c>/Root</c>). References in it are to objects of this file; see <see cref="Resolve"/>.</summary>
    [Experimental(ExperimentalApis.PdfObjects, UrlFormat = ExperimentalApis.UrlFormat)]
    public PdfDictionary Catalog { get; }

    /// <summary>The numbers of the page tree's intermediate nodes, which imports never copy.</summary>
    internal HashSet<int> PageTreeNodes { get; } = [];

    internal PdfObjectStore Store => _store;

    /// <summary>Reads a PDF file; an encrypted one is opened with the empty user password.</summary>
    /// <exception cref="PdfFormatException">The file is not a PDF, or is damaged beyond repair.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and needs a password.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted in a way that is not supported (see <see cref="PdfReader"/>).</exception>
    public static PdfReader Open(string path) => Open(path, null);

    /// <summary>Reads a PDF file, opening it with <paramref name="password"/> (the user or the owner password) if it is encrypted.</summary>
    /// <exception cref="PdfFormatException">The file is not a PDF, or is damaged beyond repair.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password is wrong.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted in a way that is not supported (see <see cref="PdfReader"/>).</exception>
    public static PdfReader Open(string path, string? password) => Open(File.ReadAllBytes(path), password);

    /// <summary>Reads a PDF from a stream (to the end, leaving it open); an encrypted one is opened with the empty user password.</summary>
    /// <exception cref="PdfFormatException">The data is not a PDF, or is damaged beyond repair.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and needs a password.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted in a way that is not supported (see <see cref="PdfReader"/>).</exception>
    public static PdfReader Open(Stream stream) => Open(stream, null);

    /// <summary>Reads a PDF from a stream (to the end, leaving it open), opening it with <paramref name="password"/> if it is encrypted.</summary>
    /// <exception cref="PdfFormatException">The data is not a PDF, or is damaged beyond repair.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password is wrong.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted in a way that is not supported (see <see cref="PdfReader"/>).</exception>
    public static PdfReader Open(Stream stream, string? password)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new PdfReader(buffer.ToArray(), password);
    }

    /// <summary>Reads a PDF from bytes (which must not be modified afterwards); an encrypted one is opened with the empty user password.</summary>
    /// <exception cref="PdfFormatException">The data is not a PDF, or is damaged beyond repair.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and needs a password.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted in a way that is not supported (see <see cref="PdfReader"/>).</exception>
    public static PdfReader Open(byte[] data) => Open(data, null);

    /// <summary>
    /// Reads a PDF from bytes (which must not be modified afterwards), opening it with <paramref name="password"/> if it
    /// is encrypted: the user password, or the owner password (<see cref="IsOpenedWithOwnerPassword"/>). <c>null</c> is the
    /// empty password; for an unencrypted file the password is ignored.
    /// </summary>
    /// <exception cref="PdfFormatException">The data is not a PDF, or is damaged beyond repair.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password is wrong.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted in a way that is not supported (see <see cref="PdfReader"/>).</exception>
    public static PdfReader Open(byte[] data, string? password)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new PdfReader(data, password);
    }

    /// <summary>
    /// Follows indirect references (<c>12 0 R</c>) in this file to the object they point to. Returns <c>null</c> for
    /// <c>null</c>, the null object, and references to missing objects (or reference loops).
    /// </summary>
    [Experimental(ExperimentalApis.PdfObjects, UrlFormat = ExperimentalApis.UrlFormat)]
    public PdfObject? Resolve(PdfObject? obj) => _store.Resolve(obj);

    /// <summary>The page that is the object <paramref name="reference"/> points to, or <c>null</c>.</summary>
    internal PdfReaderPage? PageOf(PdfObject? reference) => reference switch
    {
        PdfReference r => _pagesByObject.GetValueOrDefault(r.ObjectNumber),
        PdfInteger { Value: >= 0 } index when index.Value < Pages.Count => Pages[(int)index.Value], // Written by some producers.
        _ => null,
    };

    /// <summary>Whether the object number is a page or a page tree node.</summary>
    internal bool IsPageTreeObject(int objectNumber) => _pagesByObject.ContainsKey(objectNumber) || PageTreeNodes.Contains(objectNumber);

    /// <summary>
    /// Resolves a destination (ISO 32000-2, 12.3.2) to an explicit destination array, <c>[page /XYZ left top zoom]</c>
    /// and the like: follows names and strings through the catalog's <c>/Dests</c> and the <c>/Names /Dests</c> tree,
    /// and dictionaries through <c>/D</c>. Returns <c>null</c> when it does not resolve.
    /// </summary>
    internal PdfArray? ResolveDestination(PdfObject? destination)
    {
        for (var hops = 0; hops < 4; hops++)
        {
            switch (Resolve(destination))
            {
                case PdfArray { Items.Count: > 0 } array:
                    return array;
                case PdfDictionary dictionary:
                    destination = dictionary["D"];
                    break;
                case PdfName name:
                    destination = LookUpDestination(name.GetBytes());
                    break;
                case PdfString text:
                    destination = LookUpDestination(text.Bytes.ToArray());
                    break;
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>The destination of a link annotation or outline item: its <c>/Dest</c>, or the <c>/D</c> of a GoTo action.</summary>
    internal PdfObject? DestinationOf(PdfDictionary item)
    {
        if (item["Dest"] is { } destination)
        {
            return destination;
        }

        return Resolve(item["A"]) is PdfDictionary action && Resolve(action["S"]) is PdfName { Value: "GoTo" } ? action["D"] : null;
    }

    private PdfObject? LookUpDestination(byte[] name)
    {
        var key = System.Text.Encoding.Latin1.GetString(name);
        _destinationNames ??= ReadDestinationNames();
        return _destinationNames.GetValueOrDefault(key);
    }

    /// <summary>The named destinations of the PDF 1.1 <c>/Dests</c> dictionary and the <c>/Names /Dests</c> name tree.</summary>
    private Dictionary<string, PdfObject> ReadDestinationNames()
    {
        var names = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        if (Resolve(Catalog["Names"]) is PdfDictionary nameDictionary)
        {
            PdfNameTree.Read(this, nameDictionary["Dests"], names);
        }

        if (Resolve(Catalog["Dests"]) is PdfDictionary dests)
        {
            foreach (var (key, value) in dests.Entries)
            {
                names.TryAdd(System.Text.Encoding.Latin1.GetString(key.GetBytes()), value);
            }
        }

        return names;
    }
}
