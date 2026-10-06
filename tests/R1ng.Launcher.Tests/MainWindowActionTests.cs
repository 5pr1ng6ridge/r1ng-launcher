using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;
using R1ng.Launcher.Search;
using R1ng.Launcher.ViewModels;
using FakeHiddenStore = R1ng.Launcher.Tests.LauncherActionTests.FakeHiddenStore;
using FakeHistory = R1ng.Launcher.Tests.LauncherActionTests.FakeHistory;
using FakePreviewService = R1ng.Launcher.Tests.LauncherActionTests.FakePreviewService;
using MutableAppProvider = R1ng.Launcher.Tests.LauncherActionTests.MutableAppProvider;
using NoopFileProvider = R1ng.Launcher.Tests.LauncherActionTests.NoopFileProvider;

namespace R1ng.Launcher.Tests;

/// <summary>
/// The new window actions through the real <c>MainWindow</c> routing on an STA thread: the double
/// click on a hit row, Ctrl+Space and Ctrl+G, pasting multi-line text into the search box, the
/// notepad editing session (start, save write-back, stale and conflicting saves, end, close) and the
/// hidden-items manager. Mouse, key and paste events are raised as real routed events; only the
/// keyboard modifier state is injected. The editor, QuickLook service, hidden store, open action and
/// history are fakes, so no process is launched, no pipe is opened, the clipboard is never touched and
/// no profile file is written.
/// </summary>
public class MainWindowActionTests
{
    private static readonly Size RealizeBounds = new(760, 520);

    private static readonly PropertyInfo ClickCountProperty =
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!;

    private static readonly FieldInfo IsRepeatField =
        typeof(KeyEventArgs).GetField("_isRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // The key WPF reports: ImeProcessed while an IME owns the key, the real key then being kept apart
    // (and exposed as ImeProcessedKey). Built with the real key, then flipped to ImeProcessed.
    private static readonly FieldInfo ReportedKeyField =
        typeof(KeyEventArgs).GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // ----- double click ------------------------------------------------------------------------------

    [Fact]
    public async Task Double_clicking_a_row_opens_that_row_once_through_the_normal_open_flow()
    {
        await RunAsync(harness =>
        {
            harness.ShowRows(3);
            Assert.Equal(0, harness.ViewModel.SelectedIndex);

            var handled = Click(HitOf(harness, 1), MouseButton.Left, 2);

            // The clicked row, not the selected one, goes through Open: history is recorded and the
            // launcher is asked to hide, exactly like Enter.
            Assert.True(handled);
            Assert.Equal(new[] { Item(2).Target }, harness.Open.Targets);
            Assert.Equal(Item(2).Target, Assert.Single(harness.History.Recorded).Target);
            Assert.Equal(0, harness.ViewModel.SelectedIndex);
        });
    }

    [Fact]
    public async Task A_failed_open_from_a_double_click_records_nothing_and_says_so()
    {
        await RunAsync(harness =>
        {
            harness.ShowRows(2);
            harness.Open.Succeeds = false;

            Click(HitOf(harness, 0), MouseButton.Left, 2);

            Assert.Single(harness.Open.Targets);
            Assert.Empty(harness.History.Recorded);
            Assert.Contains("无法打开", harness.ViewModel.StatusText);
        });
    }

    [Fact]
    public async Task Single_clicks_blank_space_scroll_bars_and_other_buttons_never_open()
    {
        await RunAsync(harness =>
        {
            harness.ShowRows(2);
            var list = harness.Window.ResultsList;
            var scroll = Descendants(list).OfType<ScrollViewer>().First();

            Assert.False(Click(HitOf(harness, 0), MouseButton.Left, 1));
            Assert.False(Click(list, MouseButton.Left, 2));
            Assert.False(Click(scroll, MouseButton.Left, 2));
            Assert.False(Click(HitOf(harness, 1), MouseButton.Right, 2));

            Assert.Empty(harness.Open.Targets);
            Assert.Empty(harness.History.Recorded);

            // Control: the same row does open with a real double click.
            Assert.True(Click(HitOf(harness, 1), MouseButton.Left, 2));
            Assert.Single(harness.Open.Targets);
        });
    }

    // ----- Ctrl+Space ----------------------------------------------------------------------------------

    [Fact]
    public async Task Ctrl_Space_previews_the_selected_row_without_opening_recording_or_hiding()
    {
        await RunAsync(harness =>
        {
            harness.SetRows(2);
            ShowFocused(harness);
            harness.SetModifiers(ModifierKeys.Control);

            var args = Press(harness.Window, Key.Space);

            Assert.True(args.Handled);
            Assert.Equal(Item(1).Target, Assert.Single(harness.Preview.Requests).Target);
            Assert.Contains("已发送到 QuickLook", harness.ViewModel.ActionNotice);
            Assert.True(harness.Window.IsVisible, "a preview must not hide the launcher");
            Assert.Empty(harness.Open.Targets);
            Assert.Empty(harness.History.Recorded);

            // A held key does not toggle again, and only the exact Ctrl chord counts.
            Assert.True(Press(harness.Window, Key.Space, repeat: true).Handled);
            harness.SetModifiers(ModifierKeys.None);
            Assert.False(Press(harness.Window, Key.Space).Handled);
            harness.SetModifiers(ModifierKeys.Control | ModifierKeys.Shift);
            Assert.False(Press(harness.Window, Key.Space).Handled);
            harness.SetModifiers(ModifierKeys.Control | ModifierKeys.Alt);
            Assert.False(Press(harness.Window, Key.Space).Handled);
            Assert.Single(harness.Preview.Requests);
        });
    }

    [Fact]
    public async Task The_Ctrl_chords_also_work_when_an_IME_hands_the_key_over_as_ImeProcessed()
    {
        await RunAsync(harness =>
        {
            harness.SetRows(2);
            harness.ViewModel.Query = "item";
            ShowFocused(harness);
            harness.SetModifiers(ModifierKeys.Control);

            // Ctrl+Space with a Chinese IME active: WPF reports ImeProcessed, the real key is Space.
            var space = Press(harness.Window, Key.Space, imeProcessed: true);
            Assert.True(space.Handled);
            Assert.Equal(Item(1).Target, Assert.Single(harness.Preview.Requests).Target);

            // Still exact and once per press.
            Assert.True(Press(harness.Window, Key.Space, repeat: true, imeProcessed: true).Handled);
            harness.SetModifiers(ModifierKeys.Control | ModifierKeys.Shift);
            Assert.False(Press(harness.Window, Key.Space, imeProcessed: true).Handled);
            harness.SetModifiers(ModifierKeys.None);
            Assert.False(Press(harness.Window, Key.Space, imeProcessed: true).Handled);
            Assert.Single(harness.Preview.Requests);

            // Ctrl+G through the IME edits the input.
            harness.SetModifiers(ModifierKeys.Control);
            Assert.True(Press(harness.Window, Key.G, imeProcessed: true).Handled);
            Assert.Equal(new[] { "item" }, harness.Editor.StartTexts);
        });
    }

    [Fact]
    public async Task Keys_an_IME_processed_other_than_the_two_chords_stay_with_the_IME()
    {
        await RunAsync(harness =>
        {
            harness.SetRows(2);
            ShowFocused(harness);

            // Enter commits an IME composition, Esc cancels it, Tab/arrows pick candidates: none of
            // them may open a row, hide the launcher, switch the scope or move the selection.
            foreach (var modifiers in new[] { ModifierKeys.None, ModifierKeys.Control })
            {
                harness.SetModifiers(modifiers);
                foreach (var key in new[] { Key.Enter, Key.Escape, Key.Tab, Key.Down, Key.Up, Key.C })
                    Assert.False(Press(harness.Window, key, imeProcessed: true).Handled, $"{modifiers}+{key}");
            }

            Assert.Empty(harness.Open.Targets);
            Assert.Empty(harness.History.Recorded);
            Assert.True(harness.Window.IsVisible);
            Assert.True(harness.ViewModel.IsScopeApplications);
            Assert.Equal(0, harness.ViewModel.SelectedIndex);
            Assert.Empty(harness.Preview.Requests);
            Assert.Empty(harness.Editor.StartTexts);

            // Control: the same keys do act when no IME is involved.
            harness.SetModifiers(ModifierKeys.None);
            Assert.True(Press(harness.Window, Key.Down).Handled);
            Assert.Equal(1, harness.ViewModel.SelectedIndex);
        });
    }

    [Fact]
    public async Task The_new_footer_actions_use_the_neutral_hover_and_only_quit_keeps_the_red_one()
    {
        await RunAsync(harness =>
        {
            var window = harness.Window;
            var neutral = Assert.IsType<Style>(window.Resources["FooterActionButtonStyle"]);
            var quit = Assert.IsType<Style>(window.Resources["FooterButtonStyle"]);

            Assert.NotSame(neutral, quit);
            Assert.Same(neutral, window.EditingButton.Style);
            Assert.Same(neutral, window.HiddenItemsButton.Style);

            // The window was never shown, so look for the quit button in the laid-out content.
            harness.Layout();
            var quitButton = Descendants((DependencyObject)window.Content)
                .OfType<Button>()
                .First(button => button.Content as string == "退出");
            Assert.Same(quit, quitButton.Style);
        });
    }

    [Fact]
    public async Task Ctrl_Space_and_Ctrl_G_leave_the_keys_alone_while_the_theme_selector_owns_the_keyboard()
    {
        await RunAsync(harness =>
        {
            harness.SetRows(1);
            ShowFocused(harness);
            Keyboard.Focus(harness.Window.ThemeSelector);
            Assert.True(harness.Window.ThemeSelector.IsKeyboardFocusWithin, "the theme selector could not take focus");
            Assert.False(harness.Window.SearchBox.IsKeyboardFocusWithin);
            harness.SetModifiers(ModifierKeys.Control);

            Assert.False(Press(harness.Window, Key.Space).Handled);
            Assert.False(Press(harness.Window, Key.G).Handled);

            Assert.Empty(harness.Preview.Requests);
            Assert.Empty(harness.Editor.StartTexts);
        });
    }

    // ----- Ctrl+G --------------------------------------------------------------------------------------

    [Fact]
    public async Task Ctrl_G_edits_the_complete_input_including_a_folder_prefix_and_hides_the_launcher()
    {
        await RunAsync(harness =>
        {
            const string query = "\"C:\\Docs\" report";
            harness.ViewModel.Query = query;
            ShowFocused(harness);
            harness.SetModifiers(ModifierKeys.Control);

            var args = Press(harness.Window, Key.G);

            Assert.True(args.Handled);
            Assert.Equal(new[] { query }, harness.Editor.StartTexts);
            Assert.False(harness.Window.IsVisible, "a started edit hands the screen to the editor");
            Assert.Equal(query, harness.ViewModel.Query);
            Assert.Equal(harness.Editor.CurrentSessionId, harness.Window.EditingSessionId);
            Assert.NotNull(harness.Window.EditingSessionId);

            // The footer shows an editing indicator whose tooltip names the recoverable draft.
            Assert.Equal(Visibility.Visible, harness.Window.EditingButton.Visibility);
            Assert.Contains(harness.Editor.DraftPath!, Assert.IsType<string>(harness.Window.EditingButton.ToolTip));
            Assert.False(harness.Window.ApplySavedMenuItem.IsEnabled);
        });
    }

    [Fact]
    public async Task Ctrl_G_needs_the_exact_chord_and_the_search_box_focus_and_ignores_a_held_key()
    {
        await RunAsync(harness =>
        {
            ShowFocused(harness);

            harness.SetModifiers(ModifierKeys.None);
            Assert.False(Press(harness.Window, Key.G).Handled);
            harness.SetModifiers(ModifierKeys.Control | ModifierKeys.Shift);
            Assert.False(Press(harness.Window, Key.G).Handled);
            harness.SetModifiers(ModifierKeys.Control | ModifierKeys.Alt);
            Assert.False(Press(harness.Window, Key.G).Handled);

            harness.SetModifiers(ModifierKeys.Control);
            Assert.True(Press(harness.Window, Key.G, repeat: true).Handled);

            Assert.Empty(harness.Editor.StartTexts);
            Assert.True(harness.Window.IsVisible);
        });
    }

    [Fact]
    public async Task A_failing_start_keeps_the_input_and_shows_the_reason_in_the_footer()
    {
        await RunAsync(harness =>
        {
            harness.ViewModel.Query = "keep\r\nme";
            ShowFocused(harness);
            harness.SetModifiers(ModifierKeys.Control);
            harness.Editor.StartBehavior = _ => Task.FromResult(new QueryEditorStartResult(false, Guid.Empty, "找不到记事本"));

            Press(harness.Window, Key.G);

            Assert.True(harness.Window.IsVisible);
            Assert.Equal("keep\r\nme", harness.ViewModel.Query);
            Assert.Null(harness.Window.EditingSessionId);
            Assert.Contains("找不到记事本", harness.ViewModel.ActionNotice);
            Assert.Equal(harness.ViewModel.ActionNotice, harness.ViewModel.DisplayFooterHint);
            Assert.Equal(Visibility.Collapsed, harness.Window.EditingButton.Visibility);

            // A service that throws instead of returning a failure is reported the same way.
            harness.Editor.StartBehavior = _ => throw new InvalidOperationException("boom");
            Press(harness.Window, Key.G);
            Assert.Contains("boom", harness.ViewModel.ActionNotice);
            Assert.Equal("keep\r\nme", harness.ViewModel.Query);
            Assert.Null(harness.Window.EditingSessionId);
        });
    }

    [Fact]
    public async Task Ctrl_G_during_an_active_session_does_not_start_another_and_says_so()
    {
        await RunAsync(harness =>
        {
            harness.ViewModel.Query = "draft";
            ShowFocused(harness);
            harness.SetModifiers(ModifierKeys.Control);
            Press(harness.Window, Key.G);
            var session = harness.Window.EditingSessionId;
            Assert.NotNull(session);
            Assert.False(harness.Window.IsVisible);

            // Back to the launcher: the service is asked to catch up and the session is still the same.
            ShowFocused(harness);
            Assert.Equal(1, harness.Editor.RefreshCount);
            harness.SetModifiers(ModifierKeys.Control);
            Press(harness.Window, Key.G);

            Assert.Equal(2, harness.Editor.StartTexts.Count);
            Assert.Equal(session, harness.Window.EditingSessionId);
            Assert.True(harness.Window.IsVisible);
            Assert.Contains("已有进行中的记事本编辑", harness.ViewModel.ActionNotice);
        });
    }

    [Fact]
    public async Task A_busy_start_ignores_a_second_request_and_text_typed_meanwhile_beats_the_late_save()
    {
        await RunAsync(harness =>
        {
            var pending = new TaskCompletionSource<QueryEditorStartResult>();
            harness.Editor.StartBehavior = _ => pending.Task;
            harness.ViewModel.Query = "first";
            ShowFocused(harness);
            harness.SetModifiers(ModifierKeys.Control);

            Press(harness.Window, Key.G);
            Press(harness.Window, Key.G);

            Assert.Equal(new[] { "first" }, harness.Editor.StartTexts);
            Assert.Contains("正在启动", harness.ViewModel.ActionNotice);

            // The user keeps typing while the editor starts; the draft was made from "first".
            harness.ViewModel.Query = "first and more";
            var session = Guid.NewGuid();
            harness.Editor.CurrentSessionId = session;
            pending.SetResult(new QueryEditorStartResult(true, session, null));

            Assert.Equal(session, harness.Window.EditingSessionId);
            Assert.False(harness.Window.IsVisible);

            // A save of that older draft must not overwrite what was typed in the meantime.
            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 1, "from notepad"));
            Assert.Equal("first and more", harness.ViewModel.Query);
            Assert.Equal("from notepad", harness.Window.PendingSavedText);
        });
    }

    [Fact]
    public async Task A_save_that_arrives_before_the_start_returns_is_applied_once_the_session_is_known()
    {
        await RunAsync(harness =>
        {
            var pending = new TaskCompletionSource<QueryEditorStartResult>();
            harness.Editor.StartBehavior = _ => pending.Task;
            harness.ViewModel.Query = "draft";
            ShowFocused(harness);
            harness.SetModifiers(ModifierKeys.Control);
            Press(harness.Window, Key.G);

            var session = Guid.NewGuid();
            harness.Editor.RaiseSaved(new QueryEditorSaved(Guid.NewGuid(), 7, "someone else"));
            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 1, "A\r\nB"));
            harness.Editor.RaiseSaved(new QueryEditorSaved(Guid.NewGuid(), 9, "another one"));

            // Not applied yet: nobody knows whose save this is until the start result arrives.
            Assert.Equal("draft", harness.ViewModel.Query);

            harness.Editor.CurrentSessionId = session;
            pending.SetResult(new QueryEditorStartResult(true, session, null));

            Assert.Equal(session, harness.Window.EditingSessionId);
            Assert.Equal("A\r\nB", harness.ViewModel.Query);
            Assert.Null(harness.Window.PendingSavedText);
        });
    }

    // ----- saves from the editor ------------------------------------------------------------------------

    [Fact]
    public async Task A_save_is_written_back_without_showing_activating_or_stealing_the_focus()
    {
        await RunAsync(harness =>
        {
            var session = StartSession(harness, "abc");

            // IsActive is not usable here: a window that was just hidden keeps reporting the old
            // activation until the message pump delivers the deactivation. Count activations instead.
            var activations = 0;
            harness.Window.Activated += (_, _) => activations++;

            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 1, "line1\r\nline2\r\nline3"));

            Assert.Equal("line1\r\nline2\r\nline3", harness.ViewModel.Query);
            Assert.Equal(harness.ViewModel.Query, harness.Window.SearchBox.Text);
            Assert.False(harness.Window.IsVisible);
            Assert.Equal(0, activations);
            Assert.Contains("已从记事本更新输入", harness.ViewModel.ActionNotice);
            Assert.Null(harness.Window.PendingSavedText);

            // The session keeps being tracked.
            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 2, "v2"));
            Assert.Equal("v2", harness.ViewModel.Query);
            Assert.Equal("v2", harness.Window.SearchBox.Text);
        });
    }

    [Fact]
    public async Task Saves_of_other_sessions_and_old_or_repeated_sequences_are_rejected()
    {
        await RunAsync(harness =>
        {
            var session = StartSession(harness, "abc");
            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 1, "v1"));
            Assert.Equal("v1", harness.ViewModel.Query);

            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 1, "repeated"));
            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 0, "older"));
            harness.Editor.RaiseSaved(new QueryEditorSaved(Guid.NewGuid(), 99, "other session"));
            Assert.Equal("v1", harness.ViewModel.Query);

            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 3, "v3"));
            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 2, "late v2"));
            Assert.Equal("v3", harness.ViewModel.Query);
        });
    }

    [Fact]
    public async Task A_save_after_the_user_typed_is_kept_not_applied_and_can_be_applied_explicitly()
    {
        await RunAsync(harness =>
        {
            var session = StartSession(harness, "abc");
            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 1, "v1"));

            // The user keeps typing in the launcher: from now on the editor must not overwrite it.
            harness.ViewModel.Query = "typed";
            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 2, "v2"));

            Assert.Equal("typed", harness.ViewModel.Query);
            Assert.Equal("v2", harness.Window.PendingSavedText);
            Assert.Contains("未覆盖", harness.ViewModel.ActionNotice);
            Assert.Equal(Visibility.Visible, harness.Window.EditingButton.Visibility);
            Assert.Contains("未应用", Assert.IsType<string>(harness.Window.EditingButton.Content));
            Assert.True(harness.Window.ApplySavedMenuItem.IsEnabled);

            // A newer save replaces the held one; the typed text is still untouched.
            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 3, "v3"));
            Assert.Equal("typed", harness.ViewModel.Query);
            Assert.Equal("v3", harness.Window.PendingSavedText);

            // The explicit action writes it back and puts the session in sync again.
            Assert.True(harness.Window.ApplyPendingSavedText());
            Assert.Equal("v3", harness.ViewModel.Query);
            Assert.Equal("v3", harness.Window.SearchBox.Text);
            Assert.Null(harness.Window.PendingSavedText);
            Assert.False(harness.Window.ApplySavedMenuItem.IsEnabled);
            Assert.False(harness.Window.ApplyPendingSavedText());

            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 4, "v4"));
            Assert.Equal("v4", harness.ViewModel.Query);

            // Typing the very text the editor saves is already in sync: nothing is held back.
            harness.ViewModel.Query = "x";
            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 5, "x"));
            Assert.Null(harness.Window.PendingSavedText);
            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 6, "v6"));
            Assert.Equal("v6", harness.ViewModel.Query);
        });
    }

    [Fact]
    public async Task Ending_the_edit_stops_the_session_and_leaves_the_users_notepad_alone()
    {
        await RunAsync(harness =>
        {
            var session = StartSession(harness, "abc");

            harness.Window.EndEditing();

            Assert.Equal(1, harness.Editor.EndSessionCount);
            Assert.False(harness.Editor.Disposed);
            Assert.Null(harness.Window.EditingSessionId);
            Assert.Equal(Visibility.Collapsed, harness.Window.EditingButton.Visibility);
            Assert.Contains("草稿文件已保留", harness.ViewModel.ActionNotice);

            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 1, "late"));
            Assert.Equal("abc", harness.ViewModel.Query);
        });
    }

    [Fact]
    public async Task A_service_failure_for_the_active_session_is_shown_and_stale_ones_are_ignored()
    {
        await RunAsync(harness =>
        {
            var session = StartSession(harness, "abc");

            harness.Editor.RaiseFailed(new QueryEditorError(Guid.NewGuid(), "stale"));
            Assert.Null(harness.ViewModel.ActionNotice);

            harness.Editor.RaiseFailed(new QueryEditorError(session, "记事本草稿文件已不存在"));
            Assert.Contains("记事本草稿文件已不存在", harness.ViewModel.ActionNotice);
            Assert.Contains("草稿文件已保留", harness.ViewModel.ActionNotice);
            Assert.Equal(session, harness.Window.EditingSessionId);
        });
    }

    [Fact]
    public async Task Showing_the_launcher_asks_an_active_session_to_catch_up_through_the_same_checks()
    {
        await RunAsync(harness =>
        {
            var session = StartSession(harness, "abc");
            harness.Editor.OnRefresh = () => harness.Editor.RaiseSaved(new QueryEditorSaved(session, 1, "caught up\r\nlines"));

            ShowFocused(harness);

            Assert.Equal(1, harness.Editor.RefreshCount);
            Assert.Equal("caught up\r\nlines", harness.ViewModel.Query);

            // If the user typed meanwhile the caught-up text is held back like any other save.
            harness.Window.HideLauncher();
            harness.ViewModel.Query = "typed";
            harness.Editor.OnRefresh = () => harness.Editor.RaiseSaved(new QueryEditorSaved(session, 2, "newer"));
            harness.Window.ShowLauncher();

            Assert.Equal(2, harness.Editor.RefreshCount);
            Assert.Equal("typed", harness.ViewModel.Query);
            Assert.Equal("newer", harness.Window.PendingSavedText);
        });
    }

    [Fact]
    public async Task A_session_the_service_already_ended_is_forgotten_and_without_a_session_nothing_is_refreshed()
    {
        await RunAsync(harness =>
        {
            ShowFocused(harness);
            Assert.Equal(0, harness.Editor.RefreshCount);

            StartSession(harness, "abc");
            harness.Editor.CurrentSessionId = null;
            harness.Window.ShowLauncher();

            Assert.Equal(0, harness.Editor.RefreshCount);
            Assert.Null(harness.Window.EditingSessionId);
            Assert.Equal(Visibility.Collapsed, harness.Window.EditingButton.Visibility);
        });
    }

    // ----- paste ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_multi_line_paste_replaces_the_selection_keeps_every_line_break_and_opens_the_editor()
    {
        await RunAsync(harness =>
        {
            harness.Window.SearchBox.Text = "ab-XY-cd";
            harness.Window.SearchBox.Select(3, 2);

            var args = Paste(harness.Window, "L1\r\nL2");

            const string merged = "ab-L1\r\nL2-cd";
            Assert.True(args.CommandCancelled, "the default single-line paste must be replaced");
            Assert.Equal(new[] { merged }, harness.Editor.StartTexts);
            Assert.Equal(merged, harness.ViewModel.Query);
            Assert.Equal(merged, harness.Window.SearchBox.Text);
            Assert.Equal(harness.Editor.CurrentSessionId, harness.Window.EditingSessionId);
            Assert.NotNull(harness.Window.EditingSessionId);
        });
    }

    [Fact]
    public async Task A_lone_line_feed_or_carriage_return_also_counts_and_is_never_dropped()
    {
        await RunAsync(harness =>
        {
            var lf = Paste(harness.Window, "x\ny");
            Assert.True(lf.CommandCancelled);
            Assert.Equal("x\ny", harness.Editor.StartTexts[0]);
            Assert.Equal(harness.Window.SearchBox.Text, harness.ViewModel.Query);
            Assert.Equal("x\ny", harness.ViewModel.Query.Replace("\r\n", "\n"));

            harness.Window.EndEditing();
            harness.Window.SearchBox.Text = string.Empty;
            var cr = Paste(harness.Window, "p\rq");
            Assert.True(cr.CommandCancelled);
            Assert.Equal("p\rq", harness.Editor.StartTexts[1]);
            Assert.Contains("p", harness.ViewModel.Query);
            Assert.Contains("q", harness.ViewModel.Query);
        });
    }

    [Fact]
    public async Task Single_line_non_text_and_dragged_data_keep_the_default_behaviour()
    {
        await RunAsync(harness =>
        {
            // The window was never laid out, so its text binding is not live yet: set both sides.
            harness.ViewModel.Query = "keep";
            harness.Window.SearchBox.Text = "keep";

            Assert.False(Paste(harness.Window, "just one line").CommandCancelled);
            Assert.False(Paste(harness.Window, "dropped\r\nlines", dragDrop: true).CommandCancelled);

            var files = new DataObject(DataFormats.FileDrop, new[] { @"C:\a.txt" });
            var args = new DataObjectPastingEventArgs(files, false, DataFormats.FileDrop) { RoutedEvent = DataObject.PastingEvent };
            harness.Window.SearchBox.RaiseEvent(args);
            Assert.False(args.CommandCancelled);

            Assert.Empty(harness.Editor.StartTexts);
            Assert.Equal("keep", harness.ViewModel.Query);
            Assert.Null(harness.Window.EditingSessionId);
        });
    }

    [Fact]
    public async Task A_busy_failing_or_existing_editor_never_costs_the_pasted_lines()
    {
        await RunAsync(harness =>
        {
            // Busy: the editor is still starting when the second multi-line paste arrives.
            var pending = new TaskCompletionSource<QueryEditorStartResult>();
            harness.Editor.StartBehavior = _ => pending.Task;
            Paste(harness.Window, "A\r\nB");
            Paste(harness.Window, "C\r\nD");

            Assert.Single(harness.Editor.StartTexts);
            Assert.Equal("A\r\nBC\r\nD", harness.ViewModel.Query);
            Assert.Contains("正在启动", harness.ViewModel.ActionNotice);

            var session = Guid.NewGuid();
            harness.Editor.CurrentSessionId = session;
            pending.SetResult(new QueryEditorStartResult(true, session, null));
            Assert.Equal(session, harness.Window.EditingSessionId);
            Assert.Equal("A\r\nBC\r\nD", harness.ViewModel.Query);

            // The second paste is not in the draft, so the session's first save is held, not applied.
            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 1, "A\r\nB"));
            Assert.Equal("A\r\nBC\r\nD", harness.ViewModel.Query);
            Assert.Equal("A\r\nB", harness.Window.PendingSavedText);
        });

        await RunAsync(harness =>
        {
            // Failing: the pasted lines stay in the input and the reason is visible.
            harness.Editor.StartBehavior = _ => Task.FromResult(new QueryEditorStartResult(false, Guid.Empty, "找不到记事本"));
            Paste(harness.Window, "A\r\nB");

            Assert.Equal("A\r\nB", harness.ViewModel.Query);
            Assert.Equal("A\r\nB", harness.Window.SearchBox.Text);
            Assert.Contains("找不到记事本", harness.ViewModel.ActionNotice);
            Assert.Null(harness.Window.EditingSessionId);
        });

        await RunAsync(harness =>
        {
            // Existing session: the service reports the same id; the new lines are kept, nothing is lost.
            Paste(harness.Window, "A\r\nB");
            var session = harness.Window.EditingSessionId;
            Assert.NotNull(session);

            Paste(harness.Window, "C\r\nD");

            Assert.Equal(session, harness.Window.EditingSessionId);
            Assert.Equal("A\r\nBC\r\nD", harness.ViewModel.Query);
            Assert.Contains("已有进行中的记事本编辑", harness.ViewModel.ActionNotice);
        });
    }

    // ----- closing ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Closing_the_window_releases_the_editor_without_ending_the_users_session()
    {
        await RunAsync(harness =>
        {
            var session = StartSession(harness, "abc");
            Assert.Equal(1, harness.Editor.SavedSubscribers);
            Assert.Equal(1, harness.Editor.FailedSubscribers);

            harness.CloseWindow();

            Assert.True(harness.Editor.Disposed);
            Assert.Equal(0, harness.Editor.EndSessionCount);
            Assert.Equal(0, harness.Editor.SavedSubscribers);
            Assert.Equal(0, harness.Editor.FailedSubscribers);

            // Late callbacks (including a direct call) change nothing and do not throw.
            harness.Editor.RaiseSaved(new QueryEditorSaved(session, 1, "late"));
            harness.Window.HandleEditorSaved(new QueryEditorSaved(session, 2, "later"));
            Assert.Equal("abc", harness.ViewModel.Query);
            Assert.Null(harness.Window.EditingSessionId);
        });
    }

    [Fact]
    public async Task A_start_that_finishes_after_the_window_closed_does_nothing()
    {
        await RunAsync(harness =>
        {
            var pending = new TaskCompletionSource<QueryEditorStartResult>();
            harness.Editor.StartBehavior = _ => pending.Task;
            harness.ViewModel.Query = "draft";
            ShowFocused(harness);
            harness.SetModifiers(ModifierKeys.Control);
            Press(harness.Window, Key.G);
            Assert.Single(harness.Editor.StartTexts);

            harness.CloseWindow();
            pending.SetResult(new QueryEditorStartResult(true, Guid.NewGuid(), null));

            Assert.Null(harness.Window.EditingSessionId);
            Assert.Equal("draft", harness.ViewModel.Query);
        });
    }

    // ----- hidden items manager ------------------------------------------------------------------------------

    [Fact]
    public async Task The_hidden_dialog_holds_back_the_focus_loss_hide_only_while_it_is_open()
    {
        await RunAsync(harness =>
        {
            var window = harness.Window;
            var seen = new List<bool>();
            window.HiddenWindowPresenter = _ =>
            {
                seen.Add(window.IsDeactivationSuppressed);

                // A second request while it is open must not stack another dialog.
                harness.ViewModel.ManageHiddenCommand.Execute(null);
                return null;
            };

            Assert.False(window.IsDeactivationSuppressed);
            harness.ViewModel.ManageHiddenCommand.Execute(null);

            Assert.Equal(new[] { true }, seen);
            Assert.False(window.IsDeactivationSuppressed);

            // Even a dialog that blows up releases the hold, and the manager can be opened again.
            window.HiddenWindowPresenter = _ => throw new InvalidOperationException("dialog failed");
            Assert.Throws<InvalidOperationException>(() => harness.ViewModel.ManageHiddenCommand.Execute(null));
            Assert.False(window.IsDeactivationSuppressed);

            window.HiddenWindowPresenter = _ =>
            {
                seen.Add(window.IsDeactivationSuppressed);
                return null;
            };
            harness.ViewModel.ManageHiddenCommand.Execute(null);
            Assert.Equal(new[] { true, true }, seen);
            Assert.False(window.IsDeactivationSuppressed);
        });
    }

    [Fact]
    public async Task The_hidden_window_lists_the_items_and_restores_one_or_all_of_them()
    {
        await RunAsync(harness =>
        {
            Assert.True(harness.Hidden.TryHide(Item(1)));
            Assert.True(harness.Hidden.TryHide(Item(2)));
            harness.Window.HiddenWindowPresenter = dialog =>
            {
                Assert.Equal(new[] { "item 2", "item 1" }, dialog.Rows.Select(row => row.Title));
                Assert.Equal(Item(2).Target, dialog.Rows[0].DetailText);
                Assert.Equal("恢复 item 2", dialog.Rows[0].RestoreAutomationName);
                Assert.True(dialog.RestoreAllButton.IsEnabled);
                Assert.Equal("恢复全部", AutomationProperties.GetName(dialog.RestoreAllButton));
                Assert.Equal(Visibility.Collapsed, dialog.EmptyText.Visibility);

                // A failed restore keeps the item listed and shows the reason inside the window.
                harness.Hidden.FailNext = "无法保存";
                Assert.False(dialog.RestoreOne(dialog.Rows[0].Key));
                Assert.Equal(2, dialog.Rows.Count);
                Assert.Equal(Visibility.Visible, dialog.ErrorBanner.Visibility);
                Assert.Contains("无法保存", dialog.ErrorText.Text);

                Assert.True(dialog.RestoreOne(dialog.Rows[0].Key));
                Assert.Equal(new[] { "item 1" }, dialog.Rows.Select(row => row.Title));
                Assert.Equal(1, harness.ViewModel.HiddenCount);

                Assert.True(dialog.RestoreAll());
                Assert.Empty(dialog.Rows);
                Assert.False(dialog.RestoreAllButton.IsEnabled);
                Assert.Equal(Visibility.Visible, dialog.EmptyText.Visibility);
                Assert.Equal(Visibility.Collapsed, dialog.ErrorBanner.Visibility);
                Assert.Equal(0, harness.ViewModel.HiddenCount);
                return null;
            };

            harness.ViewModel.ManageHiddenCommand.Execute(null);
        });
    }

    [Fact]
    public async Task The_footer_offers_the_hidden_manager_and_keeps_every_control_inside_the_window()
    {
        await RunAsync(harness =>
        {
            // Bindings only come alive once the content has been laid out.
            harness.Layout();
            Assert.Same(harness.ViewModel.ManageHiddenCommand, harness.Window.HiddenItemsButton.Command);
            Assert.Equal(Visibility.Collapsed, harness.Window.EditingButton.Visibility);

            // With an editing session the footer is at its fullest.
            StartSession(harness, "abc");
            harness.Window.ShowLauncher();
            harness.Window.UpdateLayout();

            var window = harness.Window;
            var controls = new FrameworkElement[]
            {
                window.EditingButton,
                window.HiddenItemsButton,
                window.ThemeSelector,
                FindButton(window, "退出"),
            };
            Rect previous = default;
            foreach (var control in controls)
            {
                Assert.True(control.ActualWidth > 0, $"{control.GetType().Name} has no width");
                var bounds = BoundsIn(window, control);
                Assert.True(bounds.Right <= window.ActualWidth + 1.5, $"{control.GetType().Name} sticks out of the window");
                Assert.True(bounds.Bottom <= window.ActualHeight + 1.5);
                Assert.True(bounds.Left >= previous.Right - 1.5, "footer controls must not overlap");
                previous = bounds;
            }

            // The notice column still has room for a readable message.
            Assert.True(window.FooterHintText.ActualWidth >= 120, $"the notice column is only {window.FooterHintText.ActualWidth} wide");
        });
    }

    // ----- harness ----------------------------------------------------------------------------------------------

    private static SearchResultItem Item(int number) => new()
    {
        Id = $"app|{number}",
        Kind = ResultKind.Application,
        Title = $"item {number}",
        TypeLabel = "应用",
        Target = $@"C:\fake\item-{number}.exe",
        Score = 1_000 - number,
    };

    private sealed class OpenState
    {
        public bool Succeeds { get; set; } = true;

        public List<string> Targets { get; } = new();
    }

    private sealed class Harness
    {
        public required MainWindow Window { get; init; }

        public required LauncherViewModel ViewModel { get; init; }

        public required FakeQueryEditor Editor { get; init; }

        public required FakePreviewService Preview { get; init; }

        public required FakeHiddenStore Hidden { get; init; }

        public required MutableAppProvider Apps { get; init; }

        public required FakeHistory History { get; init; }

        public required OpenState Open { get; init; }

        public bool WindowClosed { get; private set; }

        public void SetModifiers(ModifierKeys modifiers) => Window.ModifierProvider = () => modifiers;

        /// <summary>Runs a real application-scope search so the rows come from the normal pipeline.</summary>
        public void SetRows(int count)
        {
            Apps.Items = Enumerable.Range(1, count).Select(Item).ToArray();
            ViewModel.Query = "item";
            ViewModel.SetScope(SearchScope.Applications);
            Assert.Equal(count, ViewModel.Results.Count);
        }

        /// <summary>Like <see cref="SetRows"/> and realizes the row containers of the never-shown window.</summary>
        public void ShowRows(int count)
        {
            SetRows(count);
            Layout();
        }

        /// <summary>
        /// Lays out the never-shown window's content, which is what activates its data bindings (the
        /// window has no template, and so no visual tree, until it is shown).
        /// </summary>
        public void Layout()
        {
            Window.Measure(RealizeBounds);
            Window.Arrange(new Rect(RealizeBounds));
            Window.UpdateLayout();
            if (Window.Content is FrameworkElement content)
            {
                content.Measure(RealizeBounds);
                content.Arrange(new Rect(RealizeBounds));
                content.UpdateLayout();
            }
        }

        public void CloseWindow()
        {
            if (WindowClosed)
                return;

            WindowClosed = true;
            Window.AllowClose();
            Window.Close();
        }
    }

    private static async Task RunAsync(Action<Harness> body)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            Harness? harness = null;
            try
            {
                var apps = new MutableAppProvider();
                var hidden = new FakeHiddenStore();
                var history = new FakeHistory();
                var preview = new FakePreviewService();
                var editor = new FakeQueryEditor();
                var open = new OpenState();

                using var viewModel = new LauncherViewModel(
                    new SearchController(apps, new NoopFileProvider()),
                    history,
                    Dispatcher.CurrentDispatcher,
                    iconLoader: (_, _, _) => null,
                    iconRetryDelay: TimeSpan.Zero,
                    openTarget: item =>
                    {
                        open.Targets.Add(item.Target);
                        return open.Succeeds;
                    },
                    copyTarget: _ => true,
                    hiddenResults: hidden,
                    previewService: preview
                );

                var window = new MainWindow(viewModel, explorerFolderResolver: _ => null, queryEditor: editor);
                harness = new Harness
                {
                    Window = window,
                    ViewModel = viewModel,
                    Editor = editor,
                    Preview = preview,
                    Hidden = hidden,
                    Apps = apps,
                    History = history,
                    Open = open,
                };

                try
                {
                    body(harness);
                }
                finally
                {
                    harness.CloseWindow();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "R1ng window action test",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var completed = await Task.Run(() => done.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(completed, "the window action test timed out");
        Assert.Null(failure);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    private static void ShowFocused(Harness harness)
    {
        harness.Window.ShowLauncher();
        harness.Window.SearchBox.Focus();
        Keyboard.Focus(harness.Window.SearchBox);
        Assert.True(harness.Window.SearchBox.IsKeyboardFocusWithin, "the search box could not take keyboard focus");
    }

    /// <summary>Starts a session through the real Ctrl+G route and returns its id (the launcher is hidden afterwards).</summary>
    private static Guid StartSession(Harness harness, string query)
    {
        harness.ViewModel.Query = query;
        ShowFocused(harness);
        harness.SetModifiers(ModifierKeys.Control);
        Press(harness.Window, Key.G);

        var session = harness.Window.EditingSessionId;
        Assert.NotNull(session);
        Assert.False(harness.Window.IsVisible);
        harness.SetModifiers(ModifierKeys.None);
        return session!.Value;
    }

    private static KeyEventArgs Press(MainWindow window, Key key, bool repeat = false, bool imeProcessed = false)
    {
        var source = PresentationSource.FromVisual(window);
        Assert.NotNull(source);

        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source!, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        if (repeat)
            IsRepeatField.SetValue(args, true);
        if (imeProcessed)
        {
            ReportedKeyField.SetValue(args, Key.ImeProcessed);
            Assert.Equal(Key.ImeProcessed, args.Key);
            Assert.Equal(key, args.ImeProcessedKey);
        }

        window.RaiseEvent(args);
        return args;
    }

    /// <summary>
    /// Raises the real tunnelling mouse-down event (<see cref="Mouse.PreviewMouseDownEvent"/>) on the
    /// element that was "hit". Like real input it is delivered to every element on the way down, and
    /// WPF's own class handlers re-raise it on each of them as the direct Preview{Left,Right}ButtonDown
    /// event with the hit element kept as <c>OriginalSource</c>. Raising the direct event on the hit
    /// element itself would never reach the list, which is not how the mouse is routed.
    /// </summary>
    private static bool Click(UIElement hit, MouseButton button, int clickCount)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
        };
        ClickCountProperty.SetValue(args, clickCount);

        hit.RaiseEvent(args);
        return args.Handled;
    }

    private static DataObjectPastingEventArgs Paste(MainWindow window, string text, bool dragDrop = false)
    {
        var args = new DataObjectPastingEventArgs(
            new DataObject(DataFormats.UnicodeText, text),
            dragDrop,
            DataFormats.UnicodeText
        )
        {
            RoutedEvent = DataObject.PastingEvent,
        };

        window.SearchBox.RaiseEvent(args);
        return args;
    }

    /// <summary>A text element inside the realized row container: what a mouse over the row really hits.</summary>
    private static UIElement HitOf(Harness harness, int index)
    {
        var container = Assert.IsType<ListBoxItem>(harness.Window.ResultsList.ItemContainerGenerator.ContainerFromIndex(index));
        return Descendants(container).OfType<TextBlock>().First();
    }

    private static Button FindButton(MainWindow window, string content) =>
        Descendants(window).OfType<Button>().First(button => button.Content as string == content);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;

            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }

    private static Rect BoundsIn(Visual ancestor, FrameworkElement element) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    /// <summary>Scriptable editor: records every call, never starts a process and never touches the disk.</summary>
    private sealed class FakeQueryEditor : IQueryEditor
    {
        private Action<QueryEditorSaved>? _saved;
        private Action<QueryEditorError>? _failed;

        public Guid? CurrentSessionId { get; set; }

        public string? DraftPath { get; set; } = @"C:\fake\drafts\r1ng-test.txt";

        public List<string> StartTexts { get; } = new();

        public int RefreshCount { get; private set; }

        public int EndSessionCount { get; private set; }

        public bool Disposed { get; private set; }

        /// <summary>Replaces the default start (new session, or the active one when there is one).</summary>
        public Func<string, Task<QueryEditorStartResult>>? StartBehavior { get; set; }

        /// <summary>Runs inside <see cref="RefreshAsync"/>, to make the service report a save.</summary>
        public Action? OnRefresh { get; set; }

        public int SavedSubscribers => _saved?.GetInvocationList().Length ?? 0;

        public int FailedSubscribers => _failed?.GetInvocationList().Length ?? 0;

        public event Action<QueryEditorSaved>? Saved
        {
            add => _saved += value;
            remove => _saved -= value;
        }

        public event Action<QueryEditorError>? Failed
        {
            add => _failed += value;
            remove => _failed -= value;
        }

        public Task<QueryEditorStartResult> StartAsync(string text, CancellationToken cancellationToken = default)
        {
            StartTexts.Add(text);
            if (StartBehavior is not null)
                return StartBehavior(text);

            if (CurrentSessionId is { } active)
                return Task.FromResult(new QueryEditorStartResult(true, active, null));

            var id = Guid.NewGuid();
            CurrentSessionId = id;
            return Task.FromResult(new QueryEditorStartResult(true, id, null));
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            OnRefresh?.Invoke();
            return Task.CompletedTask;
        }

        public void EndSession()
        {
            EndSessionCount++;
            CurrentSessionId = null;
        }

        public void Dispose()
        {
            Disposed = true;
            CurrentSessionId = null;
        }

        public void RaiseSaved(QueryEditorSaved saved) => _saved?.Invoke(saved);

        public void RaiseFailed(QueryEditorError error) => _failed?.Invoke(error);
    }
}
