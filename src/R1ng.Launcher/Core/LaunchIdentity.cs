using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace R1ng.Launcher.Core;

/// <summary>Launch identity, separate from a row's display name and original shell launch target.</summary>
public static class LaunchIdentity
{
    public static string ForItem(SearchResultItem item) => item.IdentityKey
        ?? (string.IsNullOrWhiteSpace(item.Target) ? Digest("result", item.Id) : ForTarget(item.Target, item.IsFolder));

    public static string ForTarget(string target, bool isFolder = false)
    {
        // Only filesystem paths have Windows' case-insensitive semantics. URI paths, arguments and
        // opaque shell identifiers must not be lowercased along with them.
        if (Path.IsPathFullyQualified(target))
            return Digest(isFolder ? "folder" : "path", NormalizePath(target));
        return Digest("opaque", target);
    }

    public static string ForShortcut(string target, string arguments, string workingDirectory, uint showCommand, bool runAsAdmin, string resolutionMetadata = "") =>
        Digest("link", NormalizePath(target), arguments, NormalizePath(workingDirectory), showCommand.ToString(System.Globalization.CultureInfo.InvariantCulture), runAsAdmin ? "admin" : "normal", resolutionMetadata);

    public static string ForUrl(string url) => Digest("url", url);

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
        }
        catch (Exception)
        {
            return path;
        }
    }

    internal static string Digest(params string[] fields) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields))));
}
