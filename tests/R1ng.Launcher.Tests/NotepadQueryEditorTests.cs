using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading.Channels;
using R1ng.Launcher.Platform;

namespace R1ng.Launcher.Tests;

/// <summary>
/// The real Notepad is never started: every editor gets an injected launcher that only records the
/// draft path, and "saving in Notepad" is simulated by writing that same file.
/// </summary>
public sealed class NotepadQueryEditorTests : IDisposable
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding Utf8Bom = new(true);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "r1ng-editor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<NotepadQueryEditor> _editors = new();

    public void Dispose()
    {
        foreach (var editor in _editors)
            editor.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class Harness
    {
        public NotepadQueryEditor Editor { get; set; } = null!;

        public ConcurrentQueue<string> Launches { get; } = new();

        public Channel<QueryEditorSaved> Saved { get; } = Channel.CreateUnbounded<QueryEditorSaved>();

        public Channel<QueryEditorError> Failed { get; } = Channel.CreateUnbounded<QueryEditorError>();
    }

    private Harness Create(TimeSpan? poll = null, Func<string, bool>? launch = null)
    {
        var harness = new Harness();
        var editor = new NotepadQueryEditor(
            _directory,
            path =>
            {
                harness.Launches.Enqueue(path);
                return launch?.Invoke(path) ?? true;
            },
            poll ?? Fast);
        editor.Saved += saved => harness.Saved.Writer.TryWrite(saved);
        editor.Failed += error => harness.Failed.Writer.TryWrite(error);
        harness.Editor = editor;
        _editors.Add(editor);
        return harness;
    }

    private static async Task<T> NextAsync<T>(Channel<T> channel)
    {
        using var timeout = new CancellationTokenSource(Wait);
        return await channel.Reader.ReadAsync(timeout.Token);
    }

    private static async Task AssertQuietAsync<T>(Channel<T> channel, int milliseconds = 300)
    {
        await Task.Delay(milliseconds);
        Assert.False(channel.Reader.TryRead(out _), "no notification was expected");
    }

    private static void SaveInEditor(string path, string text) =>
        File.WriteAllBytes(path, Utf8Bom.GetPreamble().Concat(Utf8NoBom.GetBytes(text)).ToArray());

    [Fact]
    public async Task Start_writes_a_utf8_draft_launches_the_editor_with_it_and_reports_the_session()
    {
        var harness = Create(Idle);
        const string text = "第一行 first\r\nsecond line\nthird\rlast";

        var result = await harness.Editor.StartAsync(text);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.NotEqual(Guid.Empty, result.SessionId);
        Assert.Equal(result.SessionId, harness.Editor.CurrentSessionId);

        var path = Assert.Single(harness.Launches);
        Assert.Equal(path, harness.Editor.DraftPath);
        Assert.Equal(_directory, Path.GetDirectoryName(path));
        Assert.Equal(".txt", Path.GetExtension(path));
        Assert.Equal(NotepadQueryEditor.DraftFileName(result.SessionId), Path.GetFileName(path));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Equal(text, Utf8NoBom.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task Empty_text_starts_a_session_with_an_empty_draft()
    {
        var harness = Create(Idle);

        var result = await harness.Editor.StartAsync(string.Empty);

        Assert.True(result.Success);
        var path = Assert.Single(harness.Launches);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_new_editor_does_nothing_until_started()
    {
        using var editor = new NotepadQueryEditor();

        Assert.Null(editor.CurrentSessionId);
        Assert.Null(editor.DraftPath);
        editor.EndSession();
    }

    [Fact]
    public void Defaults_are_the_trusted_system_notepad_and_a_launcher_owned_temp_folder()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"),
            NotepadQueryEditor.NotepadPath);
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "r1ng-launcher", "query-editor"),
            NotepadQueryEditor.DefaultDirectory);
    }

    [Fact]
    public void Constructor_rejects_unusable_poll_intervals()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NotepadQueryEditor(_directory, _ => true, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NotepadQueryEditor(_directory, _ => true, TimeSpan.FromMilliseconds(-5)));
    }

    [Fact]
    public async Task Starting_again_during_an_active_session_reuses_it_without_touching_anything()
    {
        var harness = Create(Idle);
        var first = await harness.Editor.StartAsync("original");
        var path = harness.Editor.DraftPath!;
        var before = File.ReadAllBytes(path);

        var second = await harness.Editor.StartAsync("something completely different");

        Assert.True(second.Success);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Single(harness.Launches);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Concurrent_starts_are_serialised_into_one_session_and_one_launch()
    {
        var harness = Create(Idle, launch: _ =>
        {
            Thread.Sleep(150); // long enough that every caller is already waiting
            return true;
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => harness.Editor.StartAsync("same")));

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Single(results.Select(r => r.SessionId).Distinct());
        Assert.Single(harness.Launches);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task The_initial_content_is_a_baseline_not_a_save()
    {
        var harness = Create();
        await harness.Editor.StartAsync("baseline text");

        await AssertQuietAsync(harness.Saved, 400);
        await harness.Editor.RefreshAsync();
        await AssertQuietAsync(harness.Saved, 100);
        await AssertQuietAsync(harness.Failed, 50);
    }

    [Fact]
    public async Task A_save_is_reported_with_its_session_and_an_increasing_sequence()
    {
        var harness = Create();
        var started = await harness.Editor.StartAsync("old");
        var path = harness.Editor.DraftPath!;

        SaveInEditor(path, "first edit");
        var first = await NextAsync(harness.Saved);
        SaveInEditor(path, "second edit");
        var second = await NextAsync(harness.Saved);

        Assert.Equal(new QueryEditorSaved(started.SessionId, 1, "first edit"), first);
        Assert.Equal(new QueryEditorSaved(started.SessionId, 2, "second edit"), second);
    }

    [Fact]
    public async Task A_rewrite_of_the_same_size_with_unchanged_file_times_is_still_detected()
    {
        // File times are coarse: a quick save can leave size, write time and creation time identical.
        var harness = Create();
        await harness.Editor.StartAsync("old");
        var path = harness.Editor.DraftPath!;
        var written = File.GetLastWriteTimeUtc(path);
        var created = File.GetCreationTimeUtc(path);

        SaveInEditor(path, "new"); // same byte length as "old"
        File.SetLastWriteTimeUtc(path, written);
        File.SetCreationTimeUtc(path, created);

        Assert.Equal("new", (await NextAsync(harness.Saved)).Text);
    }

    [Fact]
    public async Task Saving_the_same_text_again_is_not_a_new_save()
    {
        var harness = Create();
        await harness.Editor.StartAsync("old");
        var path = harness.Editor.DraftPath!;

        SaveInEditor(path, "edited");
        await NextAsync(harness.Saved);
        SaveInEditor(path, "edited");
        await AssertQuietAsync(harness.Saved);
        SaveInEditor(path, "edited again");

        Assert.Equal(2, (await NextAsync(harness.Saved)).Sequence);
    }

    [Theory]
    [InlineData("a\r\nb\r\nc")]
    [InlineData("a\nb\nc")]
    [InlineData("a\rb\rc")]
    [InlineData("a\r\nb\nc\rd")]
    [InlineData("trailing newline\r\n")]
    [InlineData("\r\n")]
    [InlineData("")]
    [InlineData("  spaces  and\ttabs  ")]
    [InlineData("路径 \\\\server\\共享 😀")]
    public async Task Saved_text_keeps_every_line_ending_and_whitespace_exactly(string text)
    {
        var harness = Create();
        await harness.Editor.StartAsync("something else");

        SaveInEditor(harness.Editor.DraftPath!, text);

        Assert.Equal(text, (await NextAsync(harness.Saved)).Text);
    }

    [Fact]
    public async Task A_zero_byte_file_is_empty_text()
    {
        var harness = Create();
        await harness.Editor.StartAsync("not empty");

        File.WriteAllBytes(harness.Editor.DraftPath!, Array.Empty<byte>());

        Assert.Equal(string.Empty, (await NextAsync(harness.Saved)).Text);
    }

    [Fact]
    public async Task Utf8_without_a_bom_is_decoded_strictly_as_utf8()
    {
        var harness = Create();
        await harness.Editor.StartAsync("x");

        File.WriteAllBytes(harness.Editor.DraftPath!, Utf8NoBom.GetBytes("中文 text\nline"));

        Assert.Equal("中文 text\nline", (await NextAsync(harness.Saved)).Text);
    }

    [Fact]
    public async Task Utf16_with_a_bom_is_recognised_in_both_byte_orders()
    {
        var harness = Create();
        await harness.Editor.StartAsync("x");
        var path = harness.Editor.DraftPath!;

        File.WriteAllBytes(path, new UnicodeEncoding(false, true).GetPreamble().Concat(new UnicodeEncoding(false, false).GetBytes("小端 LE\r\nok")).ToArray());
        Assert.Equal("小端 LE\r\nok", (await NextAsync(harness.Saved)).Text);

        File.WriteAllBytes(path, new UnicodeEncoding(true, true).GetPreamble().Concat(new UnicodeEncoding(true, false).GetBytes("大端 BE\nok")).ToArray());
        Assert.Equal("大端 BE\nok", (await NextAsync(harness.Saved)).Text);
    }

    [Fact]
    public async Task Undecodable_bytes_are_reported_once_keep_the_file_and_a_later_valid_save_recovers()
    {
        var harness = Create();
        var started = await harness.Editor.StartAsync("x");
        var path = harness.Editor.DraftPath!;
        var invalid = new byte[] { 0x61, 0xC3, 0x28, 0xFF, 0x62 }; // not valid UTF-8, no BOM

        File.WriteAllBytes(path, invalid);
        var error = await NextAsync(harness.Failed);

        Assert.Equal(started.SessionId, error.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(invalid, File.ReadAllBytes(path));
        await AssertQuietAsync(harness.Failed, 400); // not repeated on every poll
        await AssertQuietAsync(harness.Saved, 10);

        SaveInEditor(path, "fixed");
        Assert.Equal("fixed", (await NextAsync(harness.Saved)).Text);

        // After recovery the same kind of problem is news again.
        File.WriteAllBytes(path, invalid);
        await NextAsync(harness.Failed);
    }

    [Fact]
    public async Task An_oversized_draft_is_reported_once_kept_and_recoverable()
    {
        var harness = Create();
        await harness.Editor.StartAsync("x");
        var path = harness.Editor.DraftPath!;

        File.WriteAllBytes(path, new byte[NotepadQueryEditor.MaxDraftBytes + 1].Select(_ => (byte)'a').ToArray());
        var error = await NextAsync(harness.Failed);

        Assert.Contains("1 MiB", error.Message);
        Assert.True(File.Exists(path));
        await AssertQuietAsync(harness.Failed, 300);

        SaveInEditor(path, "small again");
        Assert.Equal("small again", (await NextAsync(harness.Saved)).Text);
    }

    [Fact]
    public async Task A_draft_exactly_at_the_limit_is_still_read()
    {
        var harness = Create();
        await harness.Editor.StartAsync("x");

        File.WriteAllBytes(harness.Editor.DraftPath!, Enumerable.Repeat((byte)'a', NotepadQueryEditor.MaxDraftBytes).ToArray());

        var saved = await NextAsync(harness.Saved);
        Assert.Equal(NotepadQueryEditor.MaxDraftBytes, saved.Text.Length);
    }

    [Fact]
    public async Task A_brief_sharing_violation_is_retried_silently_and_the_save_still_arrives()
    {
        var harness = Create(Idle); // drive every poll through RefreshAsync for a deterministic test
        await harness.Editor.StartAsync("old");
        var path = harness.Editor.DraftPath!;
        SaveInEditor(path, "while locked");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await harness.Editor.RefreshAsync(); // still locked: bounded retries, then give up quietly
            Assert.False(harness.Failed.Reader.TryRead(out _));
            Assert.False(harness.Saved.Reader.TryRead(out _));
        }

        await harness.Editor.RefreshAsync();

        Assert.Equal("while locked", (await NextAsync(harness.Saved)).Text);
        Assert.False(harness.Failed.Reader.TryRead(out _));
    }

    [Fact]
    public async Task A_lasting_sharing_violation_is_reported_once_and_reading_resumes_afterwards()
    {
        var harness = Create(Idle);
        await harness.Editor.StartAsync("old");
        var path = harness.Editor.DraftPath!;
        SaveInEditor(path, "after the lock");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            for (var i = 0; i < 12; i++)
                await harness.Editor.RefreshAsync();
        }

        Assert.NotNull((await NextAsync(harness.Failed)).Message);
        Assert.False(harness.Failed.Reader.TryRead(out _)); // 12 failed polls, still only one report
        Assert.True(File.Exists(path));

        await harness.Editor.RefreshAsync();
        Assert.Equal("after the lock", (await NextAsync(harness.Saved)).Text);
    }

    [Fact]
    public async Task A_save_that_replaces_the_file_is_detected()
    {
        var harness = Create();
        await harness.Editor.StartAsync("old");
        var path = harness.Editor.DraftPath!;

        // Typical atomic-save style: write a sibling, then swap it over the draft.
        var sibling = path + ".new";
        File.WriteAllBytes(sibling, Utf8Bom.GetPreamble().Concat(Utf8NoBom.GetBytes("replaced")).ToArray());
        File.Move(sibling, path, overwrite: true);

        Assert.Equal("replaced", (await NextAsync(harness.Saved)).Text);
    }

    [Fact]
    public async Task A_deleted_and_recreated_draft_keeps_being_monitored()
    {
        var harness = Create();
        await harness.Editor.StartAsync("old");
        var path = harness.Editor.DraftPath!;

        File.Delete(path);
        await Task.Delay(100); // shorter than the point at which a missing file becomes an error
        SaveInEditor(path, "back again");

        Assert.Equal("back again", (await NextAsync(harness.Saved)).Text);
    }

    [Fact]
    public async Task RefreshAsync_picks_up_a_save_the_timer_has_not_seen_yet()
    {
        var harness = Create(Idle);
        var started = await harness.Editor.StartAsync("old");
        SaveInEditor(harness.Editor.DraftPath!, "saved while hidden");

        await harness.Editor.RefreshAsync();

        Assert.True(harness.Saved.Reader.TryRead(out var saved));
        Assert.Equal(new QueryEditorSaved(started.SessionId, 1, "saved while hidden"), saved);

        await harness.Editor.RefreshAsync();
        Assert.False(harness.Saved.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RefreshAsync_without_a_session_or_with_a_cancelled_token_completes_quietly()
    {
        var harness = Create(Idle);
        await harness.Editor.RefreshAsync();

        await harness.Editor.StartAsync("x");
        SaveInEditor(harness.Editor.DraftPath!, "y");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await harness.Editor.RefreshAsync(cancellation.Token);

        Assert.False(harness.Saved.Reader.TryRead(out _));
        await harness.Editor.RefreshAsync(); // the save is still there to be found
        Assert.True(harness.Saved.Reader.TryRead(out _));
    }

    [Fact]
    public async Task EndSession_stops_events_forgets_the_session_but_keeps_the_draft()
    {
        var harness = Create();
        await harness.Editor.StartAsync("old");
        var path = harness.Editor.DraftPath!;

        harness.Editor.EndSession();

        Assert.Null(harness.Editor.CurrentSessionId);
        Assert.Null(harness.Editor.DraftPath);
        Assert.True(File.Exists(path), "the user's editor tab may still be open on this file");

        SaveInEditor(path, "late save");
        File.WriteAllBytes(path, new byte[] { 0xC3, 0x28 });
        await harness.Editor.RefreshAsync();
        await AssertQuietAsync(harness.Saved, 400);
        await AssertQuietAsync(harness.Failed, 10);
        harness.Editor.EndSession(); // idempotent
    }

    [Fact]
    public async Task A_new_session_after_EndSession_gets_a_fresh_id_and_file_and_ignores_the_old_one()
    {
        var harness = Create();
        var first = await harness.Editor.StartAsync("one");
        var firstPath = harness.Editor.DraftPath!;
        harness.Editor.EndSession();

        var second = await harness.Editor.StartAsync("two");
        var secondPath = harness.Editor.DraftPath!;

        Assert.True(second.Success);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.NotEqual(firstPath, secondPath);
        Assert.Equal(2, harness.Launches.Count);

        SaveInEditor(firstPath, "write into the old draft");
        await AssertQuietAsync(harness.Saved, 300);

        SaveInEditor(secondPath, "write into the new draft");
        var saved = await NextAsync(harness.Saved);
        Assert.Equal(new QueryEditorSaved(second.SessionId, 1, "write into the new draft"), saved);
    }

    [Fact]
    public async Task Dispose_stops_events_keeps_the_draft_and_rejects_new_sessions()
    {
        var harness = Create();
        await harness.Editor.StartAsync("old");
        var path = harness.Editor.DraftPath!;

        harness.Editor.Dispose();
        harness.Editor.Dispose(); // idempotent

        SaveInEditor(path, "after dispose");
        await AssertQuietAsync(harness.Saved, 300);
        Assert.True(File.Exists(path));
        Assert.Null(harness.Editor.CurrentSessionId);

        var result = await harness.Editor.StartAsync("again");
        Assert.False(result.Success);
        Assert.Equal(Guid.Empty, result.SessionId);
        Assert.Single(harness.Launches);
        await harness.Editor.RefreshAsync();
        harness.Editor.EndSession();
    }

    [Fact]
    public async Task A_launcher_that_reports_failure_leaves_no_file_and_no_session()
    {
        var harness = Create(launch: _ => false);

        var result = await harness.Editor.StartAsync("text");

        Assert.False(result.Success);
        Assert.Equal(Guid.Empty, result.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Null(harness.Editor.CurrentSessionId);
        Assert.Null(harness.Editor.DraftPath);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task A_launcher_that_throws_is_reported_not_propagated_and_can_be_retried()
    {
        var attempts = 0;
        var harness = Create(launch: _ => Interlocked.Increment(ref attempts) == 1 ? throw new InvalidOperationException("boom") : true);

        var failed = await harness.Editor.StartAsync("text");
        Assert.False(failed.Success);
        Assert.Empty(Directory.GetFiles(_directory));

        var retried = await harness.Editor.StartAsync("text");
        Assert.True(retried.Success);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task A_cancelled_start_creates_nothing_and_launches_nothing()
    {
        var harness = Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await harness.Editor.StartAsync("text", cancellation.Token);

        Assert.False(result.Success);
        Assert.Empty(harness.Launches);
        Assert.False(Directory.Exists(_directory) && Directory.GetFiles(_directory).Length > 0);
        Assert.Null(harness.Editor.CurrentSessionId);
    }

    [Fact]
    public async Task Text_that_cannot_be_written_as_utf8_or_is_too_large_is_refused_up_front()
    {
        var harness = Create();

        var lone = await harness.Editor.StartAsync("bad" + '\uD800' + "text");
        var huge = await harness.Editor.StartAsync(new string('x', NotepadQueryEditor.MaxDraftBytes));

        Assert.False(lone.Success);
        Assert.False(huge.Success);
        Assert.Empty(harness.Launches);
        Assert.Null(harness.Editor.CurrentSessionId);
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_stop_other_subscribers_or_later_saves()
    {
        var harness = Create();
        harness.Editor.Saved += _ => throw new InvalidOperationException("faulty subscriber");
        harness.Editor.Failed += _ => throw new InvalidOperationException("faulty subscriber");
        await harness.Editor.StartAsync("old");
        var path = harness.Editor.DraftPath!;

        SaveInEditor(path, "one");
        Assert.Equal("one", (await NextAsync(harness.Saved)).Text);
        File.WriteAllBytes(path, new byte[] { 0xC3, 0x28 });
        await NextAsync(harness.Failed);
        SaveInEditor(path, "two");
        Assert.Equal("two", (await NextAsync(harness.Saved)).Text);
    }

    [Fact]
    public async Task Handed_over_drafts_are_never_deleted_whatever_their_age()
    {
        // A Windows 11 Notepad tab can stay open for weeks without holding the file, so age says
        // nothing about whether a draft was abandoned.
        Directory.CreateDirectory(_directory);
        var old = DateTime.UtcNow - TimeSpan.FromDays(60);
        var leftovers = new[]
        {
            Path.Combine(_directory, NotepadQueryEditor.DraftFileName(Guid.NewGuid())),
            Path.Combine(_directory, NotepadQueryEditor.DraftFileName(Guid.NewGuid())),
            Path.Combine(_directory, "notes.txt"),
        };
        foreach (var file in leftovers)
        {
            File.WriteAllText(file, "from an earlier session");
            File.SetCreationTimeUtc(file, old);
            File.SetLastWriteTimeUtc(file, old);
        }

        var harness = Create();
        var first = await harness.Editor.StartAsync("one");
        var firstPath = harness.Editor.DraftPath!;
        harness.Editor.EndSession();
        var second = await harness.Editor.StartAsync("two");
        var secondPath = harness.Editor.DraftPath!;
        await Task.Delay(300);
        harness.Editor.Dispose();
        await Task.Delay(100);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.All(leftovers, file => Assert.True(File.Exists(file), file));
        Assert.True(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
        Assert.Equal(5, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public async Task A_failed_start_removes_only_its_own_untransferred_draft()
    {
        Directory.CreateDirectory(_directory);
        var earlier = Path.Combine(_directory, NotepadQueryEditor.DraftFileName(Guid.NewGuid()));
        File.WriteAllText(earlier, "handed over earlier");
        var harness = Create(launch: _ => false);

        var result = await harness.Editor.StartAsync("text");

        Assert.False(result.Success);
        Assert.Equal(new[] { earlier }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task A_save_of_a_large_draft_with_unchanged_size_and_file_times_is_still_detected()
    {
        // Same protection as for small drafts: content decides, not size or timestamps.
        var harness = Create();
        var original = new string('a', 300 * 1024);
        await harness.Editor.StartAsync(original);
        var path = harness.Editor.DraftPath!;
        var written = File.GetLastWriteTimeUtc(path);
        var created = File.GetCreationTimeUtc(path);

        var changed = "b" + original[1..];
        SaveInEditor(path, changed);
        File.SetLastWriteTimeUtc(path, written);
        File.SetCreationTimeUtc(path, created);

        Assert.Equal(changed, (await NextAsync(harness.Saved)).Text);
    }
}
