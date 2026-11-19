using System.Buffers.Binary;
using System.Text;

namespace RaiCore.Pdf.Email.Cfb;

/// <summary>
/// A Compound File Binary file ([MS-CFB], the OLE structured storage of <c>.msg</c>, <c>.doc</c>, <c>.xls</c> and
/// <c>.ppt</c>): a small file system of storages (folders) and streams (files) in fixed-size sectors. Version 3
/// (512-byte sectors) and version 4 (4096-byte sectors) are read.
/// </summary>
/// <remarks>
/// The file is read with care for damage and hostile input: every chain of sectors is followed at most as many steps
/// as the file has sectors (so a loop ends), the directory tree is walked without recursion and visits each entry once,
/// and the number of entries (<see cref="ResourceLimits.MaxElements"/>), the storage depth
/// (<see cref="ResourceLimits.MaxNestingDepth"/>) and the size of a stream read
/// (<see cref="ResourceLimits.MaxDecompressedBytes"/>) are limited. A file that is not a compound file, or whose
/// structures point outside it, throws <see cref="PdfFormatException"/>.
/// </remarks>
internal sealed class CompoundFile
{
    /// <summary>The first eight bytes of every compound file.</summary>
    public static ReadOnlySpan<byte> Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    // Special sector numbers ([MS-CFB] 2.1).
    private const uint MaxRegularSector = 0xFFFFFFFA;
    private const uint NoStream = 0xFFFFFFFF;

    private const int HeaderDifatEntries = 109;
    private const int DirectoryEntrySize = 128;
    private const int MiniSectorSize = 64;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly ResourceLimits _limits;
    private readonly int _sectorSize;
    private readonly uint _miniStreamCutoff;
    private readonly uint[] _fat;
    private readonly long _sectorCount;
    private uint[]? _miniFat;
    private byte[]? _miniStream;
    private readonly uint _firstMiniFatSector;

    private CompoundFile(ReadOnlyMemory<byte> data, ResourceLimits limits)
    {
        _data = data;
        _limits = limits;
        var header = data.Span;
        if (header.Length < 512 || !header.StartsWith(Signature))
        {
            throw new PdfFormatException("The file is not a compound file (no compound file signature).");
        }

        Version = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
        var byteOrder = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
        var miniSectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
        if (byteOrder != 0xFFFE || (Version, sectorShift) is not ((3, 9) or (4, 12)) || miniSectorShift != 6)
        {
            throw new PdfFormatException(
                $"The compound file header is not one of version 3 or 4 (version {Version}, sector shift {sectorShift}, mini sector shift {miniSectorShift}).");
        }

        _sectorSize = 1 << sectorShift;
        _sectorCount = Math.Max(0, (header.Length - _sectorSize + _sectorSize - 1) / _sectorSize);
        var fatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header[44..]);
        var firstDirectorySector = BinaryPrimitives.ReadUInt32LittleEndian(header[48..]);
        _miniStreamCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header[56..]);
        _firstMiniFatSector = BinaryPrimitives.ReadUInt32LittleEndian(header[60..]);
        var firstDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(header[68..]);
        if (fatSectorCount > _sectorCount)
        {
            throw new PdfFormatException($"The compound file claims {fatSectorCount} FAT sectors but has only {_sectorCount} sectors.");
        }

        _fat = ReadFat(header, (int)fatSectorCount, firstDifatSector);
        FatSectorCount = _fat.Length / (_sectorSize / 4);
        Root = ReadDirectory(firstDirectorySector);
    }

    /// <summary>The format version: 3 (512-byte sectors) or 4 (4096-byte sectors).</summary>
    public int Version { get; }

    /// <summary>How many FAT sectors were found (from the header and the DIFAT chain).</summary>
    internal int FatSectorCount { get; }

    /// <summary>The root storage: the top of the tree of storages and streams.</summary>
    public CfbEntry Root { get; }

    /// <summary>Whether data starts like a compound file.</summary>
    public static bool IsCompoundFile(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    /// <summary>Reads a compound file.</summary>
    /// <exception cref="PdfFormatException">It is not a compound file, or it is damaged beyond reading.</exception>
    /// <exception cref="ResourceLimitExceededException">It has more entries or deeper storages than the limits allow.</exception>
    public static CompoundFile Open(ReadOnlyMemory<byte> data, ResourceLimits? limits = null) => new(data, limits ?? ResourceLimits.Default);

    /// <summary>Reads a stream's data.</summary>
    /// <exception cref="ArgumentException">The entry is a storage (the root reads as the mini stream container).</exception>
    /// <exception cref="PdfFormatException">The stream's sectors are missing or loop.</exception>
    /// <exception cref="ResourceLimitExceededException">The stream is larger than <see cref="ResourceLimits.MaxDecompressedBytes"/>.</exception>
    public byte[] Read(CfbEntry stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Kind == CfbEntryKind.Storage)
        {
            throw new ArgumentException($"'{stream.Name}' is a storage, not a stream.", nameof(stream));
        }

        if (stream.Size > _limits.MaxDecompressedBytes)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxDecompressedBytes), _limits.MaxDecompressedBytes,
                $"The stream '{stream.Name}' is {stream.Size} bytes, more than {_limits.MaxDecompressedBytes} (ResourceLimits.MaxDecompressedBytes).");
        }

        return stream.Kind != CfbEntryKind.Root && stream.Size < _miniStreamCutoff
            ? ReadMini(stream.StartSector, (int)stream.Size, stream.Name)
            : ReadChain(_fat, stream.StartSector, (int)stream.Size, _sectorSize, _sectorCount, SectorSpan, stream.Name);
    }

    /// <summary>The FAT: from the sectors the header lists, then the DIFAT chain for the rest.</summary>
    private uint[] ReadFat(ReadOnlySpan<byte> header, int fatSectorCount, uint firstDifatSector)
    {
        var fatSectors = new List<uint>(fatSectorCount);
        for (var i = 0; i < HeaderDifatEntries && fatSectors.Count < fatSectorCount; i++)
        {
            var sector = BinaryPrimitives.ReadUInt32LittleEndian(header[(76 + (4 * i))..]);
            if (sector <= MaxRegularSector)
            {
                fatSectors.Add(sector);
            }
        }

        var perDifatSector = (_sectorSize / 4) - 1;
        var difat = firstDifatSector;
        for (var steps = 0L; fatSectors.Count < fatSectorCount && difat <= MaxRegularSector; steps++)
        {
            if (steps >= _sectorCount)
            {
                throw new PdfFormatException("The compound file's DIFAT chain loops.");
            }

            var entries = TableSector(difat);
            for (var i = 0; i < perDifatSector && fatSectors.Count < fatSectorCount; i++)
            {
                var sector = BinaryPrimitives.ReadUInt32LittleEndian(entries[(4 * i)..]);
                if (sector <= MaxRegularSector)
                {
                    fatSectors.Add(sector);
                }
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(entries[(4 * perDifatSector)..]);
        }

        var perSector = _sectorSize / 4;
        var fat = new uint[fatSectors.Count * perSector];
        for (var s = 0; s < fatSectors.Count; s++)
        {
            var span = TableSector(fatSectors[s]);
            for (var i = 0; i < perSector; i++)
            {
                fat[(s * perSector) + i] = BinaryPrimitives.ReadUInt32LittleEndian(span[(4 * i)..]);
            }
        }

        return fat;
    }

    /// <summary>The directory: its entries, then the tree from the root, each entry visited once.</summary>
    private CfbEntry ReadDirectory(uint firstSector)
    {
        var data = ReadChain(_fat, firstSector, -1, _sectorSize, _sectorCount, SectorSpan, "directory");
        var count = data.Length / DirectoryEntrySize;
        if (count == 0)
        {
            throw new PdfFormatException("The compound file has no directory.");
        }

        if (count > _limits.MaxElements)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxElements), _limits.MaxElements,
                $"The compound file has more than {_limits.MaxElements} directory entries (ResourceLimits.MaxElements).");
        }

        var raw = new RawEntry[count];
        for (var i = 0; i < count; i++)
        {
            raw[i] = RawEntry.Parse(data.AsSpan(i * DirectoryEntrySize, DirectoryEntrySize), Version);
        }

        if (raw[0].Type != 5)
        {
            throw new PdfFormatException("The compound file's first directory entry is not the root storage.");
        }

        var visited = new bool[count];
        visited[0] = true;
        var root = new CfbEntry(raw[0].Name, CfbEntryKind.Root, raw[0].Size, raw[0].StartSector, raw[0].Clsid);
        var storages = new Stack<(CfbEntry Storage, uint Child, int Depth)>();
        storages.Push((root, raw[0].Child, 0));
        while (storages.TryPop(out var item))
        {
            if (item.Depth > _limits.MaxNestingDepth)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), _limits.MaxNestingDepth,
                    $"The compound file's storages nest more than {_limits.MaxNestingDepth} deep (ResourceLimits.MaxNestingDepth).");
            }

            // The children of a storage are a red-black tree by name: walked in order, with an explicit stack.
            var pending = new Stack<uint>();
            var node = item.Child;
            while (pending.Count > 0 || IsEntry(node, count))
            {
                while (IsEntry(node, count) && !visited[node])
                {
                    visited[node] = true;
                    pending.Push(node);
                    node = raw[node].Left;
                }

                if (pending.Count == 0)
                {
                    break;
                }

                var index = pending.Pop();
                var entry = raw[index];
                if (entry.Type is 1 or 2)
                {
                    var child = new CfbEntry(entry.Name, entry.Type == 1 ? CfbEntryKind.Storage : CfbEntryKind.Stream, entry.Size, entry.StartSector, entry.Clsid);
                    item.Storage.Add(child);
                    if (entry.Type == 1)
                    {
                        storages.Push((child, entry.Child, item.Depth + 1));
                    }
                }

                node = entry.Right;
            }
        }

        return root;

        static bool IsEntry(uint index, int count) => index != NoStream && index < count;
    }

    /// <summary>A stream in the mini stream: 64-byte sectors chained by the mini FAT.</summary>
    private byte[] ReadMini(uint start, int size, string name)
    {
        _miniFat ??= ToEntries(ReadChain(_fat, _firstMiniFatSector, -1, _sectorSize, _sectorCount, SectorSpan, "mini FAT"));
        _miniStream ??= Read(Root);
        var miniStream = _miniStream;
        return ReadChain(_miniFat, start, size, MiniSectorSize, miniStream.Length / MiniSectorSize, sector =>
        {
            var offset = (long)sector * MiniSectorSize;
            if (offset + MiniSectorSize > miniStream.Length)
            {
                throw new PdfFormatException($"The stream '{name}' points outside the compound file's mini stream.");
            }

            return miniStream.AsSpan((int)offset, MiniSectorSize);
        }, name);
    }

    private delegate ReadOnlySpan<byte> SectorReader(uint sector);

    /// <summary>
    /// Follows a chain of sectors and joins their data: <paramref name="size"/> bytes, or the whole chain for -1. A
    /// chain that does not loop has at most as many sectors as there are (<paramref name="sectors"/>): one with more
    /// loops, and ends there.
    /// </summary>
    private static byte[] ReadChain(uint[] table, uint start, int size, int sectorSize, long sectors, SectorReader sectorAt, string name)
    {
        if (size == 0)
        {
            return [];
        }

        using var output = new MemoryStream((int)Math.Min(size > 0 ? size : sectorSize, sectors * sectorSize));
        var sector = start;
        for (var steps = 0L; sector <= MaxRegularSector && (size < 0 || output.Length < size); steps++)
        {
            if (steps >= sectors || sector >= table.Length)
            {
                throw new PdfFormatException(steps >= sectors
                    ? $"The chain of sectors of '{name}' loops."
                    : $"The chain of sectors of '{name}' points outside the compound file's allocation table.");
            }

            var data = sectorAt(sector);
            output.Write(size < 0 ? data : data[..(int)Math.Min(data.Length, size - output.Length)]);
            sector = table[sector];
        }

        if (size > 0 && output.Length < size)
        {
            throw new PdfFormatException($"The stream '{name}' is shorter than its size ({output.Length} of {size} bytes).");
        }

        return output.ToArray();
    }

    /// <summary>A regular sector's data; a last sector cut short by the end of the file is read as far as it goes.</summary>
    private ReadOnlySpan<byte> SectorSpan(uint sector)
    {
        var offset = ((long)sector + 1) * _sectorSize;
        if (sector > MaxRegularSector || offset >= _data.Length)
        {
            throw new PdfFormatException($"Sector {sector} is outside the compound file.");
        }

        return _data.Span.Slice((int)offset, (int)Math.Min(_sectorSize, _data.Length - offset));
    }

    /// <summary>A sector of a table (FAT, DIFAT): one cut short by the end of the file reads as free entries after its end.</summary>
    private ReadOnlySpan<byte> TableSector(uint sector)
    {
        var span = SectorSpan(sector);
        if (span.Length == _sectorSize)
        {
            return span;
        }

        var padded = new byte[_sectorSize];
        padded.AsSpan().Fill(0xFF);
        span.CopyTo(padded);
        return padded;
    }

    private static uint[] ToEntries(byte[] data)
    {
        var entries = new uint[data.Length / 4];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 * i));
        }

        return entries;
    }

    /// <summary>A directory entry as stored ([MS-CFB] 2.6.1).</summary>
    private readonly record struct RawEntry(string Name, byte Type, uint Left, uint Right, uint Child, Guid Clsid, uint StartSector, long Size)
    {
        public static RawEntry Parse(ReadOnlySpan<byte> entry, int version)
        {
            var nameBytes = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(entry[64..]), 0, 64);
            var name = Encoding.Unicode.GetString(entry[..(nameBytes & ~1)]).TrimEnd('\0');
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[120..]);
            if (version == 3)
            {
                size &= 0xFFFFFFFF; // Version 3 files may leave garbage in the high half.
            }

            return new RawEntry(
                name,
                entry[66],
                BinaryPrimitives.ReadUInt32LittleEndian(entry[68..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[72..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[76..]),
                new Guid(entry.Slice(80, 16)),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[116..]),
                size < 0 ? long.MaxValue : size);
        }
    }
}
