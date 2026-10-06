using System.Text;
using R1ng.Launcher.Core;
using R1ng.Launcher.Files;

namespace R1ng.Launcher.Tests;

public class EverythingProtocolTests
{
    [Fact]
    public void BuildQuery2_writes_the_packed_query2w_layout()
    {
        var buffer = EverythingProtocol.BuildQuery2("abc", 10, replyWindow: 0x1234, replyMessage: 0x5A5A0001);

        Assert.Equal(EverythingProtocol.Query2HeaderSize + ((3 + 1) * sizeof(char)), buffer.Length);
        Assert.Equal(0x1234u, BitConverter.ToUInt32(buffer, 0));
        // The reply id is caller-chosen and echoed back in the reply's dwData.
        Assert.Equal(0x5A5A0001u, BitConverter.ToUInt32(buffer, 4));
        // Match-path must be off unless the caller opts in (default semantics).
        Assert.Equal(0u, BitConverter.ToUInt32(buffer, 8));
        Assert.Equal(0u, BitConverter.ToUInt32(buffer, 12));
        Assert.Equal(10u, BitConverter.ToUInt32(buffer, 16));
        Assert.Equal(EverythingProtocol.RequestFlagFullPathAndName, BitConverter.ToUInt32(buffer, 20));
        Assert.Equal((uint)EverythingProtocol.SortNameAscending, BitConverter.ToUInt32(buffer, 24));

        Assert.Equal("abc", Encoding.Unicode.GetString(buffer, EverythingProtocol.Query2HeaderSize, 6));
        Assert.Equal(0, buffer[^2]);
        Assert.Equal(0, buffer[^1]);
    }

    [Fact]
    public void BuildQuery2_writes_explicit_search_flags_into_the_header()
    {
        const uint flags = EverythingProtocol.SearchFlagMatchCase | EverythingProtocol.SearchFlagRegex;

        var buffer = EverythingProtocol.BuildQuery2("a.*", 5, 0, 0, searchFlags: flags);

        Assert.Equal(flags, BitConverter.ToUInt32(buffer, 8));
    }

    [Theory]
    [InlineData(false, false, false, false, 0u)]
    [InlineData(true, false, false, false, EverythingProtocol.SearchFlagMatchCase)]
    [InlineData(false, true, false, false, EverythingProtocol.SearchFlagMatchWholeWord)]
    [InlineData(false, false, true, false, EverythingProtocol.SearchFlagMatchPath)]
    [InlineData(false, false, false, true, EverythingProtocol.SearchFlagRegex)]
    [InlineData(true, true, true, false,
        EverythingProtocol.SearchFlagMatchCase | EverythingProtocol.SearchFlagMatchWholeWord | EverythingProtocol.SearchFlagMatchPath)]
    public void ToSearchFlags_maps_each_quick_toggle_to_its_everything_flag(
        bool matchCase,
        bool matchWholeWord,
        bool matchPath,
        bool regex,
        uint expected
    )
    {
        var options = new FileSearchOptions
        {
            MatchCase = matchCase,
            MatchWholeWord = matchWholeWord,
            MatchPath = matchPath,
            Regex = regex,
        };

        Assert.Equal(expected, EverythingProtocol.ToSearchFlags(options));
    }

    [Fact]
    public void ToSearchFlags_drops_whole_word_when_regex_is_enabled()
    {
        // Everything ignores MATCHWHOLEWORD for regex queries; the wire flags stay honest.
        var options = new FileSearchOptions { MatchWholeWord = true, Regex = true };

        var flags = EverythingProtocol.ToSearchFlags(options);

        Assert.Equal(EverythingProtocol.SearchFlagRegex, flags);
    }

    [Fact]
    public void BuildQuery2_clamps_negative_result_counts()
    {
        var buffer = EverythingProtocol.BuildQuery2("x", -5, 0, 0);
        Assert.Equal(0u, BitConverter.ToUInt32(buffer, 16));
    }

    [Fact]
    public void ParseList2_reads_full_paths_and_folder_flags()
    {
        var payload = BuildList2(
            [
                (EverythingProtocol.ItemFlagFolder, @"C:\Users\test\Projects"),
                (0u, @"C:\Users\test\notes.txt"),
            ]
        );

        var items = EverythingProtocol.ParseList2(payload);

        Assert.Equal(2, items.Count);
        Assert.Equal(@"C:\Users\test\Projects", items[0].FullPath);
        Assert.True(items[0].IsFolder);
        Assert.Equal(@"C:\Users\test\notes.txt", items[1].FullPath);
        Assert.False(items[1].IsFolder);
    }

    [Fact]
    public void ParseList2_rejects_replies_without_the_requested_field()
    {
        var payload = BuildList2([(0u, @"C:\file.txt")], requestFlags: 0x00000002);
        Assert.Empty(EverythingProtocol.ParseList2(payload));
    }

    [Fact]
    public void ParseList2_tolerates_truncated_and_empty_buffers()
    {
        Assert.Empty(EverythingProtocol.ParseList2(Array.Empty<byte>()));
        Assert.Empty(EverythingProtocol.ParseList2(new byte[10]));
        Assert.Empty(EverythingProtocol.ParseList2(BuildList2([(0u, @"C:\file.txt")])[..30]));
    }

    [Fact]
    public void ParseList2_rejects_a_data_offset_that_would_overflow_when_four_is_added()
    {
        // dataOffset + 4 wraps to a small value, so a naive bounds check would pass and then slice
        // with a negative index.
        var payload = BuildList2([(0u, @"C:\file.txt")]);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(payload, EverythingProtocol.List2HeaderSize + 4);

        Assert.Empty(EverythingProtocol.ParseList2(payload));
    }

    [Fact]
    public void ParseList2_rejects_a_data_offset_inside_the_header()
    {
        var payload = BuildList2([(0u, @"C:\file.txt")]);
        BitConverter.GetBytes(4u).CopyTo(payload, EverythingProtocol.List2HeaderSize + 4);

        Assert.Empty(EverythingProtocol.ParseList2(payload));
    }

    [Fact]
    public void ParseList2_rejects_a_character_count_that_overflows_the_remaining_bytes()
    {
        // length is in UTF-16 units; (int)length * 2 overflows and textOffset + byteLength wraps, so
        // the slice would be negative.
        var payload = BuildList2([(0u, @"C:\file.txt")]);
        var dataOffset = (int)BitConverter.ToUInt32(payload, EverythingProtocol.List2HeaderSize + 4);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(payload, dataOffset);

        Assert.Empty(EverythingProtocol.ParseList2(payload));
    }

    [Fact]
    public void ParseList2_clamps_an_absurd_item_count_instead_of_overrunning_the_buffer()
    {
        var payload = BuildList2([(0u, @"C:\file.txt")]);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(payload, 4); // numitems

        // No exception, and the real item is still readable.
        var items = EverythingProtocol.ParseList2(payload);

        Assert.Contains(items, item => item.FullPath == @"C:\file.txt");
    }

    [Fact]
    public void ParseList2_rejects_repeated_data_offsets_to_stop_decode_amplification()
    {
        // A hostile reply can point every item at one large in-bounds string, so a naive parser would
        // decode the same maximum-length path MaxReplyItems times. Only the first use of an offset is
        // kept, so the whole reply collapses to one decode instead of an amplified one.
        var longPath = new string('a', EverythingProtocol.MaxPathChars);
        var items = Enumerable
            .Range(0, EverythingProtocol.MaxReplyItems)
            .Select(_ => (0u, longPath))
            .ToArray();
        var payload = BuildList2(items);

        // Point every item record at the first item's data offset.
        var firstOffset = BitConverter.ToUInt32(payload, EverythingProtocol.List2HeaderSize + 4);
        for (var index = 0; index < items.Length; index++)
        {
            var offsetField = EverythingProtocol.List2HeaderSize + (index * EverythingProtocol.Item2Size) + 4;
            BitConverter.GetBytes(firstOffset).CopyTo(payload, offsetField);
        }

        var decoded = EverythingProtocol.ParseList2(payload);

        var single = Assert.Single(decoded);
        Assert.Equal(longPath, single.FullPath);
    }

    [Theory]
    [InlineData(EverythingProtocol.MaxPathChars, true)]
    [InlineData(EverythingProtocol.MaxPathChars + 1, false)]
    public void ParseList2_bounds_the_decoded_path_length(int pathLength, bool accepted)
    {
        var path = new string('p', pathLength);
        var payload = BuildList2([(0u, path)]);

        var decoded = EverythingProtocol.ParseList2(payload);

        if (accepted)
        {
            var single = Assert.Single(decoded);
            Assert.Equal(path, single.FullPath);
        }
        else
        {
            Assert.Empty(decoded);
        }
    }

    [Fact]
    public void ParseList2_bounds_an_excessive_item_count_to_the_reply_budget()
    {
        // Many real entries plus a claimed count far above them: only the reply budget is decoded.
        var items = Enumerable
            .Range(0, EverythingProtocol.MaxReplyItems * 4)
            .Select(index => (0u, $@"C:\file{index}.txt"))
            .ToArray();
        var payload = BuildList2(items);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(payload, 4); // numitems

        var decoded = EverythingProtocol.ParseList2(payload);

        Assert.Equal(EverythingProtocol.MaxReplyItems, decoded.Count);
        Assert.Equal(@"C:\file0.txt", decoded[0].FullPath);
    }

    [Fact]
    public void ParseList2_total_byte_budget_covers_the_item_cap_without_allowing_amplification()
    {
        // The cumulative budget must admit every item the count cap allows at the longest legal path;
        // otherwise a legitimate full reply is silently truncated. The old 4 MiB budget failed this
        // for 120 x 32767-char paths, and this invariant pins the new contract.
        Assert.True(
            EverythingProtocol.MaxDecodedBytes
                >= EverythingProtocol.MaxReplyItems * EverythingProtocol.MaxPathChars * sizeof(char)
        );

        // Distinct offsets, each pointing at a distinct maximum-length path. A per-offset check alone
        // would materialize MaxReplyItems * MaxPathChars * 2 bytes; the item cap plus the cumulative
        // budget keep the total bounded there even when the reply claims a huge count.
        var items = Enumerable
            .Range(0, EverythingProtocol.MaxReplyItems)
            .Select(index => (0u, MaxLengthPath(index)))
            .ToArray();
        var payload = BuildList2(items);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(payload, 4); // numitems

        var decoded = EverythingProtocol.ParseList2(payload);

        Assert.Equal(EverythingProtocol.MaxReplyItems, decoded.Count);
        Assert.All(decoded, item => Assert.Equal(EverythingProtocol.MaxPathChars, item.FullPath.Length));
        Assert.True(
            decoded.Sum(item => item.FullPath.Length) * sizeof(char) <= EverythingProtocol.MaxDecodedBytes
        );
    }

    [Fact]
    public void ParseList2_retains_120_distinct_maximum_length_paths()
    {
        // Regression: a genuine full reply of 120 maximum-length Windows paths is ~7.5 MiB of UTF-16
        // text, which the old 4 MiB decoded-byte budget truncated even though every path was legal.
        const int requested = 120;
        var items = Enumerable
            .Range(0, requested)
            .Select(index => (0u, MaxLengthPath(index)))
            .ToArray();
        var payload = BuildList2(items);

        Assert.True(payload.Length < EverythingProtocol.MaxReplyBytes);

        var decoded = EverythingProtocol.ParseList2(payload);

        Assert.Equal(requested, decoded.Count);
        Assert.Equal(requested, decoded.Select(item => item.FullPath).Distinct().Count());
        for (var index = 0; index < requested; index++)
        {
            Assert.Equal(items[index].Item2, decoded[index].FullPath);
            Assert.Equal(EverythingProtocol.MaxPathChars, decoded[index].FullPath.Length);
        }
    }

    [Fact]
    public void ParseList2_reads_a_full_legitimate_reply_at_the_requested_maximum()
    {
        // EverythingFileSearchProvider.MaxHits: a normal 1.4.1 QUERY2 reply at the requested maximum
        // must round-trip untouched, long paths and folder flags included.
        const int requested = 120;
        var items = Enumerable
            .Range(0, requested)
            .Select(index => ((uint)(index % 2), $@"C:\Users\test\Projects\subfolder\file{index}.txt"))
            .ToArray();
        var payload = BuildList2(items);

        var decoded = EverythingProtocol.ParseList2(payload);

        Assert.Equal(requested, decoded.Count);
        for (var index = 0; index < requested; index++)
        {
            Assert.Equal(items[index].Item2, decoded[index].FullPath);
            Assert.Equal((index % 2) == 1, decoded[index].IsFolder);
        }
    }

    [Theory]
    [InlineData(int.MinValue, false)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(EverythingProtocol.MaxReplyBytes, true)]
    [InlineData(EverythingProtocol.MaxReplyBytes + 1, false)]
    [InlineData(int.MaxValue, false)]
    public void IsAcceptableReplySize_bounds_the_copy_before_it_happens(int cbData, bool expected)
    {
        Assert.Equal(expected, EverythingProtocol.IsAcceptableReplySize(cbData));
    }

    [Theory]
    [InlineData(1, 4, 1, true)]
    [InlineData(1, 4, 7, true)]
    [InlineData(1, 5, 0, true)]
    [InlineData(2, 0, 0, true)]
    [InlineData(1, 4, 0, false)]
    [InlineData(1, 3, 9, false)]
    [InlineData(0, 0, 0, false)]
    public void SupportsQuery2_requires_everything_1_4_1_or_newer(uint major, uint minor, uint revision, bool expected)
    {
        Assert.Equal(expected, EverythingProtocol.SupportsQuery2(major, minor, revision));
    }

    /// <summary>Builds a distinct, maximum-length Windows path for the given index.</summary>
    private static string MaxLengthPath(int index)
    {
        var prefix = $@"C:\f{index}\xi";
        return prefix + new string('x', EverythingProtocol.MaxPathChars - prefix.Length);
    }

    /// <summary>Builds a synthetic EVERYTHING_IPC_LIST2 payload with FULL_PATH_AND_NAME data.</summary>
    private static byte[] BuildList2(
        IReadOnlyList<(uint Flags, string FullPath)> items,
        uint requestFlags = EverythingProtocol.RequestFlagFullPathAndName
    )
    {
        var itemTableSize = items.Count * EverythingProtocol.Item2Size;
        var dataOffset = EverythingProtocol.List2HeaderSize + itemTableSize;

        var data = new List<byte>();
        var offsets = new List<int>();
        foreach (var (_, fullPath) in items)
        {
            offsets.Add(dataOffset + data.Count);
            var text = Encoding.Unicode.GetBytes(fullPath);
            data.AddRange(BitConverter.GetBytes((uint)fullPath.Length));
            data.AddRange(text);
            data.AddRange([0, 0]);
        }

        var buffer = new List<byte>();
        buffer.AddRange(BitConverter.GetBytes((uint)items.Count)); // totitems
        buffer.AddRange(BitConverter.GetBytes((uint)items.Count)); // numitems
        buffer.AddRange(BitConverter.GetBytes(0u)); // offset
        buffer.AddRange(BitConverter.GetBytes(requestFlags));
        buffer.AddRange(BitConverter.GetBytes((uint)EverythingProtocol.SortNameAscending));

        for (var index = 0; index < items.Count; index++)
        {
            buffer.AddRange(BitConverter.GetBytes(items[index].Flags));
            buffer.AddRange(BitConverter.GetBytes((uint)offsets[index]));
        }

        buffer.AddRange(data);
        return buffer.ToArray();
    }
}
