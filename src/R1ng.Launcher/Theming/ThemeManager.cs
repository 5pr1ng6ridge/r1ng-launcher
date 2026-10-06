using System.Windows;

namespace R1ng.Launcher.Theming;

/// <summary>
/// Swaps the complete light/dark palette at application level. Every surface in the window and every
/// control style uses <c>DynamicResource</c>, so replacing the palette dictionary repaints the live
/// UI without rebuilding it. Without an application resource scope the call is a no-op.
/// </summary>
public static class ThemeManager
{
    public static readonly AppTheme DefaultTheme = AppTheme.Dark;

    /// <summary>Applies a palette to the given resource scope (defaults to the application).</summary>
    public static bool Apply(AppTheme theme, ResourceDictionary? target = null)
    {
        target ??= Application.Current?.Resources;
        if (target is null)
            return false;

        var dictionary = ThemePalette.Create(theme);
        var merged = target.MergedDictionaries;

        // Replace only the previously applied palette, never unrelated merged dictionaries.
        for (var index = merged.Count - 1; index >= 0; index--)
        {
            if (merged[index].Contains(ThemePalette.MarkerKey))
                merged.RemoveAt(index);
        }

        // First, so a screen-specific dictionary can still override individual keys if needed.
        merged.Insert(0, dictionary);
        return true;
    }
}
