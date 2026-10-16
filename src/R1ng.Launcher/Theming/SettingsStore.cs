using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace R1ng.Launcher.Theming;

/// <summary>
/// Loads and saves <see cref="LauncherSettings"/> at
/// <c>%AppData%\r1ng-launcher\settings.json</c>. All file access is best effort: a missing, corrupt
/// or unreadable file (or an unknown theme name) falls back to the dark default instead of failing
/// startup, and saving uses the same atomic temp-then-move pattern as the history store.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public SettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "r1ng-launcher",
            "settings.json"
        );
    }

    public string FilePath => _filePath;

    /// <summary>Loads persisted settings, or the dark default on any problem.</summary>
    public LauncherSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return LauncherSettings.Default;

            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<LauncherSettings>(json, SerializerOptions);
            if (settings is null)
                return LauncherSettings.Default;

            return Enum.IsDefined(typeof(AppTheme), settings.Theme) ? settings : LauncherSettings.Default;
        }
        catch (Exception)
        {
            // Corrupt or unreadable settings are not worth surfacing.
            return LauncherSettings.Default;
        }
    }

    /// <summary>Persists settings atomically; failures are swallowed.</summary>
    public bool Save(LauncherSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            var temporaryPath = _filePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Convenience helper used by the theme toggle.</summary>
    public bool SaveTheme(AppTheme theme) => Save(LauncherSettings.Default with { Theme = theme });
}
