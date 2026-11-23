using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using RaiCore.Pdf.Cfb;

namespace RaiCore.Pdf.Tests.Cfb;

/// <summary>
/// The compound file reader against fixtures made by make-cfb.py, whose expected.json records what olefile (an
/// independent reader) finds in them; and against damaged copies of them.
/// </summary>
public class CompoundFileTests
{
    private static JsonElement Expected(string file) =>
        JsonDocument.Parse(File.ReadAllText(Fixtures.Path("Cfb/expected.json"))).RootElement.GetProperty(file);

    /// <summary>Every stream under a storage, by its path (names joined by '/').</summary>
    private static Dictionary<string, CfbEntry> Streams(CfbEntry storage, string prefix = "")
    {
        var streams = new Dictionary<string, CfbEntry>(StringComparer.Ordinal);
        foreach (var child in storage.Children)
        {
            if (child.Kind == CfbEntryKind.Stream)
            {
                streams[prefix + child.Name] = child;
            }
            else
            {
                foreach (var (path, entry) in Streams(child, prefix + child.Name + "/"))
                {
                    streams[path] = entry;
                }
            }
        }

        return streams;
    }

    [Theory]
    [InlineData("v3-basic.cfb")]
    [InlineData("v3-difat.cfb")]
    [InlineData("v4-basic.cfb")]
    public void Every_stream_reads_as_olefile_reads_it(string file)
    {
        var expected = Expected(file);
        var cfb = CompoundFile.Open(Fixtures.Read("Cfb/" + file));

        Assert.Equal(expected.GetProperty("version").GetInt32(), cfb.Version);
        Assert.Equal(expected.GetProperty("fat_sectors").GetInt32(), cfb.FatSectorCount);
        var streams = Streams(cfb.Root);
        var expectedStreams = expected.GetProperty("streams").EnumerateObject().ToList();
        Assert.Equal(expectedStreams.Select(s => s.Name).Order(StringComparer.Ordinal), streams.Keys.Order(StringComparer.Ordinal));
        foreach (var stream in expectedStreams)
        {
            var data = cfb.Read(streams[stream.Name]);
            Assert.Equal(stream.Value.GetProperty("size").GetInt32(), data.Length);
            Assert.Equal(stream.Value.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(data)));
        }
    }

    [Fact]
    public void Entries_are_found_by_name_without_case_and_know_what_they_are()
    {
        var cfb = CompoundFile.Open(Fixtures.Read("Cfb/v3-basic.cfb"));

        Assert.Equal(CfbEntryKind.Root, cfb.Root.Kind);
        var sub = cfb.Root["SUB"]!;
        Assert.Equal(CfbEntryKind.Storage, sub.Kind);
        Assert.Equal(CfbEntryKind.Stream, sub["inner"]!.Kind);
        Assert.Equal(5000, sub["inner"]!.Size);
        Assert.Null(cfb.Root["Missing"]);
        Assert.Throws<ArgumentException>(() => cfb.Read(sub));
        Assert.Empty(cfb.Read(cfb.Root["Empty"]!));
        Assert.True(CompoundFile.IsCompoundFile(Fixtures.Read("Cfb/v4-basic.cfb")));
        Assert.False(CompoundFile.IsCompoundFile("%PDF-1.7"u8));
    }

    [Theory]
    [InlineData("v3-basic.cfb")]
    [InlineData("v4-basic.cfb")]
    public void Summary_information_reads_as_olefile_reads_it(string file)
    {
        var expected = Expected(file);
        var cfb = CompoundFile.Open(Fixtures.Read("Cfb/" + file));

        var summary = PropertySetStream.Summary(cfb.Read(cfb.Root["\u0005SummaryInformation"]!));

        Assert.Equal(expected.GetProperty("title").GetString(), summary[PropertySetStream.Title]);
        Assert.Equal(expected.GetProperty("author").GetString(), summary[PropertySetStream.Author]);
        Assert.Equal(DateTime.Parse(expected.GetProperty("created").GetString()!, System.Globalization.CultureInfo.InvariantCulture), summary[PropertySetStream.Created]);
        Assert.Equal((short)1252, summary[PropertySetStream.CodePage]);
    }

    [Fact]
    public void A_unicode_property_set_reads_its_8_bit_strings_as_utf_16()
    {
        var cfb = CompoundFile.Open(Fixtures.Read("Cfb/v3-basic.cfb"));

        var (formatId, properties) = Assert.Single(PropertySetStream.Read(cfb.Read(cfb.Root["\u0005DocumentSummaryInformation"]!)));

        Assert.Equal(PropertySetStream.DocumentSummaryInformation, formatId);
        Assert.Equal("Category \u00FCn\u00EFcode", properties[2]);
        Assert.Equal("Analytical Engines Ltd", properties[15]);
        Assert.Equal(0x000C0000, properties[0x17]);
        Assert.Equal(true, properties[0x13]);
        Assert.Empty(PropertySetStream.Summary(cfb.Read(cfb.Root["\u0005DocumentSummaryInformation"]!)));
    }

    [Fact]
    public void What_is_not_a_property_set_throws()
    {
        Assert.Throws<PdfFormatException>(() => PropertySetStream.Read([1, 2, 3]));
        var cfb = CompoundFile.Open(Fixtures.Read("Cfb/v3-basic.cfb"));
        var data = cfb.Read(cfb.Root["\u0005SummaryInformation"]!);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 3); // Three sets.
        Assert.Contains("one or two", Assert.Throws<PdfFormatException>(() => PropertySetStream.Read(data)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_that_point_outside_their_set_are_left_out()
    {
        var cfb = CompoundFile.Open(Fixtures.Read("Cfb/v3-basic.cfb"));
        var data = cfb.Read(cfb.Root["\u0005SummaryInformation"]!);
        var set = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(set));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(set + 8 + 8 + 4), size - 2); // The title's offset (second property): 2 bytes before the end.

        var summary = PropertySetStream.Summary(data);

        Assert.False(summary.ContainsKey(PropertySetStream.Title));
        Assert.Equal("Ada Lovelace", summary[PropertySetStream.Author]);
    }

    // --- Damage ------------------------------------------------------------------------------------------------------

    /// <summary>The file offset of FAT entry <paramref name="index"/> of a version 3 file (first FAT sector only).</summary>
    private static int FatEntry(byte[] file, int index) => ((int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(76)) + 1) * 512 + (4 * index);

    private static int FirstDirectorySector(byte[] file) => (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(48));

    /// <summary>The file offset of the directory entry with a name (version 3, first directory sector chain followed).</summary>
    private static int DirectoryEntry(byte[] file, string name)
    {
        var sector = FirstDirectorySector(file);
        for (var steps = 0; steps < 100; steps++)
        {
            for (var i = 0; i < 4; i++)
            {
                var offset = ((sector + 1) * 512) + (128 * i);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(offset + 64));
                if (length >= 2 && System.Text.Encoding.Unicode.GetString(file, offset, length - 2) == name)
                {
                    return offset;
                }
            }

            sector = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(FatEntry(file, sector)));
        }

        throw new InvalidOperationException(name);
    }

    [Fact]
    public void Data_that_is_not_a_compound_file_throws()
    {
        Assert.Throws<PdfFormatException>(() => CompoundFile.Open("not a compound file"u8.ToArray()));
        var file = Fixtures.Read("Cfb/v3-basic.cfb");
        file[0] = 0; // Only the signature is wrong.
        Assert.Throws<PdfFormatException>(() => CompoundFile.Open(file));
        file = Fixtures.Read("Cfb/v3-basic.cfb");
        Assert.Throws<PdfFormatException>(() => CompoundFile.Open(file.AsMemory(0, 300)));
        file[30] = 12; // Sector shift 12 in a version 3 header.
        Assert.Throws<PdfFormatException>(() => CompoundFile.Open(file));
        file = Fixtures.Read("Cfb/v3-basic.cfb");
        file[28] = 0; // Byte order.
        Assert.Throws<PdfFormatException>(() => CompoundFile.Open(file));
    }

    [Fact]
    public void A_directory_chain_that_loops_throws()
    {
        var file = Fixtures.Read("Cfb/v3-basic.cfb");
        var first = FirstDirectorySector(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(FatEntry(file, first)), (uint)first);

        Assert.Contains("loops", Assert.Throws<PdfFormatException>(() => CompoundFile.Open(file)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sibling_that_points_back_is_visited_once()
    {
        var file = Fixtures.Read("Cfb/v3-basic.cfb");
        var entry = DirectoryEntry(file, "Small");
        var index = (uint)((entry - ((FirstDirectorySector(file) + 1) * 512)) / 128);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 72), index); // Its right sibling: itself.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 68), 0); // Its left sibling: the root.

        var cfb = CompoundFile.Open(file);

        Assert.Single(cfb.Root.Children, c => c.Name == "Small");
        Assert.DoesNotContain(cfb.Root.Children, c => c.Kind == CfbEntryKind.Root);
    }

    [Fact]
    public void A_stream_that_points_outside_the_file_throws_when_read()
    {
        var file = Fixtures.Read("Cfb/v3-basic.cfb");
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(DirectoryEntry(file, "Large") + 116), 50_000);

        var cfb = CompoundFile.Open(file);

        Assert.Throws<PdfFormatException>(() => cfb.Read(cfb.Root["Large"]!));
        Assert.Equal(100, cfb.Read(cfb.Root["Small"]!).Length); // The rest still reads.
    }

    [Fact]
    public void A_stream_longer_than_its_chain_throws()
    {
        var file = Fixtures.Read("Cfb/v3-basic.cfb");
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(DirectoryEntry(file, "Large") + 120), 10_241); // Its 20 sectors hold 10,240.

        var cfb = CompoundFile.Open(file);

        Assert.Contains("shorter", Assert.Throws<PdfFormatException>(() => cfb.Read(cfb.Root["Large"]!)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mini_stream_sector_outside_the_mini_stream_throws()
    {
        var file = Fixtures.Read("Cfb/v3-basic.cfb");
        var miniSectors = (uint)(CompoundFile.Open(file).Root.Size / 64);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(DirectoryEntry(file, "Small") + 116), miniSectors); // Just past its end.

        var cfb = CompoundFile.Open(file);

        Assert.Throws<PdfFormatException>(() => cfb.Read(cfb.Root["Small"]!));
    }

    [Fact]
    public void Too_many_fat_sectors_for_the_file_throws()
    {
        var file = Fixtures.Read("Cfb/v3-basic.cfb");
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(44), (uint)(file.Length / 512)); // One more than it has sectors.

        Assert.Throws<PdfFormatException>(() => CompoundFile.Open(file));
    }

    [Fact]
    public void A_first_entry_that_is_not_the_root_throws()
    {
        var file = Fixtures.Read("Cfb/v3-basic.cfb");
        file[((FirstDirectorySector(file) + 1) * 512) + 66] = 1;

        Assert.Throws<PdfFormatException>(() => CompoundFile.Open(file));
    }

    [Fact]
    public void Limits_cap_entries_depth_and_stream_size()
    {
        var file = Fixtures.Read("Cfb/v3-basic.cfb");

        Assert.Equal(nameof(ResourceLimits.MaxElements), Assert.Throws<ResourceLimitExceededException>(
            () => CompoundFile.Open(file, ResourceLimits.Default with { MaxElements = 15 })).LimitName); // 16 entries.
        Assert.NotNull(CompoundFile.Open(file, ResourceLimits.Default with { MaxElements = 16 }).Root["Small"]);
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), Assert.Throws<ResourceLimitExceededException>(
            () => CompoundFile.Open(file, ResourceLimits.Default with { MaxNestingDepth = 1 })).LimitName);
        var small = CompoundFile.Open(file, ResourceLimits.Default with { MaxDecompressedBytes = 5000 });
        Assert.Equal(nameof(ResourceLimits.MaxDecompressedBytes), Assert.Throws<ResourceLimitExceededException>(() => small.Read(small.Root["Large"]!)).LimitName);
        Assert.Equal(5000, small.Read(small.Root["Sub"]!["Inner"]!).Length);
    }

    [Fact]
    public void A_fat_sector_cut_short_by_the_end_of_the_file_reads_as_free_entries()
    {
        // Found by fuzzing: a FAT sector that is the file's last, shorter than a sector, was read past its end.
        var file = Fixtures.Read("Cfb/v3-basic.cfb");
        var sectors = (file.Length / 512) - 1;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(76 + 4), (uint)(sectors - 1)); // A second FAT sector: the last.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(44), 2);

        var cfb = CompoundFile.Open(file.AsMemory(0, file.Length - 300));

        Assert.Equal(100, cfb.Read(cfb.Root["Small"]!).Length);
    }

    [Fact]
    public void A_linked_entry_marked_unallocated_is_left_out()
    {
        var file = Fixtures.Read("Cfb/v3-basic.cfb");
        file[DirectoryEntry(file, "Small") + 66] = 0;

        var cfb = CompoundFile.Open(file);

        Assert.Null(cfb.Root["Small"]);
        Assert.NotNull(cfb.Root["Large"]);
    }

    [Fact]
    public void A_version_3_size_ignores_its_high_half()
    {
        var file = Fixtures.Read("Cfb/v3-basic.cfb");
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(DirectoryEntry(file, "Small") + 124), 0xDEADBEEF);

        var cfb = CompoundFile.Open(file);

        Assert.Equal(100, cfb.Root["Small"]!.Size);
        Assert.Equal(100, cfb.Read(cfb.Root["Small"]!).Length);
    }
}
