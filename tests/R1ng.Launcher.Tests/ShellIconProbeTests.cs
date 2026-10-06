using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using R1ng.Launcher.Apps;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;

namespace R1ng.Launcher.Tests;

/// <summary>
/// Bounded probes against the real shell: a system executable, an Internet Shortcut with a declared
/// IconFile, the installed Steam-style .url games and the installed packaged apps (for example
/// ChatGPT) must yield real, per-target icons rather than one generic placeholder. Every probe is
/// local and short-circuits when the sample does not exist on this host, so the suite stays green on
/// machines without those apps.
/// </summary>
public class ShellIconProbeTests
{
    public ShellIconProbeTests() => Directory.CreateDirectory(Root);

    private static string Root { get; } = Path.Combine(Path.GetTempPath(), "r1ng-icon-probe");

    [Fact]
    public void A_system_executable_reports_a_real_icon()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(notepad))
            return;

        var icon = ShellIconLoader.Load(notepad);

        var bitmap = Assert.IsAssignableFrom<BitmapSource>(icon);
        Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0);
    }

    [Fact]
    public void A_url_reports_the_declared_iconfile_icon_not_the_generic_internet_shortcut_icon()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(notepad))
            return;

        var sandbox = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);

        var withIcon = Path.Combine(sandbox, "game.url");
        File.WriteAllText(
            withIcon,
            $"[InternetShortcut]\r\nURL=steam://rungameid/504230\r\nIconFile={notepad}\r\nIconIndex=0\r\n"
        );
        var plain = Path.Combine(sandbox, "plain.url");
        File.WriteAllText(plain, "[InternetShortcut]\r\nURL=https://example.com\r\n");

        var entries = ShortcutScanner.Scan([new ShortcutRoot(sandbox, AppSources.StartMenu)]);
        var game = entries.Single(entry => entry.DisplayName == "game");

        Assert.Equal(notepad, game.IconPath, StringComparer.OrdinalIgnoreCase);

        var resolved = ShellIconLoader.Load(game.IconPath, false, game.IconIndex);
        // Same extraction path (index 0 of the declared IconFile) must be pixel-identical.
        var declared = ShellIconLoader.Load(notepad, false, 0);
        Assert.NotNull(resolved);
        Assert.NotNull(declared);
        Assert.True(PixelsEqual(resolved!, declared!), "the .url icon must be the declared IconFile icon");

        var generic = ShellIconLoader.Load(plain);
        if (generic is not null)
        {
            Assert.False(
                PixelsEqual(resolved!, generic),
                "the .url icon must not be the generic Internet Shortcut icon"
            );
        }
    }

    [Fact]
    public void A_legacy_url_history_target_recovers_its_local_declared_icon_without_catalog_metadata()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(notepad))
            return;
        var sandbox = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var shortcut = Path.Combine(sandbox, "legacy.url");
            File.WriteAllText(shortcut, $"[InternetShortcut]\r\nURL=https://example.invalid/\r\nIconFile={notepad}\r\nIconIndex=0\r\n");
            var recovered = ShellIconLoader.Load(shortcut);
            var declared = ShellIconLoader.Load(notepad, false, 0);
            Assert.NotNull(recovered);
            Assert.NotNull(declared);
            Assert.True(PixelsEqual(recovered!, declared!));
        }
        finally
        {
            Directory.Delete(sandbox, true);
        }
    }

    [Fact]
    public void Installed_packaged_apps_resolve_their_own_distinct_icons()
    {
        var entries = AppsFolderEnumerator.Enumerate();
        var samples = entries
            .Where(entry => entry.Target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();

        if (samples.Count == 0)
            return;

        var icons = samples
            .Select(entry => ShellIconLoader.Load(entry.Target, false, entry.IconIndex))
            .Where(icon => icon is not null)
            .Cast<ImageSource>()
            .ToList();

        // Every packaged app entry with an AUMID must resolve through SHParseDisplayName, not fall
        // back to a generic/missing icon.
        Assert.Equal(samples.Count, icons.Count);

        if (icons.Count >= 2)
        {
            var distinct = icons
                .Select(Pixels)
                .Select(bytes => Convert.ToBase64String(bytes))
                .Distinct(StringComparer.Ordinal)
                .Count();

            Assert.True(distinct >= 2, "packaged AppUserModelId apps should not all share one generic icon");
        }
    }

    [Fact]
    public void An_installed_chatgpt_app_and_steam_url_games_load_real_icons_when_present()
    {
        // Packaged ChatGPT (if installed through the Store/Microsoft Store packaging).
        var appsFolder = AppsFolderEnumerator.Enumerate();
        var chatGpt = appsFolder.FirstOrDefault(entry =>
            entry.DisplayName.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase)
        );
        if (chatGpt is not null)
        {
            var icon = ShellIconLoader.Load(chatGpt.IconPath ?? chatGpt.Target, false, chatGpt.IconIndex);
            Assert.NotNull(icon);
        }

        // Steam-style Internet Shortcuts in the Start Menu that declare a real local IconFile.
        var roots = AppFolders.GetRoots(Environment.GetFolderPath);
        var urlGames = ShortcutScanner
            .Scan(roots)
            .Where(entry =>
                entry.Target.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
                && entry.IconPath is not null
                && !string.Equals(entry.IconPath, entry.Target, StringComparison.OrdinalIgnoreCase)
            )
            .Take(5)
            .ToList();

        foreach (var game in urlGames)
        {
            var icon = ShellIconLoader.Load(game.IconPath, false, game.IconIndex);
            Assert.NotNull(icon);
        }
    }

    [Fact]
    public void A_url_with_a_rejected_remote_icon_resolves_the_generic_icon_without_reading_the_remote_path()
    {
        var sandbox = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);

        var hostile = Path.Combine(sandbox, "hostile.url");
        File.WriteAllText(
            hostile,
            "[InternetShortcut]\r\nURL=steam://rungameid/1\r\nIconFile=\\\\server\\share\\icon.ico\r\nIconIndex=0\r\n"
        );

        var entries = ShortcutScanner.Scan([new ShortcutRoot(sandbox, AppSources.StartMenu)]);
        var entry = Assert.Single(entries);

        // The remote IconFile is rejected, so the shortcut itself remains the icon source...
        Assert.Equal(hostile, entry.IconPath);
        Assert.Equal(-1, entry.IconIndex);

        // ...and loading it uses the generic .url extension icon (SHGFI_USEFILEATTRIBUTES) instead of
        // re-reading the declared remote path.
        var icon = ShellIconLoader.Load(entry.IconPath, false, entry.IconIndex);
        Assert.NotNull(icon);
    }

    private static byte[] Pixels(ImageSource source)
    {
        var bitmap = new FormatConvertedBitmap((BitmapSource)source, PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4;
        var buffer = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    private static bool PixelsEqual(ImageSource first, ImageSource second)
    {
        var firstBitmap = (BitmapSource)first;
        var secondBitmap = (BitmapSource)second;

        return firstBitmap.PixelWidth == secondBitmap.PixelWidth
            && firstBitmap.PixelHeight == secondBitmap.PixelHeight
            && Pixels(first).SequenceEqual(Pixels(second));
    }
}
