namespace R1ng.Launcher.Theming;

/// <summary>The launcher's two complete colour palettes. Dark is the default.</summary>
public enum AppTheme
{
    Dark = 0,
    Light = 1,
}

/// <summary>A selectable theme entry for the UI theme selector (ComboBox item).</summary>
public sealed record ThemeOption(AppTheme Value, string DisplayName);
