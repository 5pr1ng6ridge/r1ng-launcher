using System.IO;
using R1ng.Launcher.Apps;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Tests;

/// <summary>
/// Internet Shortcut (.url) discovery: the launcher must pick up Steam-style game shortcuts, keep the
/// .url file as the shell-safe launch target and use its declared IconFile for the real icon.
/// </summary>
public class ShortcutScannerUrlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "r1ng-url-tests-" + Guid.NewGuid().ToString("N"));

    public ShortcutScannerUrlTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the test sandbox.
        }
    }

    [Fact]
    public void Url_shortcuts_are_scanned_with_their_declared_icon()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        Assert.True(File.Exists(notepad), "the test needs a real local file for IconFile");

        var url = Path.Combine(_root, "Celeste.url");
        File.WriteAllText(
            url,
            $"[InternetShortcut]\r\nURL=steam://rungameid/504230\r\nIconFile={notepad}\r\nIconIndex=0\r\n"
        );
        File.WriteAllText(Path.Combine(_root, "Notepad.lnk"), "stub shortcut");

        var entries = ShortcutScanner.Scan([new ShortcutRoot(_root, AppSources.StartMenu)]);

        Assert.Equal(new[] { "Celeste", "Notepad" }, entries.Select(entry => entry.DisplayName));

        var celeste = entries.Single(entry => entry.DisplayName == "Celeste");
        // The .url file itself is the launch target, exactly like a .lnk; the URL is never parsed.
        Assert.Equal(url, celeste.Target);
        Assert.Equal(notepad, celeste.IconPath);
        Assert.Equal(0, celeste.IconIndex);
    }

    [Fact]
    public void A_url_without_an_icon_keeps_the_shortcut_as_the_icon_source()
    {
        var url = Path.Combine(_root, "Plain.url");
        File.WriteAllText(url, "[InternetShortcut]\r\nURL=https://example.com\r\n");

        var entries = ShortcutScanner.Scan([new ShortcutRoot(_root, AppSources.StartMenu)]);

        var plain = Assert.Single(entries);
        Assert.Equal(url, plain.Target);
        Assert.Equal(url, plain.IconPath);
        Assert.Equal(-1, plain.IconIndex);
    }

    [Fact]
    public void Malformed_or_empty_icon_declarations_fall_back_to_the_shortcut_itself()
    {
        var empty = Path.Combine(_root, "Empty.url");
        File.WriteAllText(empty, "[InternetShortcut]\r\nURL=steam://rungameid/1\r\nIconFile=\r\nIconIndex=abc\r\n");

        var missing = Path.Combine(_root, "Missing.url");
        File.WriteAllText(missing, $"[InternetShortcut]\r\nIconFile={Path.Combine(_root, "nope.ico")}\r\n");

        var entries = ShortcutScanner.Scan([new ShortcutRoot(_root, AppSources.StartMenu)]);

        foreach (var entry in entries)
        {
            Assert.Equal(entry.Target, entry.IconPath);
            Assert.Equal(-1, entry.IconIndex);
        }
    }

    [Fact]
    public void Garbage_content_is_read_without_throwing()
    {
        var garbage = Path.Combine(_root, "Garbage.url");
        File.WriteAllBytes(garbage, [0x00, 0x01, 0xFF, 0xFE, 0x7F, 0x0A, 0x0D]);

        var icon = UrlShortcut.ReadIcon(garbage);

        Assert.Null(icon.File);
        Assert.Equal(-1, icon.Index);
    }

    [Fact]
    public void An_empty_url_file_returns_no_icon()
    {
        var empty = Path.Combine(_root, "EmptyFile.url");
        File.WriteAllText(empty, string.Empty);

        var icon = UrlShortcut.ReadIcon(empty);

        Assert.Null(icon.File);
        Assert.Equal(-1, icon.Index);
    }

    [Fact]
    public void An_oversized_single_line_is_rejected_without_being_materialized()
    {
        // One line far past the read cap. The old File.ReadLines implementation materialized the
        // whole line before the length check; the bounded reader must reject it incrementally.
        var huge = Path.Combine(_root, "Huge.url");
        File.WriteAllText(huge, "IconFile=" + new string('A', UrlShortcut.MaxReadChars * 2));

        var icon = UrlShortcut.ReadIcon(huge);

        Assert.Null(icon.File);
        Assert.Equal(-1, icon.Index);
    }

    [Fact]
    public void A_url_at_the_read_cap_still_resolves_its_declared_icon()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        Assert.True(File.Exists(notepad), "the test needs a real local file for IconFile");

        var url = Path.Combine(_root, "Capped.url");
        var padding = new string('P', UrlShortcut.MaxReadChars - 256);
        File.WriteAllText(url, $"Padding={padding}\r\nIconFile={notepad}\r\nIconIndex=3\r\n");

        var icon = UrlShortcut.ReadIcon(url);

        Assert.Equal(notepad, icon.File);
        Assert.Equal(3, icon.Index);
    }

    [Fact]
    public void Iconfile_with_an_environment_variable_resolves_locally()
    {
        var url = Path.Combine(_root, "Env.url");
        File.WriteAllText(url, "[InternetShortcut]\r\nURL=steam://rungameid/1\r\nIconFile=%SystemRoot%\\System32\\shell32.dll\r\nIconIndex=2\r\n");

        var entries = ShortcutScanner.Scan([new ShortcutRoot(_root, AppSources.StartMenu)]);

        var env = Assert.Single(entries);
        Assert.Equal(2, env.IconIndex);
        Assert.NotNull(env.IconPath);
        Assert.EndsWith("shell32.dll", env.IconPath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unreachable_or_unresolved_icons_are_rejected()
    {
        Assert.Null(UrlShortcut.ResolveLocalIconFile(@"\\server\share\icon.ico"));
        Assert.Null(UrlShortcut.ResolveLocalIconFile("relative\\icon.ico"));
        Assert.Null(UrlShortcut.ResolveLocalIconFile("%NOT_A_REAL_VARIABLE%\\icon.ico"));
        Assert.Null(UrlShortcut.ResolveLocalIconFile(null));
    }
}
