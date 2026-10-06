using System.IO;
using System.Diagnostics;
using System.Windows;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Platform;

/// <summary>Opens results and exposes their secondary actions (open location / copy path).</summary>
public static class TargetLauncher
{
    /// <summary>Launches a result with the shell. Returns false when the target could not be started.</summary>
    public static bool Open(SearchResultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        try
        {
            if (IsAppsFolderTarget(item.Target))
            {
                var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                startInfo.ArgumentList.Add(item.Target);
                Process.Start(startInfo);
                return true;
            }

            if (string.IsNullOrWhiteSpace(item.Target))
                return false;

            Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Reveals the result in Explorer (selects files, opens folders).</summary>
    public static bool OpenContainingFolder(SearchResultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        try
        {
            if (item.IsFolder && !IsAppsFolderTarget(item.Target))
            {
                if (string.IsNullOrWhiteSpace(item.Target))
                    return false;

                Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true });
                return true;
            }

            var folder = item.FolderPath;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                if (string.IsNullOrWhiteSpace(folder))
                    return false;

                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                return true;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Target}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Copies the item's path (or shell target) to the clipboard.</summary>
    public static bool CopyPath(SearchResultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        try
        {
            Clipboard.SetText(item.CopyText);
            return true;
        }
        catch (Exception)
        {
            // The clipboard can be locked by another process; report the failure to the caller.
            return false;
        }
    }

    internal static bool IsAppsFolderTarget(string? target) =>
        !string.IsNullOrEmpty(target) && target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
}
