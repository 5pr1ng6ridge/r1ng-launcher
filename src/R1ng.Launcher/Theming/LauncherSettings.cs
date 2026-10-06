namespace R1ng.Launcher.Theming;

/// <summary>
/// User settings persisted to <c>%AppData%\r1ng-launcher\settings.json</c>. Only the theme is stored;
/// the search query is deliberately kept in-process because it must survive a hide/show, not a
/// restart.
/// </summary>
public sealed record LauncherSettings
{
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    public static LauncherSettings Default { get; } = new();
}
