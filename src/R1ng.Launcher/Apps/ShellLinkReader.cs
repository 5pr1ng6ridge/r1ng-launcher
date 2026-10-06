using System.Buffers.Binary;
using System.IO;
using System.Text;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Apps;

/// <summary>
/// Bounded, read-only MS-SHLLINK parsing. Only ordinary links with an unambiguous local LinkInfo
/// target are eligible for merging. No shell activation, Resolve, network lookup or environment
/// expansion is performed; unsupported links keep their original path identity.
/// </summary>
internal static class ShellLinkReader
{
    internal const int MaxBytes = 1024 * 1024;
    private static readonly byte[] ClassId = new Guid("00021401-0000-0000-C000-000000000046").ToByteArray();
    private static readonly Encoding Unicode = new UnicodeEncoding(false, false, true);

    public static string? ReadIdentity(string path)
    {
        try
        {
            if (!IsLocalAbsolute(path))
                return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 76 || stream.Length > MaxBytes)
                return null;
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return stream.ReadByte() == -1 ? ParseIdentity(bytes) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? ParseIdentity(byte[] bytes)
    {
        try
        {
            return new Parser(bytes).Parse();
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private sealed class Parser(byte[] data)
    {
        public string? Parse()
        {
            if (data.Length < 76 || data.Length > MaxBytes || UInt(0) != 76 || !data.AsSpan(4, 16).SequenceEqual(ClassId))
                return null;
            var flags = UInt(20);
            // Environment, Darwin, shim, link-to-link and unknown execution flags cannot be inferred.
            if ((flags & ~(0xffu | 0x2000u | 0x80000u)) != 0 || (flags & 2) == 0)
                return null;
            var show = UInt(60);
            if (show is not (1 or 3 or 7))
                return null;
            var position = 76;
            var resolutionMetadata = new List<string>();
            if ((flags & 1) != 0)
            {
                var idListSize = UShort(position);
                var start = position + 2;
                var limit = checked(start + idListSize);
                Bounds(start, idListSize);
                var cursor = start;
                while (cursor < limit)
                {
                    var itemSize = UShort(cursor);
                    if (itemSize == 0)
                    {
                        if (cursor + 2 != limit)
                            return null;
                        break;
                    }
                    if (itemSize < 2 || itemSize > limit - cursor)
                        return null;
                    cursor += itemSize;
                }
                if (idListSize < 2 || cursor >= limit)
                    return null;
                // An opaque PIDL can supersede LinkInfo. Only identical resolution metadata may
                // merge; never assume two different PIDLs describe the same executable.
                resolutionMetadata.Add(Convert.ToHexString(data.AsSpan(start, idListSize)));
                position = limit;
            }

            var size = Int(position);
            Bounds(position, size);
            var end = checked(position + size);
            var header = Int(position + 4);
            if (header is not (0x1c or 0x24) || size < header || UInt(position + 8) != 1)
                return null; // A CommonNetworkRelativeLink is not a local target.
            var volume = Offset(position, end, Int(position + 12), header);
            var volumeSize = Int(volume);
            if (volumeSize < 17 || volumeSize > end - volume || UInt(volume + 4) is not (2 or 3))
                return null;
            var labelOffset = Int(volume + 12);
            if (labelOffset == 0x14)
            {
                if (volumeSize < 24 || StringAt(volume, volume + volumeSize, Int(volume + 16), 20, unicode: true) is null)
                    return null;
            }
            else if (StringAt(volume, volume + volumeSize, labelOffset, 16, unicode: false) is null)
            {
                return null;
            }
            resolutionMetadata.Add(Convert.ToHexString(data.AsSpan(volume, volumeSize)));
            var baseOffset = Int(position + 16);
            var suffixOffset = Int(position + 24);
            var target = StringAt(position, end, baseOffset, header, unicode: false);
            var suffix = StringAt(position, end, suffixOffset, header, unicode: false);
            if (header >= 0x24)
            {
                var wideBase = Int(position + 28);
                var wideSuffix = Int(position + 32);
                if (wideBase != 0)
                    target = StringAt(position, end, wideBase, header, unicode: true);
                if (wideSuffix != 0)
                    suffix = StringAt(position, end, wideSuffix, header, unicode: true);
            }
            if (target is null || suffix is null || !IsLocalAbsolute(target))
                return null;
            if (suffix.Length > 0 && !target.EndsWith("\\" + suffix, StringComparison.OrdinalIgnoreCase))
            {
                if (Path.IsPathRooted(suffix) || suffix.Split('\\', '/').Any(part => part == ".."))
                    return null;
                target = Path.Combine(target, suffix);
            }
            position = end;
            var workingDirectory = string.Empty;
            var arguments = string.Empty;
            for (var bit = 4u; bit <= 0x40; bit <<= 1)
            {
                if ((flags & bit) == 0)
                    continue;
                var count = UShort(position);
                position += 2;
                var byteCount = checked(count * ((flags & 0x80) != 0 ? 2 : 1));
                Bounds(position, byteCount);
                var text = Decode(position, byteCount, (flags & 0x80) != 0);
                if (text is null || text.Contains('\0'))
                    return null;
                position += byteCount;
                if (bit == 8)
                    resolutionMetadata.Add("relative:" + text);
                if (bit == 0x10)
                    workingDirectory = text;
                if (bit == 0x20)
                    arguments = text;
            }
            if (workingDirectory.Length > 0 && !IsLocalAbsolute(workingDirectory))
                return null;

            // Tracking can redirect a moved target, so require identical tracker metadata as well.
            // Other ExtraData blocks (environment targets, property stores, etc.) remain unsupported.
            while (true)
            {
                var blockSize = Int(position);
                if (blockSize == 0)
                {
                    position += 4;
                    if (position != data.Length)
                        return null;
                    break;
                }
                Bounds(position, blockSize);
                if (blockSize != 0x60 || UInt(position + 4) != 0xA0000003 || UInt(position + 8) != 0x58 || UInt(position + 12) != 0)
                    return null;
                resolutionMetadata.Add(Convert.ToHexString(data.AsSpan(position, blockSize)));
                position += blockSize;
            }
            return LaunchIdentity.ForShortcut(target, arguments, workingDirectory, show, (flags & 0x2000) != 0, string.Join("|", resolutionMetadata));
        }

        private uint UInt(int position)
        {
            Bounds(position, 4);
            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, 4));
        }

        private int Int(int position) => checked((int)UInt(position));

        private ushort UShort(int position)
        {
            Bounds(position, 2);
            return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, 2));
        }

        private void Bounds(int position, int length)
        {
            if (position < 0 || length < 0 || position > data.Length - length)
                throw new InvalidDataException();
        }

        private static int Offset(int start, int end, int relative, int header)
        {
            if (relative < header || relative >= end - start)
                throw new InvalidDataException();
            return checked(start + relative);
        }

        private string? StringAt(int start, int end, int relative, int header, bool unicode)
        {
            var position = Offset(start, end, relative, header);
            var cursor = position;
            var stride = unicode ? 2 : 1;
            while (cursor <= end - stride)
            {
                if (data[cursor] == 0 && (!unicode || data[cursor + 1] == 0))
                    return Decode(position, cursor - position, unicode);
                cursor += stride;
            }
            throw new InvalidDataException();
        }

        private string? Decode(int position, int count, bool unicode)
        {
            // Without a reliable source code page, non-ASCII ANSI strings are ambiguous. Unicode
            // offsets/StringData support international names without guessing a legacy encoding.
            if (unicode)
                return Unicode.GetString(data, position, count);
            if (data.AsSpan(position, count).ContainsAnyInRange((byte)128, byte.MaxValue))
                return null;
            return Encoding.ASCII.GetString(data, position, count);
        }
    }

    internal static bool IsLocalAbsolute(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
        && !path.Contains('%') && Path.IsPathFullyQualified(path);
}
