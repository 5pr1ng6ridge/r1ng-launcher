using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace R1ng.Launcher.Theming;

/// <summary>
/// Builds the complete dark/light brush palette in code so both palettes are guaranteed to define the
/// same key set and can be swapped at runtime without a resource-file round-trip. The window and the
/// control styles reference these keys through <c>DynamicResource</c> only.
/// </summary>
public static class ThemePalette
{
    /// <summary>Marker key present in every generated palette, used to find and replace it.</summary>
    public const string MarkerKey = "r1ng.theme.marker";

    private static readonly (string Key, string Dark, string Light)[] Colors =
    [
        ("TextPrimaryBrush", "#FFF6F6F8", "#FF1A1A20"),
        ("TextSecondaryBrush", "#9BFDFDFF", "#B31A1A20"),
        ("TextMutedBrush", "#73FDFDFF", "#801A1A20"),
        ("HighlightBrush", "#FF8AB4FF", "#FF1D4ED8"),
        ("AccentBrush", "#FF4C8DFF", "#FF2563EB"),
        ("CardBackgroundBrush", "#F21A1A20", "#F2F4F5F8"),
        ("CardBorderBrush", "#2EFFFFFF", "#22000000"),
        ("ChipBackgroundBrush", "#14FFFFFF", "#12000000"),
        ("ChipBorderBrush", "#22FFFFFF", "#22000000"),
        ("ChipHoverBackgroundBrush", "#26FFFFFF", "#24000000"),
        ("ChipCheckedTextBrush", "#FFFFFFFF", "#FFFFFFFF"),
        ("ToggleHoverBackgroundBrush", "#1FFFFFFF", "#1F000000"),
        ("ToggleCheckedBackgroundBrush", "#2EFFFFFF", "#1F000000"),
        ("HoverBorderBrush", "#66FFFFFF", "#66000000"),
        ("ResultHoverBackgroundBrush", "#14FFFFFF", "#12000000"),
        ("ResultSelectedBackgroundBrush", "#2EFFFFFF", "#262563EB"),
        ("TypeBadgeBackgroundBrush", "#1FFFFFFF", "#14000000"),
        ("FooterButtonBackgroundBrush", "#14FFFFFF", "#12000000"),
        ("FooterButtonBorderBrush", "#22FFFFFF", "#22000000"),
        ("FooterButtonHoverBackgroundBrush", "#33FF5A5A", "#33FF5A5A"),
        ("FooterButtonHoverBorderBrush", "#66FF5A5A", "#66FF5A5A"),
        ("FooterButtonHoverTextBrush", "#FFFFFFFF", "#FFFFFFFF"),
        ("WarningBackgroundBrush", "#26FFB020", "#26FFB020"),
        ("WarningTextBrush", "#FFFFD08A", "#FF7A4E00"),
        ("WarningAccentTextBrush", "#FFFFC46B", "#FF8A5A00"),
        ("FailureBackgroundBrush", "#33FF4D4D", "#33FF4D4D"),
        ("FailureTextBrush", "#FFFFB4B4", "#FFB00020"),
        ("DropdownBackgroundBrush", "#FF23232B", "#FFFFFFFF"),
        ("DropdownBorderBrush", "#33FFFFFF", "#22000000"),
        ("DropdownHoverBackgroundBrush", "#1FFFFFFF", "#12000000"),
        ("DropdownSelectedBackgroundBrush", "#2E4C8DFF", "#262563EB"),
    ];

    /// <summary>Every brush key a complete palette must define.</summary>
    public static IReadOnlyList<string> BrushKeys { get; } = Colors.Select(entry => entry.Key).ToArray();

    /// <summary>Creates a frozen, complete palette dictionary for the requested theme.</summary>
    public static ResourceDictionary Create(AppTheme theme)
    {
        var dark = theme == AppTheme.Dark;
        var dictionary = new ResourceDictionary { [MarkerKey] = theme.ToString() };

        foreach (var (key, darkColor, lightColor) in Colors)
            dictionary[key] = ParseBrush(dark ? darkColor : lightColor);

        return dictionary;
    }

    private static SolidColorBrush ParseBrush(string hex)
    {
        var value = uint.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var color = Color.FromArgb(
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        );

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
