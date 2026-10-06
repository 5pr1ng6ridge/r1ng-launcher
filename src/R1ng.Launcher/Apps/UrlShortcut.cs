using System.IO;
using System.Text;

namespace R1ng.Launcher.Apps;

/// <summary>
/// Bounded Internet Shortcut metadata reader. A validated URL is used only as an identity, never
/// visited or launched directly: the original .url remains the shell launch target. Icon sources
/// must be existing local absolute files; remote declarations never reach the shell icon handler.
/// </summary>
public static class UrlShortcut
{
    public const int MaxReadChars = 64 * 1024;

    public readonly record struct IconReference(string? File, int Index)
    {
        public static IconReference None { get; } = new(null, -1);
    }

    public readonly record struct Metadata(IconReference Icon, string? Url);

    public static IconReference ReadIcon(string path) => Read(path).Icon;

    public static Metadata Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Parse(reader);
        }
        catch (Exception)
        {
            return new Metadata(IconReference.None, null);
        }
    }

    private static Metadata Parse(TextReader reader)
    {
        string? iconFile = null;
        var iconIndex = 0;
        string? url = null;
        var inInternetSection = false;
        var sectionCount = 0;
        var urlCount = 0;
        var remaining = MaxReadChars;
        var buffer = new char[4096];
        var line = new StringBuilder();

        void ConsumeLine()
        {
            var trimmed = line.ToString().Trim();
            line.Clear();
            if (trimmed.StartsWith('['))
            {
                inInternetSection = trimmed.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                if (inInternetSection)
                    sectionCount++;
                return;
            }
            // Preserve icon compatibility with earlier, sectionless .url metadata.
            if (trimmed.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                iconFile = trimmed["IconFile=".Length..].Trim();
            else if (trimmed.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(trimmed["IconIndex=".Length..].Trim(), out var parsed))
                    iconIndex = Math.Max(0, parsed);
            }
            else if (inInternetSection && trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
            {
                urlCount++;
                url = trimmed["URL=".Length..].Trim();
            }
        }

        while (true)
        {
            var toRead = Math.Min(buffer.Length, remaining);
            if (toRead <= 0)
            {
                if (reader.Peek() != -1)
                    return new Metadata(IconReference.None, null);
                break;
            }
            var read = reader.Read(buffer, 0, toRead);
            if (read <= 0)
                break;
            remaining -= read;
            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character is '\r' or '\n')
                    ConsumeLine();
                else
                    line.Append(character);
            }
        }
        if (line.Length > 0)
            ConsumeLine();

        if (sectionCount != 1 || urlCount != 1 || string.IsNullOrEmpty(url)
            || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            url = null;
        var resolved = ResolveLocalIconFile(iconFile);
        var icon = resolved is null ? IconReference.None : new IconReference(resolved, iconIndex);
        return new Metadata(icon, url);
    }

    internal static string? ResolveLocalIconFile(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared))
            return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(declared.Trim());
            if (string.IsNullOrWhiteSpace(expanded) || expanded.Contains('%'))
                return null;
            if (expanded.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(expanded))
                return null;
            if (expanded.Contains("://", StringComparison.Ordinal))
                return null;
            var full = Path.GetFullPath(expanded);
            return File.Exists(full) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
