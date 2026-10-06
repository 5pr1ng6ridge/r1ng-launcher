using System.Windows;
using System.Windows.Media;
using R1ng.Launcher.Theming;

namespace R1ng.Launcher.Tests;

/// <summary>
/// The two palettes must be complete and interchangeable: identical key sets, and swapping one for
/// the other must replace exactly one dictionary so the live UI repaints.
/// </summary>
public class ThemePaletteTests
{
    [Fact]
    public void Dark_and_light_define_the_same_complete_key_set()
    {
        var dark = ThemePalette.Create(AppTheme.Dark);
        var light = ThemePalette.Create(AppTheme.Light);
        var darkKeys = Keys(dark);

        Assert.NotEmpty(darkKeys);
        Assert.Equal(darkKeys, Keys(light));

        // Spot-check the surfaces the requirement calls out explicitly.
        foreach (var key in new[]
                 {
                     "CardBackgroundBrush",
                     "ChipBackgroundBrush",
                     "ToggleCheckedBackgroundBrush",
                     "ResultSelectedBackgroundBrush",
                     "FooterButtonBackgroundBrush",
                     "DropdownBackgroundBrush",
                     "WarningBackgroundBrush",
                     "FailureBackgroundBrush",
                     "AccentBrush",
                     "TextPrimaryBrush",
                 })
        {
            Assert.Contains(key, darkKeys);
        }
    }

    [Fact]
    public void Applying_a_theme_replaces_the_single_theme_dictionary()
    {
        var target = new ResourceDictionary();

        Assert.True(ThemeManager.Apply(AppTheme.Dark, target));
        var darkCard = Brush(target, "CardBackgroundBrush");
        Assert.Single(target.MergedDictionaries);

        Assert.True(ThemeManager.Apply(AppTheme.Light, target));
        var lightCard = Brush(target, "CardBackgroundBrush");

        Assert.Single(target.MergedDictionaries);
        Assert.NotEqual(darkCard, lightCard);
        Assert.Equal(AppTheme.Light.ToString(), target[ThemePalette.MarkerKey]);
    }

    [Fact]
    public void The_default_theme_is_dark() => Assert.Equal(AppTheme.Dark, ThemeManager.DefaultTheme);

    private static Color Brush(ResourceDictionary dictionary, string key) =>
        ((SolidColorBrush)dictionary[key]).Color;

    private static string[] Keys(ResourceDictionary dictionary) => dictionary
        .Keys
        .Cast<object>()
        .Select(key => key.ToString() ?? string.Empty)
        .OrderBy(key => key, StringComparer.Ordinal)
        .ToArray();
}
