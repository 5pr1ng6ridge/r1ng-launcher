using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using R1ng.Launcher.Search;
using R1ng.Launcher.ViewModels;

namespace R1ng.Launcher;

/// <summary>
/// Small themed manager for hidden results: lists what the current profile hides and restores one
/// item or all of them through the launcher view model (which refreshes the search). It is shown
/// modally over the launcher; the launcher itself holds back its focus-loss hide while it is open.
/// </summary>
public partial class HiddenItemsWindow : Window
{
    private readonly LauncherViewModel _viewModel;

    public HiddenItemsWindow(LauncherViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        RowsList.ItemsSource = Rows;
        HeaderBar.MouseLeftButtonDown += OnHeaderMouseLeftButtonDown;
        PreviewKeyDown += OnPreviewKeyDown;
        Reload();
    }

    /// <summary>The hidden entries currently shown.</summary>
    internal ObservableCollection<HiddenItemRow> Rows { get; } = new();

    /// <summary>Re-reads the hidden list (and any store problem) from the view model.</summary>
    internal void Reload()
    {
        Rows.Clear();
        foreach (var record in _viewModel.HiddenItems)
            Rows.Add(HiddenItemRow.From(record));

        var empty = Rows.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RestoreAllButton.IsEnabled = !empty;
        CountText.Text = empty
            ? "当前没有隐藏的项目。"
            : $"共 {Rows.Count} 项。恢复后会重新出现在搜索结果和最近使用中；历史、置顶和排序数据不受影响。";

        ShowError(_viewModel.HiddenError);
    }

    /// <summary>Restores one entry; on failure the reason stays visible in the window.</summary>
    internal bool RestoreOne(string key)
    {
        var restored = _viewModel.RestoreHidden(key);
        Reload();
        if (!restored)
            ShowError(_viewModel.HiddenError ?? _viewModel.ActionNotice ?? "恢复失败");

        return restored;
    }

    /// <summary>Restores every entry; on failure the reason stays visible in the window.</summary>
    internal bool RestoreAll()
    {
        var restored = _viewModel.RestoreAllHidden();
        Reload();
        if (!restored)
            ShowError(_viewModel.HiddenError ?? _viewModel.ActionNotice ?? "恢复失败");

        return restored;
    }

    private void ShowError(string? message)
    {
        var hasError = !string.IsNullOrWhiteSpace(message);
        ErrorText.Text = hasError ? message : string.Empty;
        ErrorBanner.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HiddenItemRow row })
            RestoreOne(row.Key);
    }

    private void OnRestoreAllClick(object sender, RoutedEventArgs e) => RestoreAll();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        Close();
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the move started.
        }
    }
}

/// <summary>One row of the manager: the stored key plus what is shown to identify the item.</summary>
public sealed class HiddenItemRow
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public string Target { get; init; } = string.Empty;

    public string TypeLabel { get; init; } = string.Empty;

    /// <summary>Second line: the stored detail, otherwise the original target path.</summary>
    public string DetailText { get; init; } = string.Empty;

    public string RestoreAutomationName => $"恢复 {Title}";

    internal static HiddenItemRow From(HiddenResultRecord record) => new()
    {
        Key = record.Key,
        Title = string.IsNullOrWhiteSpace(record.Title) ? record.Target : record.Title,
        Target = record.Target,
        TypeLabel = record.TypeLabel,
        DetailText = string.IsNullOrWhiteSpace(record.Detail) ? record.Target : record.Detail,
    };
}
