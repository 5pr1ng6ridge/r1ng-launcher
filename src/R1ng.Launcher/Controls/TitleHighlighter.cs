using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace R1ng.Launcher.Controls;

/// <summary>
/// Attached behaviour that renders a title with the matched characters emphasized.
/// Usage: <c>controls:TitleHighlighter.Text="{Binding Title}"</c> plus
/// <c>controls:TitleHighlighter.MatchIndices="{Binding Model.TitleMatchIndices}"</c>.
/// </summary>
public static class TitleHighlighter
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(TitleHighlighter),
        new PropertyMetadata(null, OnChanged)
    );

    public static readonly DependencyProperty MatchIndicesProperty = DependencyProperty.RegisterAttached(
        "MatchIndices",
        typeof(IReadOnlyList<int>),
        typeof(TitleHighlighter),
        new PropertyMetadata(null, OnChanged)
    );

    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.RegisterAttached(
        "HighlightBrush",
        typeof(Brush),
        typeof(TitleHighlighter),
        new PropertyMetadata(null, OnChanged)
    );

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetMatchIndices(DependencyObject element, IReadOnlyList<int>? value) =>
        element.SetValue(MatchIndicesProperty, value);

    public static IReadOnlyList<int>? GetMatchIndices(DependencyObject element) =>
        (IReadOnlyList<int>?)element.GetValue(MatchIndicesProperty);

    public static void SetHighlightBrush(DependencyObject element, Brush? value) =>
        element.SetValue(HighlightBrushProperty, value);

    public static Brush? GetHighlightBrush(DependencyObject element) => (Brush?)element.GetValue(HighlightBrushProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock textBlock)
            Apply(textBlock);
    }

    private static void Apply(TextBlock textBlock)
    {
        var text = GetText(textBlock) ?? string.Empty;
        var indices = GetMatchIndices(textBlock);
        var highlight = GetHighlightBrush(textBlock) ?? Brushes.Orange;

        textBlock.Inlines.Clear();

        if (text.Length == 0 || indices is null || indices.Count == 0)
        {
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        var matched = new bool[text.Length];
        foreach (var index in indices)
        {
            if (index >= 0 && index < matched.Length)
                matched[index] = true;
        }

        var start = 0;
        while (start < text.Length)
        {
            var isMatch = matched[start];
            var end = start;
            while (end < text.Length && matched[end] == isMatch)
                end++;

            var run = new Run(text[start..end]);
            if (isMatch)
                run.Foreground = highlight;

            textBlock.Inlines.Add(run);
            start = end;
        }
    }
}
