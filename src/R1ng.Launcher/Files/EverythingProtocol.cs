using System.Buffers.Binary;
using System.Text;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Files;

/// <summary>One raw item of an Everything QUERY2 reply.</summary>
public readonly record struct EverythingRawItem(string FullPath, uint Flags)
{
    public bool IsFolder => (Flags & EverythingProtocol.ItemFlagFolder) != 0;
}

/// <summary>
/// Hand-rolled implementation of Everything's documented IPC wire format (Everything_IPC.h, MIT).
/// Only the pieces the launcher needs are implemented: the QUERY2 request and its LIST2 reply.
/// <para>
/// The structures are byte-packed (1-byte alignment) and variable length, so they are built and
/// parsed manually instead of being mapped to managed structs. Keeping this pure makes the wire
/// format unit-testable without a running Everything instance.
/// </para>
/// </summary>
public static class EverythingProtocol
{
    /// <summary>Window class of the Everything notification window (EVERYTHING_IPC_WNDCLASSW).</summary>
    public const string NotificationWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";

    /// <summary>WM_USER: the message id base used by the Everything IPC messages.</summary>
    public const uint WmIpc = 0x0400;

    public const uint GetMajorVersion = 0;
    public const uint GetMinorVersion = 1;
    public const uint GetRevision = 2;
    public const uint GetBuildNumber = 3;
    public const uint IsDbLoaded = 401;

    /// <summary>COPYDATASTRUCT.dwData for a Unicode QUERY2 request (EVERYTHING_IPC_COPYDATA_QUERY2W).</summary>
    public const uint CopyDataQuery2W = 18;

    /// <summary>search_flags: match case (EVERYTHING_IPC_MATCHCASE).</summary>
    public const uint SearchFlagMatchCase = 0x00000001;

    /// <summary>search_flags: match whole word (EVERYTHING_IPC_MATCHWHOLEWORD).</summary>
    public const uint SearchFlagMatchWholeWord = 0x00000002;

    /// <summary>search_flags: include the path in the query (EVERYTHING_IPC_MATCHPATH).</summary>
    public const uint SearchFlagMatchPath = 0x00000004;

    /// <summary>search_flags: treat the query as a regular expression (EVERYTHING_IPC_REGEX).</summary>
    public const uint SearchFlagRegex = 0x00000008;

    /// <summary>request_flags: return the full path and name (EVERYTHING_IPC_QUERY2_REQUEST_FULL_PATH_AND_NAME).</summary>
    public const uint RequestFlagFullPathAndName = 0x00000004;

    /// <summary>EVERYTHING_IPC_SORT_NAME_ASCENDING - always indexed, so never a slow sort.</summary>
    public const int SortNameAscending = 1;

    /// <summary>
    /// Upper bound for an accepted LIST2 reply. The launcher asks for at most a few hundred long
    /// paths, so a much larger <c>cbData</c> is a malformed or hostile payload and is rejected before
    /// it is copied out of the sender's buffer.
    /// </summary>
    public const int MaxReplyBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Highest item count accepted from a LIST2 reply. The launcher asks Everything for at most
    /// <c>EverythingFileSearchProvider.MaxHits</c> (120) results, so a reply that claims more than
    /// that is malformed; the small slack tolerates only an off-by-one in Everything's own limit
    /// handling. Capping the count stops a hostile reply from turning one in-bounds data region into
    /// millions of decoded strings.
    /// </summary>
    public const int MaxReplyItems = 120 + 8;

    /// <summary>
    /// Longest single path accepted from a LIST2 reply, in UTF-16 code units. Windows extended-length
    /// paths top out at 32767 units, so a larger length field is malformed.
    /// </summary>
    public const int MaxPathChars = 32767;

    /// <summary>
    /// Total UTF-16 bytes the parser is willing to materialize from one reply. A legitimate reply can
    /// hold <see cref="MaxReplyItems"/> distinct paths of <see cref="MaxPathChars"/> UTF-16 units each
    /// (128 * 32767 * 2 = 8,388,352 bytes), so the budget must cover at least that; 8 MiB does, with a
    /// little slack. It remains the hard ceiling that bounds a single reply to
    /// O(<see cref="MaxReplyItems"/> * <see cref="MaxPathChars"/>) bytes even when every item points at
    /// a distinct, individually-valid maximum-length offset. The cumulative sum stays in
    /// <see cref="int"/>: <see cref="MaxDecodedBytes"/> is well below <see cref="int.MaxValue"/> and
    /// each item adds at most <see cref="MaxPathChars"/> * 2 bytes.
    /// </summary>
    public const int MaxDecodedBytes = 8 * 1024 * 1024;

    /// <summary>True when a reply's <c>cbData</c> is a plausible LIST2 size.</summary>
    public static bool IsAcceptableReplySize(int cbData) => cbData > 0 && cbData <= MaxReplyBytes;

    internal const int Query2HeaderSize = 28;
    internal const int List2HeaderSize = 20;
    internal const int Item2Size = 8;

    /// <summary>Item flag: the entry is a folder (EVERYTHING_IPC_FOLDER).</summary>
    public const uint ItemFlagFolder = 0x00000001;

    /// <summary>Item flag: the entry is a drive/root (EVERYTHING_IPC_DRIVE).</summary>
    public const uint ItemFlagDrive = 0x00000002;

    /// <summary>Builds the packed EVERYTHING_IPC_QUERY2W payload for a WM_COPYDATA request.</summary>
    public static byte[] BuildQuery2(
        string search,
        int maxResults,
        uint replyWindow,
        uint replyMessage,
        uint requestFlags = RequestFlagFullPathAndName,
        int sort = SortNameAscending,
        uint searchFlags = 0
    )
    {
        ArgumentNullException.ThrowIfNull(search);

        var searchLength = search.Length;
        var buffer = new byte[Query2HeaderSize + ((searchLength + 1) * sizeof(char))];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), replyWindow);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), replyMessage);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), searchFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16, 4), unchecked((uint)Math.Max(0, maxResults)));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20, 4), requestFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24, 4), unchecked((uint)sort));

        if (searchLength > 0)
            Encoding.Unicode.GetBytes(search, buffer.AsSpan(Query2HeaderSize));

        // The terminating NUL is already zero.
        return buffer;
    }

    /// <summary>
    /// Parses an EVERYTHING_IPC_LIST2 reply that was produced for
    /// <see cref="RequestFlagFullPathAndName"/>. Malformed buffers yield an empty list.
    /// </summary>
    public static IReadOnlyList<EverythingRawItem> ParseList2(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < List2HeaderSize)
            return Array.Empty<EverythingRawItem>();

        // totitems is only a hint; the loop is bounded by both numitems and the actual buffer length.
        var numItems = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4, 4));
        var requestFlags = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(12, 4));

        if ((requestFlags & RequestFlagFullPathAndName) == 0)
            return Array.Empty<EverythingRawItem>();

        // A reply can never hold more item records than the bytes that follow the header, and a
        // well-formed reply never exceeds the count the launcher asked for. Both caps use unsigned
        // arithmetic so no count can overflow the buffer check.
        var itemTableBytes = (uint)(buffer.Length - List2HeaderSize);
        var tableCount = itemTableBytes / Item2Size;
        var count = (int)Math.Min(Math.Min(numItems, tableCount), (uint)MaxReplyItems);
        var results = new List<EverythingRawItem>(count);

        // Each item normally points at its own string in the data block; a repeated data offset would
        // let a hostile reply decode one large in-bounds region over and over.
        var seenOffsets = new HashSet<uint>();
        long decodedBytes = 0;

        for (var index = 0; index < count; index++)
        {
            var itemOffset = List2HeaderSize + (index * Item2Size);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(itemOffset, 4));
            var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(itemOffset + 4, 4));

            // Subtraction-based bounds: `dataOffset + 4` and `textOffset + byteLength` can overflow
            // before the comparison, so every offset is validated against a safe upper bound and
            // every length against the number of bytes that actually remain.
            if (dataOffset < List2HeaderSize || dataOffset > (uint)(buffer.Length - 4))
                continue;

            var textOffset = (int)dataOffset + 4;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice((int)dataOffset, 4));
            var remainingBytes = (uint)(buffer.Length - textOffset);

            // Reject an empty length, a length longer than the longest real Windows path, or a length
            // longer than the bytes that actually remain.
            if (length == 0 || length > MaxPathChars || length > remainingBytes / sizeof(char))
                continue;

            if (!seenOffsets.Add(dataOffset))
                continue;

            var byteLength = (int)length * sizeof(char);

            // Cumulative budget: `decodedBytes` can never exceed MaxDecodedBytes and byteLength is
            // small, so this addition cannot overflow. Items that would break the budget are skipped
            // rather than allowing one reply to allocate without bound.
            if (decodedBytes + byteLength > MaxDecodedBytes)
                continue;

            var fullPath = Encoding.Unicode.GetString(buffer.Slice(textOffset, byteLength));
            if (fullPath.Length > 0)
            {
                decodedBytes += byteLength;
                results.Add(new EverythingRawItem(fullPath, flags));
            }
        }

        return results;
    }

    /// <summary>
    /// Translates the launcher's quick-toggle refinements into the packed Everything
    /// <c>search_flags</c> bit set. Everything ignores whole-word matching for regex queries, so the
    /// whole-word bit is dropped whenever <see cref="FileSearchOptions.Regex"/> is set.
    /// </summary>
    public static uint ToSearchFlags(FileSearchOptions options)
    {
        var flags = 0u;

        if (options.MatchCase)
            flags |= SearchFlagMatchCase;
        if (options.MatchWholeWord && !options.Regex)
            flags |= SearchFlagMatchWholeWord;
        if (options.MatchPath)
            flags |= SearchFlagMatchPath;
        if (options.Regex)
            flags |= SearchFlagRegex;

        return flags;
    }

    /// <summary>True when the reported version supports QUERY2 (Everything 1.4.1+).</summary>
    public static bool SupportsQuery2(uint major, uint minor, uint revision) =>
        major > 1 || (major == 1 && (minor > 4 || (minor == 4 && revision >= 1)));

    public static string DescribeError(uint sdkError) => sdkError switch
    {
        2 => "Everything IPC 不可用，请启动 Everything 或启用 Everything 服务",
        6 => "Everything 返回了无效的结果索引",
        7 => "Everything SDK 在上一次查询完成前被调用",
        _ => $"Everything 查询失败（错误 {sdkError}）",
    };
}
