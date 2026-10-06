using System.IO;
using R1ng.Launcher.Core;
using R1ng.Launcher.Search;

namespace R1ng.Launcher.Tests;

public class SearchHistoryStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "r1ng-history-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_file))
                File.Delete(_file);

            var temporary = _file + ".tmp";
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void Record_deduplicates_by_target_and_keeps_the_newest_first()
    {
        var clock = new StubClock();
        var store = new SearchHistoryStore(_file, () => clock.Now);

        clock.Now = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        store.Record(Item("notepad", "记事本", AppSources.StartMenu));

        clock.Now = new DateTime(2024, 1, 1, 11, 0, 0, DateTimeKind.Utc);
        store.Record(Item("calc", "计算器", AppSources.StartMenu));

        clock.Now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Item("notepad", "记事本", AppSources.StartMenu));

        var recent = store.GetRecent(5);

        Assert.Equal(2, recent.Count);
        Assert.Equal("记事本", recent[0].Title);
        Assert.Equal("计算器", recent[1].Title);
    }

    [Fact]
    public void Record_respects_the_capacity()
    {
        var store = new SearchHistoryStore(_file, () => DateTime.UtcNow, capacity: 3);

        for (var index = 0; index < 6; index++)
            store.Record(Item($"app{index}", $"应用{index}", AppSources.StartMenu));

        Assert.Equal(3, store.GetRecent(10).Count);
        Assert.Equal("应用5", store.GetRecent(10)[0].Title);
    }

    [Fact]
    public void History_round_trips_through_disk_with_launch_metadata()
    {
        var store = new SearchHistoryStore(_file, () => DateTime.UtcNow);
        store.Record(new SearchResultItem
        {
            Id = "file|C:\\tmp\\report.docx",
            Kind = ResultKind.File,
            Title = "report.docx",
            Detail = @"C:\tmp",
            TypeLabel = "文件",
            Target = @"C:\tmp\report.docx",
            Source = AppSources.Everything,
        });

        var reloaded = new SearchHistoryStore(_file).GetRecent(5);

        Assert.Single(reloaded);
        Assert.Equal(ResultKind.File, reloaded[0].Kind);
        Assert.Equal(@"C:\tmp\report.docx", reloaded[0].Target);
        Assert.Equal(@"C:\tmp", reloaded[0].FolderPath);
        Assert.Equal("文件", reloaded[0].TypeLabel);
        Assert.Equal(AppSources.History, reloaded[0].Source);
    }

    [Fact]
    public void Corrupt_history_file_degrades_to_empty()
    {
        File.WriteAllText(_file, "{ this is not json");
        var store = new SearchHistoryStore(_file);

        Assert.Empty(store.GetRecent(5));

        // Recording still works once the file is replaced.
        store.Record(Item("notepad", "记事本", AppSources.StartMenu));
        Assert.Single(store.GetRecent(5));
    }

    [Fact]
    public void History_round_trips_the_icon_source_and_index()
    {
        var store = new SearchHistoryStore(_file, () => DateTime.UtcNow);
        store.Record(new SearchResultItem
        {
            Id = "app|start|C:\\games\\Celeste.url",
            Kind = ResultKind.Application,
            Title = "Celeste",
            Detail = @"C:\games",
            TypeLabel = "应用",
            Target = @"C:\games\Celeste.url",
            Source = AppSources.StartMenu,
            IconPath = @"C:\Windows\System32\notepad.exe",
            IconIndex = 2,
        });

        var reloaded = new SearchHistoryStore(_file).GetRecent(5);

        var item = Assert.Single(reloaded);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", item.IconPath);
        Assert.Equal(2, item.IconIndex);
    }

    [Fact]
    public void An_old_history_file_without_icon_fields_loads_with_backward_compatible_defaults()
    {
        // The shape written by the previous version: no IconPath / IconIndex properties at all.
        File.WriteAllText(
            _file,
            """
            [
              {
                "Title": "Celeste",
                "Target": "C:\\games\\Celeste.url",
                "KindValue": 0,
                "Detail": "C:\\games",
                "TypeLabel": "应用",
                "LastUsedUtc": "2024-01-01T00:00:00Z"
              }
            ]
            """
        );

        var item = Assert.Single(new SearchHistoryStore(_file).GetRecent(5));

        // Legacy rows fall back to the target as the icon source and the default "file icon" index.
        Assert.Equal(@"C:\games\Celeste.url", item.IconPath);
        Assert.Equal(-1, item.IconIndex);
    }

    [Fact]
    public void Recording_a_plain_shortcut_keeps_the_target_as_the_icon_source()
    {
        var store = new SearchHistoryStore(_file, () => DateTime.UtcNow);
        store.Record(Item("notepad", "记事本", AppSources.StartMenu));

        var item = Assert.Single(new SearchHistoryStore(_file).GetRecent(5));

        Assert.Equal($@"C:\apps\notepad.lnk", item.IconPath);
        Assert.Equal(-1, item.IconIndex);
    }

    private static SearchResultItem Item(string id, string title, string source) => new()
    {
        Id = id,
        Kind = ResultKind.Application,
        Title = title,
        TypeLabel = "应用",
        Target = $@"C:\apps\{id}.lnk",
        Source = source,
    };

    private sealed class StubClock
    {
        public DateTime Now { get; set; } = DateTime.UtcNow;
    }
}
