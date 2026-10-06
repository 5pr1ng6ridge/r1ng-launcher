using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Apps;

/// <summary>
/// Enumerates <c>shell:AppsFolder</c> (packaged apps and shell-registered entries) through the
/// Shell automation object. Best effort by design: any COM failure yields an empty list so the
/// shortcut-based catalog stays usable.
/// </summary>
public static class AppsFolderEnumerator
{
    /// <summary>Enumerates on a dedicated STA thread. Never throws.</summary>
    public static IReadOnlyList<AppCatalogEntry> Enumerate(Action<string, Exception>? onError = null)
    {
        var results = new List<AppCatalogEntry>();
        var error = new List<Exception>();
        var thread = new Thread(() =>
        {
            try
            {
                results.AddRange(EnumerateOnStaThread(error));
            }
            catch (Exception exception)
            {
                error.Add(exception);
            }
        })
        {
            IsBackground = true,
            Name = "R1ng AppsFolder enumeration",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // AppsFolder enumeration is fast; this keeps the catalog load bounded.
        if (!thread.Join(TimeSpan.FromSeconds(5)))
            return new ReadOnlyCollection<AppCatalogEntry>(new List<AppCatalogEntry>());

        var handler = onError ?? ((_, _) => { });
        foreach (var exception in error)
            handler("shell:AppsFolder", exception);

        return new ReadOnlyCollection<AppCatalogEntry>(results);
    }

    private static List<AppCatalogEntry> EnumerateOnStaThread(List<Exception> errors)
    {
        var entries = new List<AppCatalogEntry>();
        object? shell = null;
        object? folder = null;
        object? items = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return entries;

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return entries;

            dynamic dynamicShell = shell;
            folder = dynamicShell.NameSpace("shell:AppsFolder");
            if (folder is null)
                return entries;

            dynamic dynamicFolder = folder;
            items = dynamicFolder.Items();
            if (items is null)
                return entries;

            dynamic dynamicItems = items;
            var count = (int)dynamicItems.Count;
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = dynamicItems.Item(index);
                    if (item is null)
                        continue;

                    dynamic dynamicItem = item;
                    string name = (string)dynamicItem.Name;
                    string appUserModelId = (string)dynamicItem.Path;

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appUserModelId))
                        continue;

                    entries.Add(new AppCatalogEntry
                    {
                        DisplayName = name.Trim(),
                        Target = BuildTarget(appUserModelId),
                        Source = AppSources.AppsFolder,
                        FolderPath = null,
                        IconPath = null,
                    });
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
                finally
                {
                    Release(item);
                }
            }
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        finally
        {
            Release(items);
            Release(folder);
            Release(shell);
        }

        return entries;
    }

    /// <summary>
    /// AppsFolder items expose either an already-qualified shell target, an absolute filesystem
    /// path, a URI (for example <c>steam://…</c>) or a raw AppUserModelId. Only a raw id is
    /// qualified with <c>shell:AppsFolder\</c>; everything else already launches correctly through
    /// the shell and must not be prefixed.
    /// </summary>
    internal static string BuildTarget(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
            return appUserModelId;

        var value = appUserModelId.Trim();

        if (value.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return value;

        // Virtual shell locations such as ::{GUID} are already shell paths.
        if (value.StartsWith("::", StringComparison.Ordinal))
            return value;

        if (IsUri(value))
            return value;

        if (IsAbsolutePath(value))
            return value;

        return $"shell:AppsFolder\\{value}";
    }

    /// <summary>True for scheme URIs (<c>steam://…</c>, <c>http://…</c>, <c>ms-settings:…</c>).</summary>
    internal static bool IsUri(string value)
    {
        var schemeEnd = value.IndexOf(':');
        if (schemeEnd <= 0)
            return false;

        // A drive letter (C:\…) is a path, not a one-letter URI scheme.
        if (schemeEnd == 1 && char.IsLetter(value[0]))
            return false;

        for (var index = 0; index < schemeEnd; index++)
        {
            var character = value[index];
            if (!char.IsLetterOrDigit(character) && character is not ('+' or '-' or '.'))
                return false;
        }

        return true;
    }

    internal static bool IsAbsolutePath(string value)
    {
        try
        {
            return Path.IsPathRooted(value);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
