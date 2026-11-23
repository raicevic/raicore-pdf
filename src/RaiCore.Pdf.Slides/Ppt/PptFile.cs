using RaiCore.Pdf.Cfb;

namespace RaiCore.Pdf.Slides.Ppt;

/// <summary>
/// A PowerPoint 97-2003 file opened ([MS-PPT] 2.1): its compound file, the "PowerPoint Document" stream, the persist
/// object directory of its current edit (the user edit chain followed from the "Current User" stream, later edits
/// overriding earlier ones), the document container, the "Pictures" stream and the summary information.
/// </summary>
/// <remarks>
/// A file whose current user stream or user edit chain is damaged is read from the persist directories found in the
/// stream instead. Encrypted files, PowerPoint 95 and PowerPoint 4.0 files throw <see cref="NotSupportedException"/>;
/// files that are not compound files, or have no readable document, throw <see cref="PresentationFormatException"/>.
/// </remarks>
internal sealed class PptFile
{
    /// <summary>The header token of an unencrypted file's current user atom.</summary>
    private const uint PlainToken = 0xE391C05F;

    /// <summary>The header token of an encrypted file's current user atom.</summary>
    private const uint EncryptedToken = 0xF3D1C4DF;

    /// <summary>The most user edits followed (a chain that long is damage).</summary>
    private const int MaxEdits = 4096;

    private PptFile(RecordStream stream, Dictionary<int, int> persist, Record document, byte[]? pictures, string? title, string? author)
    {
        Stream = stream;
        Persist = persist;
        Document = document;
        Pictures = pictures;
        Title = title;
        Author = author;
    }

    /// <summary>The PowerPoint Document stream.</summary>
    public RecordStream Stream { get; }

    /// <summary>The persist object directory: persist id to offset in <see cref="Stream"/>.</summary>
    public Dictionary<int, int> Persist { get; }

    /// <summary>The document container.</summary>
    public Record Document { get; }

    /// <summary>The Pictures stream, if any.</summary>
    public byte[]? Pictures { get; }

    /// <summary>The title of the summary information, if any.</summary>
    public string? Title { get; }

    /// <summary>The author of the summary information, if any.</summary>
    public string? Author { get; }

    /// <summary>The record a persist id refers to, if it is one of the given type.</summary>
    public Record? Object(int persistId, int type) =>
        Persist.TryGetValue(persistId, out var offset) && Stream.At(offset) is { } record && record.Type == type ? record : null;

    /// <summary>Opens a file.</summary>
    /// <exception cref="PresentationFormatException">The data is not a PowerPoint 97-2003 file, or it is damaged beyond reading.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted, or a PowerPoint 95 or 4.0 file.</exception>
    public static PptFile Open(byte[] data, ResourceLimits limits)
    {
        if (!CompoundFile.IsCompoundFile(data))
        {
            throw new PresentationFormatException(data.AsSpan().StartsWith("PK"u8)
                ? "The data is a ZIP package, not a PowerPoint 97-2003 file: convert PPTX presentations with PptxToPdf."
                : "The data is not a PowerPoint 97-2003 file (no compound file signature).");
        }

        CompoundFile file;
        try
        {
            file = CompoundFile.Open(data, limits);
        }
        catch (PdfFormatException ex)
        {
            throw new PresentationFormatException($"The PowerPoint 97-2003 file is damaged: {ex.Message}", ex);
        }

        var root = file.Root;
        if (root["EncryptionInfo"] is not null || root["EncryptedPackage"] is not null)
        {
            throw new NotSupportedException("The presentation is password-protected (encrypted), which is not supported.");
        }

        // PowerPoint 95 files saved "dual" keep a PowerPoint 97 copy in a storage of their own.
        var storage = root["PP97_DUALSTORAGE"] is { Kind: CfbEntryKind.Storage } dual && dual["PowerPoint Document"] is not null ? dual : root;
        if (storage["PowerPoint Document"] is not { Kind: CfbEntryKind.Stream } documentEntry)
        {
            throw root["PP40"] is not null
                ? new NotSupportedException("The file is a PowerPoint 4.0 presentation, which is not supported (only PowerPoint 97-2003).")
                : root["Header"] is not null
                    ? new NotSupportedException("The file is a PowerPoint 95 presentation, which is not supported (only PowerPoint 97-2003).")
                    : new PresentationFormatException("The compound file has no PowerPoint Document stream: it is not a PowerPoint 97-2003 file.");
        }

        var stream = new RecordStream(Read(file, documentEntry, data.Length), limits);
        var currentEdit = CurrentEdit(file, storage, root, data.Length);
        var persist = PersistDirectory(stream, currentEdit, out var documentId);
        var document = persist.TryGetValue(documentId, out var offset) && stream.At(offset) is { Type: Rt.Document } found ? found : (Record?)null;
        if (document is null)
        {
            // A damaged edit chain: the persist directories of the stream, in order, and the last document container.
            persist = ScanPersistDirectories(stream);
            document = persist.TryGetValue(1, out offset) && stream.At(offset) is { Type: Rt.Document } scanned ? scanned : LastTopLevel(stream, Rt.Document);
        }

        if (document is null)
        {
            throw new PresentationFormatException("The PowerPoint Document stream has no document container.");
        }

        var pictures = storage["Pictures"] is { Kind: CfbEntryKind.Stream } picturesEntry ? TryRead(file, picturesEntry, data.Length) : null;
        var (title, author) = Summary(file, storage, data.Length);
        return new PptFile(stream, persist, document.Value, pictures, title, author);
    }

    private static byte[] Read(CompoundFile file, CfbEntry entry, long fileLength)
    {
        if (entry.Size > fileLength)
        {
            throw new PresentationFormatException($"The stream '{entry.Name}' of the PowerPoint 97-2003 file claims {entry.Size} bytes, more than the whole file.");
        }

        try
        {
            return file.Read(entry);
        }
        catch (PdfFormatException ex)
        {
            throw new PresentationFormatException($"The stream '{entry.Name}' of the PowerPoint 97-2003 file is damaged: {ex.Message}", ex);
        }
    }

    private static byte[]? TryRead(CompoundFile file, CfbEntry entry, long fileLength)
    {
        if (entry.Size > fileLength)
        {
            return null;
        }

        try
        {
            return file.Read(entry);
        }
        catch (PdfFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The offset of the current user edit, from the "Current User" stream; -1 when it is missing or damaged.
    /// </summary>
    private static long CurrentEdit(CompoundFile file, CfbEntry storage, CfbEntry root, long fileLength)
    {
        var data = storage["Current User"] is { Kind: CfbEntryKind.Stream } entry ? TryRead(file, entry, fileLength) : null;
        if (data is null || data.Length < 20 || Bytes.U16(data, 2) != Rt.CurrentUserAtom)
        {
            if (root["Header"] is not null && storage == root)
            {
                throw new NotSupportedException("The file is a PowerPoint 95 presentation, which is not supported (only PowerPoint 97-2003).");
            }

            return -1;
        }

        var token = Bytes.U32(data, 12);
        if (token == EncryptedToken)
        {
            throw new NotSupportedException("The presentation is password-protected (encrypted), which is not supported.");
        }

        return token == PlainToken ? Bytes.U32(data, 16) : -1;
    }

    /// <summary>
    /// The persist directory of a user edit chain: the edits followed back from the current one, then their
    /// directories applied oldest first. When the chain is broken (it loops, or points at something else), the
    /// directories found in the stream come first. Empty when the chain does not start with a user edit.
    /// </summary>
    private static Dictionary<int, int> PersistDirectory(RecordStream stream, long currentEdit, out int documentId)
    {
        documentId = 1;
        var edits = new List<(int Directory, int DocumentId)>();
        var visited = new HashSet<long>();
        var offset = currentEdit;
        var broken = false;
        while (offset >= 0)
        {
            if (!visited.Add(offset) || edits.Count >= MaxEdits || stream.At(offset) is not { Type: Rt.UserEditAtom } edit)
            {
                broken = edits.Count > 0;
                break;
            }

            var body = stream.Body(edit);
            if (edits.Count == 0 && body.Length >= 32 && Bytes.U32(body, 28) != 0)
            {
                // An encryptSessionPersistIdRef: the edit's objects are encrypted.
                throw new NotSupportedException("The presentation is password-protected (encrypted), which is not supported.");
            }

            edits.Add(((int)Math.Min(Bytes.U32(body, 12), int.MaxValue), (int)Math.Min(Bytes.U32(body, 16), int.MaxValue)));
            var last = Bytes.U32(body, 8);
            offset = last == 0 ? -1 : last;
        }

        var persist = broken ? ScanPersistDirectories(stream) : [];
        if (edits.Count > 0)
        {
            documentId = edits[0].DocumentId;
        }

        for (var i = edits.Count - 1; i >= 0; i--)
        {
            if (stream.At(edits[i].Directory) is { Type: Rt.PersistDirectoryAtom } directory)
            {
                ReadDirectory(stream, directory, persist);
            }
        }

        return persist;
    }

    /// <summary>The persist directories of the stream's top-level records, in stream order.</summary>
    private static Dictionary<int, int> ScanPersistDirectories(RecordStream stream)
    {
        var persist = new Dictionary<int, int>();
        foreach (var record in TopLevel(stream))
        {
            if (record.Type == Rt.PersistDirectoryAtom)
            {
                ReadDirectory(stream, record, persist);
            }
        }

        return persist;
    }

    /// <summary>Adds the entries of a persist directory atom (runs of persist ids and their offsets).</summary>
    private static void ReadDirectory(RecordStream stream, Record directory, Dictionary<int, int> persist)
    {
        var body = stream.Body(directory);
        var at = 0;
        while (at + 4 <= body.Length)
        {
            var entry = Bytes.U32(body, at);
            var first = (int)(entry & 0xFFFFF);
            var count = (int)(entry >> 20);
            at += 4;
            for (var i = 0; i < count && at + 4 <= body.Length; i++, at += 4)
            {
                var offset = Bytes.U32(body, at);
                if (offset < stream.Data.Length)
                {
                    persist[first + i] = (int)offset;
                }
            }
        }
    }

    private static IEnumerable<Record> TopLevel(RecordStream stream)
    {
        var offset = 0;
        while (stream.At(offset) is { } record)
        {
            yield return record;
            offset = record.End;
        }
    }

    private static Record? LastTopLevel(RecordStream stream, int type)
    {
        Record? last = null;
        foreach (var record in TopLevel(stream))
        {
            if (record.Type == type)
            {
                last = record;
            }
        }

        return last;
    }

    /// <summary>The title and author of the summary information, if it can be read.</summary>
    private static (string? Title, string? Author) Summary(CompoundFile file, CfbEntry storage, long fileLength)
    {
        if (storage["\u0005SummaryInformation"] is not { Kind: CfbEntryKind.Stream } entry || TryRead(file, entry, fileLength) is not { } data)
        {
            return (null, null);
        }

        try
        {
            var properties = PropertySetStream.Summary(data);
            static string? Text(IReadOnlyDictionary<uint, object> properties, uint id) =>
                properties.TryGetValue(id, out var value) && value is string { Length: > 0 } text ? text.TrimEnd('\0') : null;
            return (Text(properties, PropertySetStream.Title), Text(properties, PropertySetStream.Author));
        }
        catch (PdfFormatException)
        {
            return (null, null);
        }
    }
}
