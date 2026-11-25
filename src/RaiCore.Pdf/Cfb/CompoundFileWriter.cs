using System.Buffers.Binary;
using System.Text;

namespace RaiCore.Pdf.Cfb;

/// <summary>
/// Writes a storage of a <see cref="CompoundFile"/> as a compound file of its own ([MS-CFB] version 3: 512-byte
/// sectors, a mini stream for streams under 4096 bytes), so that an Office 97-2003 document embedded as an OLE object
/// (a Word, Excel or PowerPoint storage inside an Outlook message) becomes a <c>.doc</c>, <c>.xls</c> or <c>.ppt</c> file.
/// </summary>
internal static class CompoundFileWriter
{
    private const int SectorSize = 512;
    private const int MiniSectorSize = 64;
    private const int MiniStreamCutoff = 4096;
    private const int EntrySize = 128;
    private const int HeaderDifatEntries = 109;
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FatSector = 0xFFFFFFFD;
    private const uint NoStream = 0xFFFFFFFF;

    /// <summary>The deepest storage nesting copied.</summary>
    private const int MaxDepth = 32;

    /// <summary>An entry to write: a storage (with children) or a stream (with data).</summary>
    private sealed class Node(string name, Guid clsid, byte[]? data)
    {
        public string Name { get; } = name;

        public Guid Clsid { get; } = clsid;

        public byte[]? Data { get; } = data;

        public List<Node> Children { get; } = [];

        public bool IsStream => Data is not null;

        public uint StartSector { get; set; } = EndOfChain;

        public uint Left { get; set; } = NoStream;

        public uint Right { get; set; } = NoStream;

        public uint Child { get; set; } = NoStream;
    }

    /// <summary>
    /// Writes <paramref name="storage"/> of <paramref name="file"/>, with everything in it, as the root of a new compound
    /// file; the root takes the storage's class id.
    /// </summary>
    /// <exception cref="ResourceLimitExceededException">
    /// Its streams together are larger than <see cref="ResourceLimits.MaxDecompressedBytes"/>, or it has more entries or
    /// deeper storages than <see cref="ResourceLimits.MaxElements"/> and the nesting limit allow.
    /// </exception>
    /// <exception cref="NotSupportedException">It needs more than 109 FAT sectors (about 7 MB), which this writer does not do.</exception>
    public static byte[] Write(CompoundFile file, CfbEntry storage, ResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(limits);
        var total = 0L;
        var count = 0;
        var root = new Node("Root Entry", storage.Clsid, null);
        Copy(storage, root, 0);
        return Write(root);

        void Copy(CfbEntry source, Node target, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), MaxDepth, "The embedded storage is nested too deeply.");
            }

            foreach (var child in source.Children)
            {
                if (++count > limits.MaxElements)
                {
                    throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxElements), limits.MaxElements, "The embedded storage has too many entries.");
                }

                if (child.Kind == CfbEntryKind.Stream)
                {
                    var data = file.Read(child);
                    total += data.Length;
                    if (total > limits.MaxDecompressedBytes)
                    {
                        throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxDecompressedBytes), limits.MaxDecompressedBytes,
                            "The embedded storage's streams together are larger than the limit.");
                    }

                    target.Children.Add(new Node(child.Name, Guid.Empty, data));
                }
                else
                {
                    var node = new Node(child.Name, child.Clsid, null);
                    Copy(child, node, depth + 1);
                    target.Children.Add(node);
                }
            }
        }
    }

    private static byte[] Write(Node root)
    {
        // Directory entries in breadth-first order, each storage's children a balanced tree in compound file name order.
        var entries = new List<Node> { root };
        for (var i = 0; i < entries.Count; i++)
        {
            var node = entries[i];
            if (node.IsStream)
            {
                continue;
            }

            var sorted = node.Children.OrderBy(c => c.Name.Length).ThenBy(c => c.Name.ToUpperInvariant(), StringComparer.Ordinal).ToList();
            var first = entries.Count;
            entries.AddRange(sorted);
            node.Child = Balance(sorted, first, 0, sorted.Count);
        }

        // The mini stream holds the small streams, 64-byte sectors one after the other.
        var miniFat = new List<uint>();
        var miniStream = new MemoryStream();
        foreach (var node in entries.Where(n => n.IsStream && n.Data!.Length > 0 && n.Data.Length < MiniStreamCutoff))
        {
            var sectors = (node.Data!.Length + MiniSectorSize - 1) / MiniSectorSize;
            node.StartSector = (uint)miniFat.Count;
            for (var s = 0; s < sectors; s++)
            {
                miniFat.Add(s == sectors - 1 ? EndOfChain : (uint)(miniFat.Count + 1));
            }

            miniStream.Write(node.Data);
            miniStream.Write(new byte[(sectors * MiniSectorSize) - node.Data.Length]);
        }

        var large = entries.Where(n => n.IsStream && n.Data!.Length >= MiniStreamCutoff).ToList();
        int Sectors(long bytes) => (int)((bytes + SectorSize - 1) / SectorSize);
        var miniFatSectors = Sectors(miniFat.Count * 4L);
        var directorySectors = Sectors(entries.Count * (long)EntrySize);
        var miniStreamSectors = Sectors(miniStream.Length);
        var dataSectors = miniFatSectors + directorySectors + miniStreamSectors + large.Sum(n => Sectors(n.Data!.Length));
        var fatSectors = 1;
        while (fatSectors * (SectorSize / 4) < fatSectors + dataSectors)
        {
            fatSectors++;
        }

        if (fatSectors > HeaderDifatEntries)
        {
            throw new NotSupportedException("The embedded storage is too large to write as a compound file of its own.");
        }

        // Sectors: the FAT, the mini FAT, the directory, the mini stream, then the large streams.
        var fat = new uint[fatSectors * (SectorSize / 4)];
        Array.Fill(fat, FreeSector);
        var next = 0u;
        for (var i = 0; i < fatSectors; i++)
        {
            fat[next++] = FatSector;
        }

        uint Chain(int sectors)
        {
            if (sectors == 0)
            {
                return EndOfChain;
            }

            var start = next;
            for (var i = 0; i < sectors; i++, next++)
            {
                fat[next] = i == sectors - 1 ? EndOfChain : next + 1;
            }

            return start;
        }

        var firstMiniFat = Chain(miniFatSectors);
        var firstDirectory = Chain(directorySectors);
        root.StartSector = Chain(miniStreamSectors);
        foreach (var node in large)
        {
            node.StartSector = Chain(Sectors(node.Data!.Length));
        }

        var output = new byte[SectorSize * (1 + (int)next)];
        WriteHeader(output, fatSectors, firstDirectory, firstMiniFat, miniFatSectors);
        for (var i = 0; i < fat.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(SectorSize + (4 * i)), fat[i]);
        }

        var miniFatBytes = output.AsSpan(SectorSize * (1 + (int)firstMiniFat), miniFatSectors * SectorSize);
        miniFatBytes.Fill(0xFF);
        for (var i = 0; i < miniFat.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(miniFatBytes[(4 * i)..], miniFat[i]);
        }

        var directory = output.AsSpan(SectorSize * (1 + (int)firstDirectory), directorySectors * SectorSize);
        for (var i = 0; i < directorySectors * (SectorSize / EntrySize); i++)
        {
            var entry = directory.Slice(i * EntrySize, EntrySize);
            if (i < entries.Count)
            {
                WriteEntry(entry, entries[i], i == 0 ? miniStream.Length : entries[i].Data?.Length ?? 0, i == 0);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], NoStream);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], NoStream);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], NoStream);
            }
        }

        if (miniStreamSectors > 0)
        {
            miniStream.GetBuffer().AsSpan(0, (int)miniStream.Length).CopyTo(output.AsSpan(SectorSize * (1 + (int)root.StartSector)));
        }

        foreach (var node in large)
        {
            node.Data.CopyTo(output.AsSpan(SectorSize * (1 + (int)node.StartSector))); // Its sectors follow each other.
        }

        return output;
    }

    /// <summary>Links <paramref name="sorted"/>[start..end) as a balanced binary tree; returns the directory index of its root.</summary>
    private static uint Balance(List<Node> sorted, int firstIndex, int start, int end)
    {
        if (start >= end)
        {
            return NoStream;
        }

        var middle = (start + end) / 2;
        sorted[middle].Left = Balance(sorted, firstIndex, start, middle);
        sorted[middle].Right = Balance(sorted, firstIndex, middle + 1, end);
        return (uint)(firstIndex + middle);
    }

    private static void WriteHeader(Span<byte> output, int fatSectors, uint firstDirectory, uint firstMiniFat, int miniFatSectors)
    {
        CompoundFile.Signature.CopyTo(output);
        BinaryPrimitives.WriteUInt16LittleEndian(output[24..], 0x003E); // Minor version.
        BinaryPrimitives.WriteUInt16LittleEndian(output[26..], 3); // Major version.
        BinaryPrimitives.WriteUInt16LittleEndian(output[28..], 0xFFFE); // Little-endian.
        BinaryPrimitives.WriteUInt16LittleEndian(output[30..], 9); // 512-byte sectors.
        BinaryPrimitives.WriteUInt16LittleEndian(output[32..], 6); // 64-byte mini sectors.
        BinaryPrimitives.WriteUInt32LittleEndian(output[44..], (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(output[48..], firstDirectory);
        BinaryPrimitives.WriteUInt32LittleEndian(output[56..], MiniStreamCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(output[60..], miniFatSectors == 0 ? EndOfChain : firstMiniFat);
        BinaryPrimitives.WriteUInt32LittleEndian(output[64..], (uint)miniFatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(output[68..], EndOfChain); // No DIFAT sectors.
        for (var i = 0; i < HeaderDifatEntries; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output[(76 + (4 * i))..], i < fatSectors ? (uint)i : FreeSector);
        }
    }

    private static void WriteEntry(Span<byte> entry, Node node, long size, bool isRoot)
    {
        var name = node.Name.Length > 31 ? node.Name[..31] : node.Name;
        var nameBytes = Encoding.Unicode.GetBytes(name);
        nameBytes.CopyTo(entry);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[64..], (ushort)(nameBytes.Length + 2));
        entry[66] = isRoot ? (byte)5 : node.IsStream ? (byte)2 : (byte)1;
        entry[67] = 1; // Black: every child tree is balanced, which a red-black tree of black nodes allows.
        BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], node.Left);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], node.Right);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], node.Child);
        node.Clsid.TryWriteBytes(entry.Slice(80, 16));
        var start = isRoot ? node.StartSector : !node.IsStream ? 0 : size == 0 ? EndOfChain : node.StartSector; // Storages: 0.
        BinaryPrimitives.WriteUInt32LittleEndian(entry[116..], start);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[120..], (ulong)size);
    }
}
