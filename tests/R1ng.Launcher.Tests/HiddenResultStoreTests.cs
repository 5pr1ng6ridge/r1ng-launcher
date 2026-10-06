using System.IO;
using System.Text;
using R1ng.Launcher.Core;
using R1ng.Launcher.Search;

namespace R1ng.Launcher.Tests;

public class HiddenResultStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "r1ng-hidden-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public HiddenResultStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, "hidden.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void A_missing_file_is_a_normal_empty_store()
    {
        var store = new HiddenResultStore(_file);

        Assert.Empty(store.GetHidden());
        Assert.False(store.IsHidden(App("notepad", "记事本")));
        Assert.Null(store.Error);
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void Hiding_persists_the_canonical_key_aliases_and_display_fields_across_a_restart()
    {
        var item = App("notepad", "记事本") with
        {
            IdentityKey = "identity-notepad",
            IdentityAliases = ["alias-lnk-path"],
            Detail = @"C:\Start Menu",
            IsFolder = false,
        };
        var store = new HiddenResultStore(_file, () => new DateTime(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc));

        Assert.True(store.TryHide(item));
        Assert.Null(store.Error);

        var reloaded = new HiddenResultStore(_file);
        var record = Assert.Single(reloaded.GetHidden());
        Assert.Equal("identity-notepad", record.Key);
        Assert.Equal("identity-notepad", LaunchIdentity.ForItem(item));
        Assert.Equal("记事本", record.Title);
        Assert.Equal(@"C:\Start Menu", record.Detail);
        Assert.Equal(item.Target, record.Target);
        Assert.Equal("应用", record.TypeLabel);
        Assert.Equal(ResultKind.Application, record.Kind);
        Assert.Equal(new[] { "alias-lnk-path" }, record.IdentityAliases);
        Assert.Equal(new DateTime(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc), record.HiddenAtUtc);
        Assert.True(reloaded.IsHidden(item));
        Assert.Null(reloaded.Error);
        // Titles are written unescaped so the file stays readable.
        Assert.Contains("记事本", File.ReadAllText(_file, Encoding.UTF8));
        Assert.False(File.Exists(_file + ".tmp"));
    }

    [Fact]
    public void A_row_without_identity_or_target_cannot_be_hidden()
    {
        var store = new HiddenResultStore(_file);
        var item = App("anonymous", "无目标") with { Target = string.Empty };

        Assert.False(store.TryHide(item));
        Assert.NotNull(store.Error);
        Assert.Empty(store.GetHidden());
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void Rows_match_by_canonical_identity_alias_or_original_target_never_by_title()
    {
        var store = new HiddenResultStore(_file);
        var hidden = App("notepad", "记事本") with
        {
            IdentityKey = "identity-notepad",
            IdentityAliases = ["alias-lnk-path"],
        };
        Assert.True(store.TryHide(hidden));

        // Same canonical identity, unrelated title/target/id.
        Assert.True(store.IsHidden(Other("a", "完全不同的标题") with { IdentityKey = "identity-notepad" }));
        // The other row names the hidden canonical key as one of its aliases.
        Assert.True(store.IsHidden(Other("b", "别名行") with { IdentityKey = "identity-other", IdentityAliases = ["identity-notepad"] }));
        // The other row's identity is one of the hidden row's recorded aliases.
        Assert.True(store.IsHidden(Other("c", "路径行") with { IdentityKey = "alias-lnk-path" }));
        // Both rows carry the same verified alias.
        Assert.True(store.IsHidden(Other("d", "同别名") with { IdentityKey = "identity-d", IdentityAliases = ["alias-lnk-path"] }));
        // Same original target and no identity of its own (a differently-spelled path still normalizes).
        Assert.True(store.IsHidden(new SearchResultItem
        {
            Id = "file|x",
            Kind = ResultKind.File,
            Title = "x",
            TypeLabel = "文件",
            Target = hidden.Target.ToUpperInvariant(),
        }));

        // Same title with a different identity and target is a different thing and stays visible.
        Assert.False(store.IsHidden(Other("e", "记事本") with { IdentityKey = "identity-else" }));
        Assert.False(store.IsHidden(Other("f", "记事本")));
        // Different title, identity and target.
        Assert.False(store.IsHidden(App("calc", "计算器")));
    }

    [Fact]
    public void An_item_without_identity_hides_by_its_target_path_and_folders_stay_distinct_from_files()
    {
        var store = new HiddenResultStore(_file);
        var file = new SearchResultItem
        {
            Id = "file|C:\\data\\report",
            Kind = ResultKind.File,
            Title = "report",
            TypeLabel = "文件",
            Target = @"C:\data\report",
        };
        Assert.True(store.TryHide(file));

        Assert.Equal(LaunchIdentity.ForTarget(@"C:\data\report"), Assert.Single(store.GetHidden()).Key);
        Assert.True(store.IsHidden(file with { Id = "history|C:\\data\\report", Target = @"c:\DATA\report\" }));
        Assert.False(store.IsHidden(file with { Kind = ResultKind.Folder, IsFolder = true }));
        Assert.False(store.IsHidden(file with { Target = @"C:\data\report2" }));
    }

    [Fact]
    public void Hiding_an_equivalent_row_again_keeps_one_record_and_merges_new_aliases()
    {
        var clock = new StubClock();
        var store = new HiddenResultStore(_file, () => clock.Now);
        var first = App("notepad", "记事本") with { IdentityKey = "identity-notepad", IdentityAliases = ["alias-1"] };
        clock.Now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(store.TryHide(first));

        // No new information: succeed without saving. A locked file would fail any real save.
        using (new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(store.TryHide(first));
        Assert.Single(store.GetHidden());

        // The same thing reached through an alias contributes its other spellings.
        clock.Now = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var viaAlias = Other("again", "记事本 (快捷方式)") with { IdentityKey = "identity-other", IdentityAliases = ["alias-1", "alias-2"] };
        Assert.True(store.TryHide(viaAlias));

        var record = Assert.Single(store.GetHidden());
        Assert.Equal("identity-notepad", record.Key);
        Assert.Equal("记事本", record.Title);
        Assert.Contains("alias-2", record.IdentityAliases);
        Assert.Contains("identity-other", record.IdentityAliases);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), record.HiddenAtUtc);
        Assert.True(new HiddenResultStore(_file).IsHidden(Other("later", "x") with { IdentityKey = "alias-2" }));
    }

    [Fact]
    public void Entries_are_listed_newest_first()
    {
        var clock = new StubClock();
        var store = new HiddenResultStore(_file, () => clock.Now);

        foreach (var index in Enumerable.Range(0, 3))
        {
            clock.Now = new DateTime(2025, 1, 1 + index, 0, 0, 0, DateTimeKind.Utc);
            Assert.True(store.TryHide(App($"app{index}", $"应用{index}")));
        }

        Assert.Equal(new[] { "应用2", "应用1", "应用0" }, store.GetHidden().Select(record => record.Title));
        Assert.Equal(new[] { "应用2", "应用1", "应用0" }, new HiddenResultStore(_file).GetHidden().Select(record => record.Title));
    }

    [Fact]
    public void Restoring_one_entry_or_all_makes_rows_visible_again_and_persists()
    {
        var store = new HiddenResultStore(_file);
        var a = App("a", "甲");
        var b = App("b", "乙");
        var c = App("c", "丙");
        Assert.True(store.TryHide(a));
        Assert.True(store.TryHide(b));
        Assert.True(store.TryHide(c));

        var keyOfB = store.GetHidden().Single(record => record.Title == "乙").Key;
        Assert.True(store.TryRestore(keyOfB));

        Assert.False(store.IsHidden(b));
        Assert.True(store.IsHidden(a));
        Assert.True(store.IsHidden(c));
        Assert.Equal(2, store.GetHidden().Count);
        var afterOne = new HiddenResultStore(_file);
        Assert.False(afterOne.IsHidden(b));
        Assert.Equal(2, afterOne.GetHidden().Count);

        Assert.True(store.TryRestoreAll());
        Assert.Empty(store.GetHidden());
        Assert.False(store.IsHidden(a));
        Assert.Null(store.Error);
        var afterAll = new HiddenResultStore(_file);
        Assert.Empty(afterAll.GetHidden());
        Assert.False(afterAll.IsHidden(c));
        Assert.Null(afterAll.Error);

        // Restoring everything of an already empty store is a harmless success.
        Assert.True(afterAll.TryRestoreAll());
    }

    [Fact]
    public void Restoring_an_unknown_key_fails_visibly_and_changes_nothing()
    {
        var store = new HiddenResultStore(_file);
        Assert.True(store.TryHide(App("a", "甲")));

        Assert.False(store.TryRestore("missing-key"));
        Assert.False(store.TryRestore(string.Empty));
        Assert.NotNull(store.Error);
        Assert.Single(store.GetHidden());

        // A later successful change clears the message.
        Assert.True(store.TryRestoreAll());
        Assert.Null(store.Error);
    }

    [Fact]
    public void A_failed_save_leaves_memory_and_file_unchanged_for_hide_restore_and_restore_all()
    {
        var store = new HiddenResultStore(_file);
        var a = App("a", "甲");
        var b = App("b", "乙");
        Assert.True(store.TryHide(a));
        var original = File.ReadAllBytes(_file);
        var keyOfA = Assert.Single(store.GetHidden()).Key;

        // Holding the file open without delete sharing makes the atomic replace fail.
        using (new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(store.TryHide(b));
            Assert.NotNull(store.Error);
            Assert.False(store.IsHidden(b));
            Assert.Equal(new[] { keyOfA }, store.GetHidden().Select(record => record.Key));

            Assert.False(store.TryRestore(keyOfA));
            Assert.True(store.IsHidden(a));
            Assert.Single(store.GetHidden());

            Assert.False(store.TryRestoreAll());
            Assert.True(store.IsHidden(a));
            Assert.Single(store.GetHidden());
        }

        Assert.Equal(original, File.ReadAllBytes(_file));
        Assert.False(File.Exists(_file + ".tmp"));
        Assert.True(new HiddenResultStore(_file).IsHidden(a));

        // Once the file is writable again the same store recovers and clears the error.
        Assert.True(store.TryHide(b));
        Assert.Null(store.Error);
        Assert.Equal(2, new HiddenResultStore(_file).GetHidden().Count);
    }

    [Fact]
    public void A_failed_first_save_does_not_hide_anything_or_create_a_file()
    {
        // A directory squatting on the temporary name makes writing it fail deterministically.
        Directory.CreateDirectory(_file + ".tmp");
        var store = new HiddenResultStore(_file);
        var item = App("a", "甲");

        Assert.False(store.TryHide(item));
        Assert.NotNull(store.Error);
        Assert.False(store.IsHidden(item));
        Assert.Empty(store.GetHidden());
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void A_corrupt_file_reports_an_error_is_never_overwritten_and_can_be_recovered()
    {
        const string corrupt = "{ this is not json";
        File.WriteAllText(_file, corrupt);
        var store = new HiddenResultStore(_file);

        Assert.False(store.IsHidden(App("a", "甲")));
        Assert.NotNull(store.Error);
        Assert.Empty(store.GetHidden());
        Assert.False(store.TryHide(App("a", "甲")));
        Assert.False(store.TryRestore("anything"));
        Assert.False(store.TryRestoreAll());
        Assert.NotNull(store.Error);

        Assert.Equal(corrupt, File.ReadAllText(_file));
        Assert.False(File.Exists(_file + ".tmp"));

        // Repairing the file is picked up the next time the list is opened or changed.
        var repaired = new HiddenResultStore(Path.Combine(_directory, "other.json"));
        Assert.True(repaired.TryHide(App("a", "甲")));
        File.Copy(repaired.FilePath, _file, overwrite: true);

        Assert.Single(store.GetHidden());
        Assert.Null(store.Error);
        Assert.True(store.IsHidden(App("a", "甲")));
        Assert.True(store.TryHide(App("b", "乙")));
        Assert.Equal(2, new HiddenResultStore(_file).GetHidden().Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"Version\":2,\"Items\":[]}")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"Items\":[null]}")]
    [InlineData("{\"Version\":1,\"Items\":[{\"Title\":\"没有标识\"}]}")]
    public void An_unrecognized_file_is_an_error_and_is_left_untouched(string content)
    {
        File.WriteAllText(_file, content);
        var store = new HiddenResultStore(_file);

        Assert.Empty(store.GetHidden());
        Assert.NotNull(store.Error);
        Assert.False(store.TryHide(App("a", "甲")));
        Assert.Equal(content, File.ReadAllText(_file));
    }

    [Fact]
    public void An_oversized_file_is_rejected_without_being_read_or_replaced()
    {
        using (var stream = new FileStream(_file, FileMode.Create))
            stream.SetLength(HiddenResultStore.MaxFileBytes + 1L);
        var store = new HiddenResultStore(_file);

        Assert.Empty(store.GetHidden());
        Assert.NotNull(store.Error);
        Assert.False(store.TryHide(App("a", "甲")));
        Assert.Equal(HiddenResultStore.MaxFileBytes + 1L, new FileInfo(_file).Length);
    }

    [Fact]
    public void A_full_store_rejects_new_entries_instead_of_evicting_old_ones()
    {
        var store = new HiddenResultStore(_file, null, maxRecords: 3);
        var items = Enumerable.Range(0, 4).Select(index => App($"app{index}", $"应用{index}")).ToArray();
        foreach (var item in items.Take(3))
            Assert.True(store.TryHide(item));

        Assert.False(store.TryHide(items[3]));
        Assert.NotNull(store.Error);
        Assert.False(store.IsHidden(items[3]));
        Assert.All(items.Take(3), item => Assert.True(store.IsHidden(item)));
        Assert.Equal(3, new HiddenResultStore(_file, null, maxRecords: 3).GetHidden().Count);

        // Making room works, and a row that is already hidden never needs room.
        Assert.True(store.TryHide(items[0]));
        Assert.True(store.TryRestore(store.GetHidden().First().Key));
        Assert.True(store.TryHide(items[3]));
    }

    [Fact]
    public void Concurrent_hides_and_lookups_are_all_kept()
    {
        var store = new HiddenResultStore(_file);
        var items = Enumerable.Range(0, 40).Select(index => App($"app{index:D2}", $"应用{index:D2}")).ToArray();

        Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = 8 }, item =>
        {
            Assert.True(store.TryHide(item));
            Assert.True(store.IsHidden(item));
            Assert.NotNull(store.GetHidden());
        });

        Assert.Equal(40, store.GetHidden().Count);
        Assert.Null(store.Error);
        var reloaded = new HiddenResultStore(_file);
        Assert.All(items, item => Assert.True(reloaded.IsHidden(item)));
        Assert.Equal(40, reloaded.GetHidden().Count);
    }

    [Fact]
    public void An_empty_path_is_rejected_so_there_is_no_default_profile_write()
    {
        Assert.Throws<ArgumentException>(() => new HiddenResultStore(string.Empty));
        Assert.Throws<ArgumentException>(() => new HiddenResultStore("  "));
    }

    private static SearchResultItem App(string id, string title) => new()
    {
        Id = $"app|{id}",
        Kind = ResultKind.Application,
        Title = title,
        TypeLabel = "应用",
        Target = $@"C:\apps\{id}.lnk",
        Source = AppSources.StartMenu,
    };

    private static SearchResultItem Other(string id, string title) => new()
    {
        Id = $"other|{id}",
        Kind = ResultKind.Application,
        Title = title,
        TypeLabel = "应用",
        Target = $@"C:\elsewhere\{id}.lnk",
        Source = AppSources.Desktop,
    };

    private sealed class StubClock
    {
        public DateTime Now { get; set; } = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
