using System.IO;
using R1ng.Launcher.Theming;

namespace R1ng.Launcher.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "r1ng-settings-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_file))
                File.Delete(_file);
            if (File.Exists(_file + ".tmp"))
                File.Delete(_file + ".tmp");
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void Missing_file_loads_the_dark_default()
    {
        var settings = new SettingsStore(_file).Load();
        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public void A_saved_theme_round_trips()
    {
        var store = new SettingsStore(_file);

        Assert.True(store.SaveTheme(AppTheme.Light));
        Assert.True(File.Exists(_file));

        Assert.Equal(AppTheme.Light, store.Load().Theme);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_dark_without_throwing()
    {
        File.WriteAllText(_file, "{ this is not json ");
        Assert.Equal(AppTheme.Dark, new SettingsStore(_file).Load().Theme);
    }

    [Fact]
    public void An_unknown_theme_name_falls_back_to_dark()
    {
        File.WriteAllText(_file, """{ "Theme": "Neon" }""");
        Assert.Equal(AppTheme.Dark, new SettingsStore(_file).Load().Theme);
    }

    [Fact]
    public void An_out_of_range_theme_value_falls_back_to_dark()
    {
        File.WriteAllText(_file, """{ "Theme": 99 }""");
        Assert.Equal(AppTheme.Dark, new SettingsStore(_file).Load().Theme);
    }

    [Fact]
    public void Saving_never_leaves_the_temporary_file_behind()
    {
        var store = new SettingsStore(_file);
        store.SaveTheme(AppTheme.Light);
        Assert.False(File.Exists(_file + ".tmp"));
    }
}
