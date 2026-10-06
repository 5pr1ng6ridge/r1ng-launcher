using System.Buffers.Binary;
using System.IO;
using System.Text;
using R1ng.Launcher.Apps;
using R1ng.Launcher.Core;
using R1ng.Launcher.Search;

namespace R1ng.Launcher.Tests;

public sealed class ShortcutIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "r1ng-links-" + Guid.NewGuid().ToString("N"));
    public ShortcutIdentityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Plain_local_link_identity_is_case_insensitive_only_for_paths()
    {
        var first = ShellLinkReader.ParseIdentity(Link(@"C:\Apps\app.exe", "--profile Work", @"C:\Apps"));
        Assert.NotNull(first);
        Assert.Equal(first, ShellLinkReader.ParseIdentity(Link(@"c:\apps\APP.EXE", "--profile Work", @"c:\APPS")));
        Assert.NotEqual(first, ShellLinkReader.ParseIdentity(Link(@"C:\Apps\app.exe", "--profile work", @"C:\Apps")));
        Assert.NotEqual(first, ShellLinkReader.ParseIdentity(Link(@"C:\Apps\other.exe", "--profile Work", @"C:\Apps")));
        Assert.NotEqual(first, ShellLinkReader.ParseIdentity(Link(@"C:\Apps\app.exe", "--profile Work", @"C:\Other")));
        Assert.NotEqual(first, ShellLinkReader.ParseIdentity(Link(@"C:\Apps\app.exe", "--profile Work", @"C:\Apps", admin: true)));
        Assert.NotEqual(first, ShellLinkReader.ParseIdentity(Link(@"C:\Apps\app.exe", "--profile Work", @"C:\Apps", show: 3)));
    }

    [Theory]
    [InlineData(0x200u)] // expanded environment target
    [InlineData(0x1000u)] // advertised Darwin target
    [InlineData(0x20000u)] // shim execution
    [InlineData(0x400u)] // separate-process behavior
    public void Unknown_or_dynamic_link_execution_falls_back(uint extraFlags)
    {
        var bytes = Link(@"C:\Apps\app.exe", "", @"C:\Apps");
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), flags | extraFlags);
        Assert.Null(ShellLinkReader.ParseIdentity(bytes));
    }

    [Fact]
    public void Malformed_oversized_network_or_ambiguous_links_are_not_merged()
    {
        Assert.Null(ShellLinkReader.ParseIdentity(new byte[10]));
        Assert.Null(ShellLinkReader.ParseIdentity(new byte[ShellLinkReader.MaxBytes + 1]));
        Assert.Null(ShellLinkReader.ParseIdentity(Link(@"\\server\share\app.exe", "", @"C:\Apps")));
        Assert.Null(ShellLinkReader.ParseIdentity(Link(@"C:\Apps\app.exe", "", "relative")));
        var bytes = Link(@"C:\Apps\app.exe", "", @"C:\Apps");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76 + 16), uint.MaxValue);
        Assert.Null(ShellLinkReader.ParseIdentity(bytes));
        Assert.Null(ShellLinkReader.ParseIdentity(Link(@"C:\Apps\app.exe", "", @"C:\Apps")[..^1]));
        bytes = Link(@"C:\Apps\app.exe", "", @"C:\Apps");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76 + 8), 3); // LinkInfo network flag
        Assert.Null(ShellLinkReader.ParseIdentity(bytes));
    }

    [Fact]
    public async Task Equivalent_link_copies_merge_stably_keep_original_launch_target_and_searchable_aliases()
    {
        File.WriteAllBytes(Path.Combine(_root, "Original app.lnk"), Link(@"C:\Apps\app.exe", "--profile Work", @"C:\Apps"));
        File.WriteAllBytes(Path.Combine(_root, "Alias tool.lnk"), Link(@"c:\apps\APP.EXE", "--profile Work", @"c:\apps"));
        File.WriteAllBytes(Path.Combine(_root, "Other profile.lnk"), Link(@"C:\Apps\app.exe", "--profile Personal", @"C:\Apps"));
        var shortcuts = ShortcutScanner.Scan([new ShortcutRoot(_root, AppSources.StartMenu)]);
        var merged = AppCatalog.MergeEquivalent(shortcuts);
        Assert.Equal(2, merged.Count);
        Assert.Equal(merged.Select(entry => entry.Target), AppCatalog.MergeEquivalent(shortcuts.Reverse()).Select(entry => entry.Target));
        var duplicate = Assert.Single(merged.Where(entry => entry.NameAliases.Contains("Original app")));
        Assert.Contains("Alias tool", duplicate.NameAliases);
        Assert.Equal(2, duplicate.IdentityAliases.Count);
        Assert.EndsWith(".lnk", duplicate.Target);
        Assert.Equal(duplicate.Target, duplicate.IconPath);
        Assert.Equal(3, Directory.GetFiles(_root, "*.lnk").Length); // Never delete user shortcuts.
        var provider = new AppSearchProvider(new AppCatalog(() => [], _ => merged));
        var result = Assert.Single(await provider.SearchAsync("Original", default));
        Assert.Equal("Original app", result.Title);
        Assert.All(result.TitleMatchIndices, index => Assert.InRange(index, 0, result.Title.Length - 1));
        Assert.Equal(duplicate.Target, result.Target);
        Assert.Single(await provider.SearchAsync("Alias", default));
    }

    [Fact]
    public async Task Identical_urls_merge_but_case_query_fragment_and_same_named_distinct_targets_do_not()
    {
        WriteUrl("One", "https://example.invalid/Path?q=A#X");
        WriteUrl("Copy", "https://example.invalid/Path?q=A#X");
        WriteUrl("Case", "https://example.invalid/path?q=A#X");
        WriteUrl("Query", "https://example.invalid/Path?q=a#X");
        WriteUrl("Fragment", "https://example.invalid/Path?q=A#x");
        var catalog = new AppCatalog(() => [new ShortcutRoot(_root, AppSources.Desktop)], _ => []);
        Assert.Equal(4, (await catalog.GetAsync()).Count);
        Assert.Equal(5, Directory.GetFiles(_root, "*.url").Length);
        Assert.NotEqual(LaunchIdentity.ForTarget("https://example.invalid/A"), LaunchIdentity.ForTarget("https://example.invalid/a"));
    }

    [Theory]
    [InlineData("URL=https://example.invalid/A")]
    [InlineData("[Other]\nURL=https://example.invalid/A")]
    [InlineData("[InternetShortcut]\nURL=https://example.invalid/A\nURL=https://example.invalid/A")]
    [InlineData("[InternetShortcut]\nURL=https://example.invalid/A\n[InternetShortcut]")]
    [InlineData("[InternetShortcut]\nURL=relative")]
    public void Ambiguous_or_sectionless_urls_keep_their_shortcut_path_identity(string text)
    {
        var path = Path.Combine(_root, "Ambiguous.url");
        File.WriteAllText(path, text);
        Assert.Null(UrlShortcut.Read(path).Url);
        var entry = Assert.Single(ShortcutScanner.Scan([new ShortcutRoot(_root, AppSources.Desktop)]));
        Assert.Equal(LaunchIdentity.ForTarget(path), entry.IdentityKey);
    }

    [Fact]
    public void Oversized_url_rejects_even_an_early_valid_identity()
    {
        var path = Path.Combine(_root, "Huge.url");
        File.WriteAllText(path, "[InternetShortcut]\nURL=https://example.invalid/A\n" + new string('x', UrlShortcut.MaxReadChars));
        Assert.Null(UrlShortcut.Read(path).Url);
    }

    [Fact]
    public void Same_named_unknown_shortcuts_and_opaque_shell_targets_remain_distinct()
    {
        var entries = new[]
        {
            new AppCatalogEntry { DisplayName = "App", Target = @"C:\one\app.lnk", Source = AppSources.StartMenu },
            new AppCatalogEntry { DisplayName = "App", Target = @"C:\two\app.lnk", Source = AppSources.Desktop },
            new AppCatalogEntry { DisplayName = "App", Target = "shell:AppsFolder\\Package!App", Source = AppSources.AppsFolder },
        };
        Assert.Equal(3, AppCatalog.MergeEquivalent(entries).Count);
    }

    [Fact]
    public void Opaque_pidl_and_tracker_resolution_metadata_must_match_before_merging()
    {
        var plain = Link(@"C:\Apps\app.exe", "", @"C:\Apps");
        var first = WithPidl(plain, 1);
        Assert.NotNull(ShellLinkReader.ParseIdentity(first));
        Assert.Equal(ShellLinkReader.ParseIdentity(first), ShellLinkReader.ParseIdentity(first.ToArray()));
        Assert.NotEqual(ShellLinkReader.ParseIdentity(first), ShellLinkReader.ParseIdentity(WithPidl(plain, 2)));
        Assert.NotEqual(ShellLinkReader.ParseIdentity(first), ShellLinkReader.ParseIdentity(plain));
        var tracker = WithTracker(plain, 1);
        Assert.NotNull(ShellLinkReader.ParseIdentity(tracker));
        Assert.Equal(ShellLinkReader.ParseIdentity(tracker), ShellLinkReader.ParseIdentity(tracker.ToArray()));
        Assert.NotEqual(ShellLinkReader.ParseIdentity(tracker), ShellLinkReader.ParseIdentity(WithTracker(plain, 2)));
        Assert.NotEqual(ShellLinkReader.ParseIdentity(tracker), ShellLinkReader.ParseIdentity(plain));
        BinaryPrimitives.WriteUInt32LittleEndian(tracker.AsSpan(plain.Length - 4 + 12), 1);
        Assert.Null(ShellLinkReader.ParseIdentity(tracker));
        BinaryPrimitives.WriteUInt16LittleEndian(first.AsSpan(78), 1);
        Assert.Null(ShellLinkReader.ParseIdentity(first));
    }

    private static byte[] WithPidl(byte[] plain, byte marker)
    {
        var bytes = new byte[plain.Length + 8];
        plain.AsSpan(0, 76).CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)) | 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(76), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(78), 4);
        bytes[80] = marker;
        plain.AsSpan(76).CopyTo(bytes.AsSpan(84));
        return bytes;
    }

    private static byte[] WithTracker(byte[] plain, byte marker)
    {
        var bytes = new byte[plain.Length + 0x60];
        var start = plain.Length - 4;
        plain.AsSpan(0, start).CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(start), 0x60);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(start + 4), 0xA0000003);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(start + 8), 0x58);
        bytes[start + 32] = marker;
        return bytes;
    }

    private void WriteUrl(string name, string url) => File.WriteAllText(Path.Combine(_root, name + ".url"), "[InternetShortcut]\nURL=" + url);

    internal static byte[] Link(string target, string arguments, string workingDirectory, bool admin = false, uint show = 1)
    {
        var targetBytes = Encoding.ASCII.GetBytes(target + '\0');
        var volumeSize = 17;
        var infoSize = 28 + volumeSize + targetBytes.Length + 1;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write(76u);
        writer.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        writer.Write(2u | 0x10u | 0x20u | 0x80u | (admin ? 0x2000u : 0));
        writer.Write(new byte[36]);
        writer.Write(show);
        writer.Write(new byte[12]);
        writer.Write((uint)infoSize);
        writer.Write(28u);
        writer.Write(1u);
        writer.Write(28u);
        writer.Write((uint)(28 + volumeSize));
        writer.Write(0u);
        writer.Write((uint)(infoSize - 1));
        writer.Write((uint)volumeSize);
        writer.Write(3u);
        writer.Write(123u);
        writer.Write(16u);
        writer.Write((byte)0);
        writer.Write(targetBytes);
        writer.Write((byte)0);
        foreach (var text in new[] { workingDirectory, arguments })
        {
            writer.Write((ushort)text.Length);
            writer.Write(Encoding.Unicode.GetBytes(text));
        }
        writer.Write(0u); // ExtraData terminal block.
        writer.Flush();
        return stream.ToArray();
    }
}
