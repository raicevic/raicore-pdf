using System.IO.Compression;
using System.Text;
using System.Xml;
using RaiCore.Pdf.Cfb;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// The ZIP package of a workbook (Open Packaging Conventions): its parts read with the decompressed bytes bounded by
/// <see cref="ResourceLimits.MaxDecompressedBytes"/> (for all parts together), and parsed as XML with no DTDs and
/// nothing external resolved. Malformed ZIP or XML data throws <see cref="SpreadsheetFormatException"/>.
/// </summary>
internal sealed class XlsxPackage : IDisposable
{
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false,
        MaxCharactersFromEntities = 1024,
    };

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _limit;
    private readonly Action? _checkpoint;
    private long _read;

    private XlsxPackage(ZipArchive zip, long limit, Action? checkpoint)
    {
        _zip = zip;
        _limit = limit;
        _checkpoint = checkpoint;
        foreach (var entry in zip.Entries)
        {
            _entries.TryAdd(entry.FullName.Replace('\\', '/').TrimStart('/'), entry);
        }
    }

    /// <summary>Opens a package.</summary>
    /// <exception cref="NotSupportedException">The data is an encrypted workbook or an Excel 97-2003 file.</exception>
    /// <exception cref="SpreadsheetFormatException">The data is not a ZIP package.</exception>
    public static XlsxPackage Open(byte[] data, ResourceLimits limits, Action? checkpoint = null)
    {
        if (CompoundFile.IsCompoundFile(data))
        {
            throw new NotSupportedException(
                "The data is a Compound File: a password-protected (encrypted) workbook or an Excel 97-2003 (.xls) file, which are not supported.");
        }

        if (!data.AsSpan().StartsWith("PK"u8))
        {
            throw new SpreadsheetFormatException("The data is not an XLSX workbook (a ZIP package).");
        }

        try
        {
            return new XlsxPackage(new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read), limits.MaxDecompressedBytes, checkpoint);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            throw new SpreadsheetFormatException("The data is not a valid ZIP package: " + ex.Message, ex);
        }
    }

    /// <summary>Whether the package has a part.</summary>
    public bool Contains(string path) => _entries.ContainsKey(path);

    /// <summary>A part's bytes, or <c>null</c> if the package has no such part.</summary>
    /// <exception cref="ResourceLimitExceededException">The parts read decompress to more than the limit.</exception>
    public byte[]? Read(string path)
    {
        if (!_entries.TryGetValue(path, out var entry))
        {
            return null;
        }

        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream((int)Math.Min(Math.Max(entry.Length, 0), 1 << 20));
            var chunk = new byte[81920];
            int count;
            while ((count = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                _read += count;
                if (_read > _limit)
                {
                    throw Exceeded();
                }

                buffer.Write(chunk, 0, count);
                _checkpoint?.Invoke();
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            throw new SpreadsheetFormatException($"The workbook part '{path}' cannot be decompressed: {ex.Message}", ex);
        }
    }

    private ResourceLimitExceededException Exceeded() => new(nameof(ResourceLimits.MaxDecompressedBytes), _limit,
        $"The workbook decompresses to more than ResourceLimits.MaxDecompressedBytes ({_limit} bytes).");

    /// <summary>
    /// Parses a part as XML with <paramref name="parse"/>, or returns <paramref name="missing"/> if the package has no
    /// such part. Malformed XML throws <see cref="SpreadsheetFormatException"/>.
    /// </summary>
    public T Parse<T>(string path, Func<XmlReader, T> parse, T missing)
    {
        if (Read(path) is not { } data)
        {
            return missing;
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(Decode(data)), XmlSettings);
            return parse(reader);
        }
        catch (XmlException ex)
        {
            throw new SpreadsheetFormatException($"The workbook part '{path}' is not well-formed XML: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Decodes a part's text: UTF-16 or UTF-8 by its byte order mark (or the zero bytes of UTF-16 without one), else
    /// UTF-8, with invalid bytes as U+FFFD. The XML reader gets text, so a declared encoding cannot switch it.
    /// </summary>
    internal static string Decode(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        }

        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }

        if (data.Length >= 4 && data[0] == '<' && data[1] == 0 && data[2] != 0)
        {
            return Encoding.Unicode.GetString(data);
        }

        if (data.Length >= 4 && data[0] == 0 && data[1] == '<')
        {
            return Encoding.BigEndianUnicode.GetString(data);
        }

        return Encoding.UTF8.GetString(data);
    }

    /// <summary>
    /// Resolves a relationship's target against the folder of the part that has it (<c>xl</c> for
    /// <c>xl/workbook.xml</c>): relative, or absolute from the package root, with <c>..</c> and escapes resolved.
    /// </summary>
    internal static string ResolveTarget(string folder, string target)
    {
        target = target.Replace('\\', '/');
        try
        {
            target = Uri.UnescapeDataString(target);
        }
        catch (UriFormatException)
        {
        }

        var segments = new List<string>();
        if (!target.StartsWith('/'))
        {
            segments.AddRange(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var segment in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else if (segment != ".")
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>The folder of a part (<c>xl</c> for <c>xl/workbook.xml</c>, empty at the root).</summary>
    internal static string FolderOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>The relationships part of a part (<c>xl/_rels/workbook.xml.rels</c> for <c>xl/workbook.xml</c>).</summary>
    internal static string RelationshipsOf(string path)
    {
        var folder = FolderOf(path);
        var name = path[(folder.Length == 0 ? 0 : folder.Length + 1)..];
        return (folder.Length == 0 ? string.Empty : folder + "/") + "_rels/" + name + ".rels";
    }

    /// <summary>
    /// Reads a relationships part: each internal relationship's id, type (the last segment of its URI, such as
    /// <c>worksheet</c>, which strict and transitional files share) and resolved target.
    /// </summary>
    public List<(string Id, string Type, string Target)> Relationships(string partPath)
    {
        var folder = FolderOf(partPath);
        return Parse(RelationshipsOf(partPath), reader =>
        {
            var result = new List<(string, string, string)>();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship"
                    && reader.GetAttribute("TargetMode") is not "External"
                    && reader.GetAttribute("Target") is { } target && reader.GetAttribute("Type") is { } type)
                {
                    result.Add((reader.GetAttribute("Id") ?? string.Empty, type[(type.LastIndexOf('/') + 1)..], ResolveTarget(folder, target)));
                }
            }

            return result;
        }, []);
    }

    public void Dispose() => _zip.Dispose();
}
