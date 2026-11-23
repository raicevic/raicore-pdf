using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets.Workbooks;

/// <summary>
/// The resource limits of a workbook's operations: the decompressed bytes and XML elements of the parts it reads (all
/// of them together), the nesting of their XML, and the time one operation (opening, an insertion) may take.
/// </summary>
internal sealed class WorkbookGuard(ResourceLimits limits)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _decompressed;
    private long _elements;

    public ResourceLimits Limits { get; } = limits;

    /// <summary>Starts the time of an operation.</summary>
    public void Start() => _clock.Restart();

    /// <summary>Throws when the operation has taken longer than <see cref="ResourceLimits.Timeout"/>.</summary>
    public void Checkpoint()
    {
        if (Limits.Timeout != Timeout.InfiniteTimeSpan && _clock.Elapsed > Limits.Timeout)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.Timeout), (long)Limits.Timeout.TotalMilliseconds,
                $"The workbook operation took longer than ResourceLimits.Timeout ({Limits.Timeout}).");
        }
    }

    /// <summary>Counts decompressed bytes read.</summary>
    public void Decompressed(long bytes)
    {
        _decompressed += bytes;
        if (_decompressed > Limits.MaxDecompressedBytes)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxDecompressedBytes), Limits.MaxDecompressedBytes,
                $"The workbook decompresses to more than ResourceLimits.MaxDecompressedBytes ({Limits.MaxDecompressedBytes} bytes).");
        }
    }

    /// <summary>Counts an XML element read at a depth.</summary>
    public void Element(int depth)
    {
        if (++_elements > Limits.MaxElements)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxElements), Limits.MaxElements,
                $"The workbook has more XML elements than ResourceLimits.MaxElements ({Limits.MaxElements}).");
        }

        if (depth >= Limits.MaxNestingDepth)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), Limits.MaxNestingDepth,
                $"The workbook's XML nests deeper than ResourceLimits.MaxNestingDepth ({Limits.MaxNestingDepth}).");
        }

        if ((_elements & 4095) == 0)
        {
            Checkpoint();
        }
    }
}

/// <summary>A relationship of a part: its id, type (the full URI), target (resolved to a part name) and whether it is external.</summary>
internal readonly record struct PartRelationship(string Id, string Type, string Target, bool External)
{
    /// <summary>The last segment of the type (<c>worksheet</c>), which strict and transitional files share.</summary>
    public string ShortType => Type is null ? string.Empty : Type[(Type.LastIndexOf('/') + 1)..];
}

/// <summary>
/// The ZIP package of a workbook (Open Packaging Conventions) as it is edited: parts that are not changed are written
/// back as they were (their decompressed bytes unchanged, in the original order, with their names and times), and the
/// parts that are changed are written from their XML trees, deterministically. Parts are read within the
/// <see cref="WorkbookGuard"/>'s limits, as XML with no DTDs and nothing external resolved; malformed data throws
/// <see cref="SpreadsheetFormatException"/>.
/// </summary>
internal sealed class WorkbookPackage
{
    /// <summary>The name of the content types part.</summary>
    public const string ContentTypesPart = "[Content_Types].xml";

    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The time new parts get (ZIP times start in 1980), so that output does not depend on the clock.</summary>
    private static readonly DateTimeOffset NewPartTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = false,
        IgnoreProcessingInstructions = false,
        CheckCharacters = false,
        MaxCharactersFromEntities = 1024,
    };

    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        NewLineHandling = NewLineHandling.Entitize,
        CheckCharacters = false,
    };

    private readonly byte[]? _source;
    private readonly ZipArchive? _zip;
    private readonly List<Part> _parts = [];
    private readonly Dictionary<string, Part> _byName = new(StringComparer.OrdinalIgnoreCase);

    private WorkbookPackage(byte[]? source, ZipArchive? zip, WorkbookGuard guard)
    {
        _source = source;
        _zip = zip;
        Guard = guard;
    }

    public WorkbookGuard Guard { get; }

    /// <summary>Whether any part was changed, added or removed.</summary>
    public bool IsModified => _parts.Any(p => p.Dirty || p.Removed || p.Entry is null);

    /// <summary>The names of the parts, in the package's order.</summary>
    public IEnumerable<string> PartNames => _parts.Where(p => !p.Removed).Select(p => p.Name);

    /// <summary>Opens a ZIP package.</summary>
    /// <exception cref="SpreadsheetFormatException">The data is not a ZIP package.</exception>
    public static WorkbookPackage Open(byte[] data, WorkbookGuard guard)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            throw new SpreadsheetFormatException("The data is not a valid ZIP package: " + ex.Message, ex);
        }

        var package = new WorkbookPackage(data, zip, guard);
        try
        {
            foreach (var entry in zip.Entries)
            {
                guard.Element(0);
                var name = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (name.Length == 0 || package._byName.ContainsKey(name))
                {
                    continue;
                }

                var part = new Part(entry.FullName, name) { Entry = entry, LastWrite = entry.LastWriteTime };
                package._parts.Add(part);
                package._byName[name] = part;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new SpreadsheetFormatException("The data is not a valid ZIP package: " + ex.Message, ex);
        }

        return package;
    }

    /// <summary>A package without parts, to add them to.</summary>
    public static WorkbookPackage Create(WorkbookGuard guard) => new(null, null, guard);

    /// <summary>Whether the package has a part.</summary>
    public bool Contains(string name) => _byName.TryGetValue(name, out var part) && !part.Removed;

    /// <summary>A part's bytes as they are now, or <c>null</c> if the package has no such part.</summary>
    public byte[]? Read(string name)
    {
        if (!_byName.TryGetValue(name, out var part) || part.Removed)
        {
            return null;
        }

        if (part.Xml is not null)
        {
            return Serialize(part.Xml);
        }

        if (part.Bytes is not null)
        {
            return part.Bytes;
        }

        using var buffer = new MemoryStream();
        Copy(part.Entry!, buffer, Guard.Decompressed);
        return buffer.ToArray();
    }

    /// <summary>
    /// A part parsed as XML (once; later calls return the same tree, which may be edited and marked with
    /// <see cref="MarkDirty"/>), or <c>null</c> if the package has no such part.
    /// </summary>
    public XDocument? Xml(string name)
    {
        if (!_byName.TryGetValue(name, out var part) || part.Removed)
        {
            return null;
        }

        if (part.Xml is null)
        {
            var bytes = Read(name)!;
            part.Xml = Load(bytes, name, Guard);
        }

        return part.Xml;
    }

    /// <summary>Whether a part is new or marked changed.</summary>
    public bool IsDirty(string name) => _byName.TryGetValue(name, out var part) && !part.Removed && (part.Dirty || part.Entry is null);

    /// <summary>Marks a parsed part as changed, so that it is written from its tree.</summary>
    public void MarkDirty(string name)
    {
        if (_byName.TryGetValue(name, out var part) && part.Xml is not null)
        {
            part.Dirty = true;
        }
    }

    /// <summary>Adds a part (or replaces one) with an XML tree and, if given, a content type override.</summary>
    public void Add(string name, XDocument xml, string? contentType)
    {
        if (!_byName.TryGetValue(name, out var part) || part.Removed)
        {
            if (part is not null)
            {
                _parts.Remove(part);
            }

            part = new Part(name, name) { LastWrite = NewPartTime };
            _parts.Add(part);
            _byName[name] = part;
        }

        part.Xml = xml;
        part.Bytes = null;
        part.Dirty = true;
        if (contentType is not null && name != ContentTypesPart)
        {
            SetContentType(name, contentType);
        }
    }

    /// <summary>Removes a part, its content type override and its relationships part.</summary>
    public void Remove(string name)
    {
        if (!_byName.TryGetValue(name, out var part) || part.Removed)
        {
            return;
        }

        part.Removed = true;
        if (Xml(ContentTypesPart) is { } types && types.Root is { } root)
        {
            var overrides = root.Elements().Where(e => e.Name.LocalName == "Override"
                && string.Equals(((string?)e.Attribute("PartName"))?.TrimStart('/'), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (overrides.Count > 0)
            {
                overrides.ForEach(e => e.Remove());
                MarkDirty(ContentTypesPart);
            }
        }

        if (_byName.TryGetValue(XlsxPackage.RelationshipsOf(name), out var relationships))
        {
            relationships.Removed = true;
        }
    }

    /// <summary>The content type of a part: its override, else the default for its extension; <c>null</c> if neither.</summary>
    public string? ContentType(string name)
    {
        if (Xml(ContentTypesPart)?.Root is not { } root)
        {
            return null;
        }

        string? byExtension = null;
        var extension = Path.GetExtension(name).TrimStart('.');
        foreach (var element in root.Elements())
        {
            if (element.Name.LocalName == "Override"
                && string.Equals(((string?)element.Attribute("PartName"))?.TrimStart('/'), name, StringComparison.OrdinalIgnoreCase))
            {
                return (string?)element.Attribute("ContentType");
            }

            if (element.Name.LocalName == "Default" && byExtension is null
                && string.Equals((string?)element.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase))
            {
                byExtension = (string?)element.Attribute("ContentType");
            }
        }

        return byExtension;
    }

    /// <summary>Sets a part's content type override, creating the content types part if there is none.</summary>
    private void SetContentType(string name, string contentType)
    {
        var types = Xml(ContentTypesPart);
        if (types?.Root is null)
        {
            XNamespace ns = ContentTypesNamespace;
            types = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(ns + "Types",
                new XElement(ns + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ns + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml"))));
            Add(ContentTypesPart, types, null);
        }

        var root = types.Root!;
        var existing = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Override"
            && string.Equals(((string?)e.Attribute("PartName"))?.TrimStart('/'), name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.SetAttributeValue("ContentType", contentType);
        }
        else
        {
            root.Add(new XElement(root.Name.Namespace + "Override", new XAttribute("PartName", "/" + name), new XAttribute("ContentType", contentType)));
        }

        MarkDirty(ContentTypesPart);
    }

    /// <summary>The relationships of a part (of the package for an empty name), with targets resolved to part names.</summary>
    public List<PartRelationship> Relationships(string partName)
    {
        var result = new List<PartRelationship>();
        if (Xml(XlsxPackage.RelationshipsOf(partName))?.Root is not { } root)
        {
            return result;
        }

        var folder = XlsxPackage.FolderOf(partName);
        foreach (var element in root.Elements())
        {
            if (element.Name.LocalName == "Relationship" && (string?)element.Attribute("Target") is { } target && (string?)element.Attribute("Type") is { } type)
            {
                var external = (string?)element.Attribute("TargetMode") == "External";
                result.Add(new PartRelationship((string?)element.Attribute("Id") ?? string.Empty, type, external ? target : XlsxPackage.ResolveTarget(folder, target), external));
            }
        }

        return result;
    }

    /// <summary>Adds a relationship from a part to another part; returns its id.</summary>
    public string AddRelationship(string partName, string type, string targetPart)
    {
        var relationshipsPart = XlsxPackage.RelationshipsOf(partName);
        var document = Xml(relationshipsPart);
        if (document?.Root is null)
        {
            document = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(XName.Get("Relationships", RelationshipsNamespace)));
            Add(relationshipsPart, document, null);
        }

        var root = document.Root!;
        var ids = root.Elements().Select(e => (string?)e.Attribute("Id")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var number = ids.Count + 1;
        while (ids.Contains("rId" + number))
        {
            number++;
        }

        var id = "rId" + number;
        var folder = XlsxPackage.FolderOf(partName);
        var target = folder.Length > 0 && targetPart.StartsWith(folder + "/", StringComparison.Ordinal) ? targetPart[(folder.Length + 1)..] : "/" + targetPart;
        root.Add(new XElement(root.Name.Namespace + "Relationship", new XAttribute("Id", id), new XAttribute("Type", type), new XAttribute("Target", target)));
        MarkDirty(relationshipsPart);
        return id;
    }

    /// <summary>Removes relationships of a part by id.</summary>
    public void RemoveRelationship(string partName, string id)
    {
        var relationshipsPart = XlsxPackage.RelationshipsOf(partName);
        if (Xml(relationshipsPart)?.Root is not { } root)
        {
            return;
        }

        var found = root.Elements().Where(e => (string?)e.Attribute("Id") == id).ToList();
        if (found.Count > 0)
        {
            found.ForEach(e => e.Remove());
            MarkDirty(relationshipsPart);
        }
    }

    /// <summary>
    /// Writes the package: unchanged, the bytes it was read from; otherwise every part in order, changed ones from
    /// their trees and the others with their bytes as they were.
    /// </summary>
    public void Save(Stream output)
    {
        if (_source is not null && !IsModified)
        {
            output.Write(_source);
            return;
        }

        var copied = 0L;
        void Count(long bytes)
        {
            copied += bytes;
            if (copied > Guard.Limits.MaxDecompressedBytes)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxDecompressedBytes), Guard.Limits.MaxDecompressedBytes,
                    $"The workbook decompresses to more than ResourceLimits.MaxDecompressedBytes ({Guard.Limits.MaxDecompressedBytes} bytes).");
            }
        }

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var part in _parts.Where(p => !p.Removed))
        {
            var entry = zip.CreateEntry(part.EntryName, part.EntryName.EndsWith('/') ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            entry.LastWriteTime = part.LastWrite;
            using var stream = entry.Open();
            if (part.Dirty && part.Xml is not null)
            {
                Write(part.Xml, stream);
            }
            else if (part.Bytes is not null)
            {
                stream.Write(part.Bytes);
            }
            else if (part.Xml is not null && part.Entry is null)
            {
                Write(part.Xml, stream);
            }
            else if (part.Entry is not null)
            {
                Copy(part.Entry, stream, Count);
            }
        }
    }

    /// <summary>Copies an entry's decompressed bytes, counting them.</summary>
    private void Copy(ZipArchiveEntry entry, Stream output, Action<long> count)
    {
        try
        {
            using var input = entry.Open();
            var chunk = new byte[81920];
            int read;
            while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                count(read);
                output.Write(chunk, 0, read);
                Guard.Checkpoint();
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            throw new SpreadsheetFormatException($"The workbook part '{entry.FullName}' cannot be decompressed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parses a part's XML into a tree within the guard's limits: elements, attributes (namespace declarations too, so
    /// that prefixes are written as they were), text, comments and processing instructions.
    /// </summary>
    internal static XDocument Load(byte[] data, string name, WorkbookGuard guard)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(XlsxPackage.Decode(data)), ReaderSettings);
            var document = new XDocument();
            XContainer current = document;
            var stack = new Stack<XContainer>();
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.XmlDeclaration:
                        document.Declaration = new XDeclaration("1.0", "UTF-8", reader.GetAttribute("standalone"));
                        break;
                    case XmlNodeType.Element:
                        guard.Element(stack.Count);
                        var element = new XElement(XName.Get(reader.LocalName, reader.NamespaceURI));
                        if (reader.MoveToFirstAttribute())
                        {
                            do
                            {
                                XName attributeName = reader.NamespaceURI == XNamespace.Xmlns.NamespaceName
                                    ? reader.Prefix.Length == 0 ? "xmlns" : XNamespace.Xmlns + reader.LocalName
                                    : XName.Get(reader.LocalName, reader.NamespaceURI);
                                element.SetAttributeValue(attributeName, reader.Value);
                            }
                            while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }

                        current.Add(element);
                        if (!reader.IsEmptyElement)
                        {
                            stack.Push(current);
                            current = element;
                        }

                        break;
                    case XmlNodeType.EndElement:
                        current = stack.Pop();
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        if (current is XElement)
                        {
                            current.Add(new XText(reader.Value));
                        }

                        break;
                    case XmlNodeType.Comment:
                        current.Add(new XComment(reader.Value));
                        break;
                    case XmlNodeType.ProcessingInstruction:
                        current.Add(new XProcessingInstruction(reader.Name, reader.Value));
                        break;
                }
            }

            if (document.Root is null)
            {
                throw new SpreadsheetFormatException($"The workbook part '{name}' has no XML element.");
            }

            return document;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            throw new SpreadsheetFormatException($"The workbook part '{name}' is not well-formed XML: {ex.Message}", ex);
        }
    }

    /// <summary>Writes a tree as UTF-8 XML without a byte order mark.</summary>
    internal static void Write(XDocument document, Stream output)
    {
        using var writer = XmlWriter.Create(output, WriterSettings);
        document.Save(writer);
    }

    /// <summary>A tree as UTF-8 XML bytes.</summary>
    internal static byte[] Serialize(XDocument document)
    {
        using var buffer = new MemoryStream();
        Write(document, buffer);
        return buffer.ToArray();
    }

    private sealed class Part(string entryName, string name)
    {
        public string EntryName { get; } = entryName;

        public string Name { get; } = name;

        public ZipArchiveEntry? Entry { get; init; }

        public DateTimeOffset LastWrite { get; init; }

        public XDocument? Xml { get; set; }

        public byte[]? Bytes { get; set; }

        public bool Dirty { get; set; }

        public bool Removed { get; set; }
    }
}
