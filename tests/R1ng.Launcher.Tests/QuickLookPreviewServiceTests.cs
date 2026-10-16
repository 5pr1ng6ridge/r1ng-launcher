using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using R1ng.Launcher.Core;
using R1ng.Launcher.Platform;

namespace R1ng.Launcher.Tests;

/// <summary>
/// Every test that sends a request uses its own randomly named pipe with an in-test server. None of
/// them may ever talk to a QuickLook that happens to be running on the machine.
/// </summary>
public sealed class QuickLookPreviewServiceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "r1ng-quicklook-tests-" + Guid.NewGuid().ToString("N"));

    public QuickLookPreviewServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static SearchResultItem Item(string target, bool folder = false) => new()
    {
        Id = "test-row",
        Kind = folder ? ResultKind.Folder : ResultKind.File,
        Title = "test",
        TypeLabel = folder ? "文件夹" : "文件",
        Target = target,
        IsFolder = folder,
    };

    private static string NewPipeName() => "r1ng-test-quicklook-" + Guid.NewGuid().ToString("N");

    private static QuickLookPreviewService Service(string pipeName, int timeoutMilliseconds = 3000) =>
        new(pipeName, TimeSpan.FromMilliseconds(timeoutMilliseconds));

    /// <summary>A one-shot byte-mode pipe server that collects everything a client writes until it disconnects.</summary>
    private sealed class FakeQuickLook : IDisposable
    {
        private readonly NamedPipeServerStream _server;

        public FakeQuickLook()
        {
            Name = NewPipeName();
            _server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Received = ReceiveAsync();
        }

        public string Name { get; }

        public Task<byte[]> Received { get; }

        public bool Connected => _server.IsConnected;

        private async Task<byte[]> ReceiveAsync()
        {
            try
            {
                await _server.WaitForConnectionAsync();
                using var buffer = new MemoryStream();
                await _server.CopyToAsync(buffer);
                return buffer.ToArray();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException)
            {
                // The test disposed the server while it was still waiting; keep the task observed.
                return Array.Empty<byte>();
            }
        }

        public void Dispose() => _server.Dispose();
    }

    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"Z:\definitely\not\there\file.xyz")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Users\someone\Desktop\App.lnk")]
    [InlineData(@"C:\Users\someone\Desktop\Site.url")]
    [InlineData(@"\\server\share\folder\file.txt")]
    [InlineData(@"\\?\C:\very\long\path\file.txt")]
    [InlineData(@"\\?\UNC\server\share\file.txt")]
    [InlineData("C:/forward/slashes.txt")]
    public void Absolute_physical_paths_can_be_previewed_without_touching_the_disk(string target)
    {
        Assert.True(new QuickLookPreviewService(NewPipeName()).CanPreview(Item(target)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file.txt")]
    [InlineData(@"relative\dir\file.txt")]
    [InlineData(@"\rooted\no\drive.txt")]
    [InlineData(@"C:drive-relative.txt")]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData(@"SHELL:AppsFolder\Anything")]
    [InlineData("https://example.com/a.txt")]
    [InlineData("ms-settings:display")]
    [InlineData(@"C:\folder\pipe|delimiter.txt")]
    [InlineData("C:\\folder\"quote\".txt")]
    [InlineData(@"\\.\pipe\something")]
    [InlineData(@"\\.\C:\file.txt")]
    [InlineData(@"\??\C:\file.txt")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\file.txt")]
    public void Anything_else_cannot_be_previewed(string target)
    {
        var service = new QuickLookPreviewService(NewPipeName());
        Assert.False(service.CanPreview(Item(target)));
    }

    [Fact]
    public void Protocol_delimiters_and_unencodable_text_cannot_be_previewed()
    {
        // Built at run time: control characters and a lone surrogate do not belong in test names.
        var service = new QuickLookPreviewService(NewPipeName());
        foreach (var target in new[]
        {
            "C:\\folder\r\ninjected.txt",
            "C:\\folder\ninjected.txt",
            "C:\\folder\rinjected.txt",
            "C:\\folder\0nul.txt",
            "C:\\folder\ttab.txt",
            "C:\\unpaired" + '\uD800' + "surrogate.txt",
        })
            Assert.False(service.CanPreview(Item(target)));
    }

    [Fact]
    public async Task Missing_row_is_not_previewable_and_preview_rejects_null()
    {
        var service = new QuickLookPreviewService(NewPipeName());
        Assert.False(service.CanPreview(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.PreviewAsync(null!));
    }

    [Fact]
    public void Paths_are_normalised_to_the_form_that_is_sent()
    {
        Assert.True(QuickLookPreviewService.TryGetPreviewPath(Item("C:/a/b/../c.txt"), out var path));
        Assert.Equal(@"C:\a\c.txt", path);
    }

    [Fact]
    public void Toggle_and_switch_messages_use_the_verified_protocol_command_in_utf8_without_a_bom()
    {
        var path = @"C:\a b\预览.txt";

        Assert.Equal(Utf8NoBom.GetBytes("QuickLook.App.PipeMessages.Toggle|C:\\a b\\预览.txt|\n"), QuickLookPreviewService.BuildMessage(path));
        Assert.Equal(Utf8NoBom.GetBytes("QuickLook.App.PipeMessages.Switch|C:\\a b\\预览.txt|\n"), QuickLookPreviewService.BuildMessage(path, QuickLookPreviewService.SwitchCommand));
        Assert.NotEqual(0xEF, QuickLookPreviewService.BuildMessage(path)[0]);
    }

    [Fact]
    public void Default_pipe_is_named_after_the_current_user_sid()
    {
        var name = QuickLookPreviewService.DefaultPipeName();

        Assert.NotNull(name);
        Assert.StartsWith("QuickLook.App.Pipe.S-1-", name);
    }

    [Fact]
    public void Constructor_rejects_unusable_settings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuickLookPreviewService(NewPipeName(), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuickLookPreviewService(NewPipeName(), TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentException>(() => new QuickLookPreviewService("  "));
    }

    [Fact]
    public async Task An_existing_file_is_sent_as_one_toggle_request()
    {
        var file = Path.Combine(_directory, "report.txt");
        File.WriteAllText(file, "hello");
        using var server = new FakeQuickLook();

        var result = await Service(server.Name).PreviewAsync(Item(file));

        Assert.True(result.Success);
        Assert.Equal("预览请求已发送", result.Message);
        var received = await server.Received.WaitAsync(Wait);
        Assert.Equal(Utf8NoBom.GetBytes($"QuickLook.App.PipeMessages.Toggle|{file}|\n"), received);
    }

    [Fact]
    public async Task Switching_sends_the_non_toggling_switch_command_for_an_existing_target()
    {
        var file = Path.Combine(_directory, "switch-target.txt");
        File.WriteAllText(file, "hello");
        using var server = new FakeQuickLook();

        var result = await Service(server.Name).SwitchAsync(Item(file));

        Assert.True(result.Success);
        Assert.Equal(Utf8NoBom.GetBytes($"QuickLook.App.PipeMessages.Switch|{file}|\n"), await server.Received.WaitAsync(Wait));
    }

    [Fact]
    public async Task Closing_sends_only_the_pathless_close_command_and_never_toggle_or_quit()
    {
        using var server = new FakeQuickLook();
        var result = await Service(server.Name).CloseAsync();
        Assert.True(result.Success);
        Assert.Equal(Utf8NoBom.GetBytes("QuickLook.App.PipeMessages.Close||\n"), await server.Received.WaitAsync(Wait));
    }

    [Fact]
    public async Task A_cancelled_close_sends_nothing_and_a_missing_pipe_fails_within_its_bound()
    {
        using var server = new FakeQuickLook();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await Service(server.Name).CloseAsync(cancellation.Token);
        Assert.False(cancelled.Success);
        Assert.False(server.Connected);
        var unavailable = await Service(NewPipeName(), timeoutMilliseconds: 50).CloseAsync();
        Assert.False(unavailable.Success);
        Assert.Contains("超时", unavailable.Message);
    }

    [Fact]
    public async Task An_unsupported_switch_target_sends_nothing()
    {
        using var server = new FakeQuickLook();

        var result = await Service(server.Name).SwitchAsync(Item("https://example.com"));

        Assert.False(result.Success);
        await Task.Delay(100);
        Assert.False(server.Connected);
    }

    [Fact]
    public async Task An_existing_folder_is_sent_the_same_way()
    {
        var folder = Path.Combine(_directory, "some folder");
        Directory.CreateDirectory(folder);
        using var server = new FakeQuickLook();

        var result = await Service(server.Name).PreviewAsync(Item(folder, folder: true));

        Assert.True(result.Success);
        var received = await server.Received.WaitAsync(Wait);
        Assert.Equal(Utf8NoBom.GetBytes($"QuickLook.App.PipeMessages.Toggle|{folder}|\n"), received);
    }

    [Fact]
    public async Task Non_ascii_and_astral_characters_survive_as_utf8()
    {
        var file = Path.Combine(_directory, "预览 é 😀.txt");
        File.WriteAllText(file, "x");
        using var server = new FakeQuickLook();

        var result = await Service(server.Name).PreviewAsync(Item(file));

        Assert.True(result.Success);
        var received = await server.Received.WaitAsync(Wait);
        Assert.Equal(Utf8NoBom.GetBytes($"QuickLook.App.PipeMessages.Toggle|{file}|\n"), received);
        Assert.Equal($"QuickLook.App.PipeMessages.Toggle|{file}|\n", Utf8NoBom.GetString(received));
    }

    [Fact]
    public async Task A_shortcut_is_previewed_as_the_file_it_is_and_never_resolved_or_run()
    {
        // A .cmd that would leave a marker if anything ever executed it, and a .lnk that is not a
        // valid shortcut: resolving it would fail, previewing it as a plain file must not.
        var marker = Path.Combine(_directory, "executed.marker");
        var script = Path.Combine(_directory, "run-me.cmd");
        File.WriteAllText(script, $"@echo ran> \"{marker}\"");
        var shortcut = Path.Combine(_directory, "program.lnk");
        File.WriteAllText(shortcut, "not a real shortcut");

        using var server = new FakeQuickLook();
        var service = Service(server.Name);

        var result = await service.PreviewAsync(Item(shortcut));
        Assert.True(result.Success);
        var received = Utf8NoBom.GetString(await server.Received.WaitAsync(Wait));
        Assert.Equal($"QuickLook.App.PipeMessages.Toggle|{shortcut}|\n", received);

        using var scriptServer = new FakeQuickLook();
        Assert.True((await Service(scriptServer.Name).PreviewAsync(Item(script))).Success);
        await scriptServer.Received.WaitAsync(Wait);
        await Task.Delay(300);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_missing_target_fails_without_connecting_to_the_pipe()
    {
        using var server = new FakeQuickLook();

        var result = await Service(server.Name).PreviewAsync(Item(Path.Combine(_directory, "gone.txt")));

        Assert.False(result.Success);
        Assert.Contains("不存在", result.Message);
        await Task.Delay(200);
        Assert.False(server.Connected);
        Assert.False(server.Received.IsCompleted);
    }

    [Theory]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData("https://example.com")]
    [InlineData("relative.txt")]
    [InlineData("")]
    public async Task Targets_that_are_not_physical_paths_fail_without_connecting(string target)
    {
        using var server = new FakeQuickLook();

        var result = await Service(server.Name).PreviewAsync(Item(target));

        Assert.False(result.Success);
        Assert.Contains("本地文件", result.Message);
        await Task.Delay(100);
        Assert.False(server.Connected);
    }

    [Fact]
    public async Task No_listening_pipe_times_out_with_a_neutral_message()
    {
        var file = Path.Combine(_directory, "report.txt");
        File.WriteAllText(file, "hello");
        var stopwatch = Stopwatch.StartNew();

        var result = await Service(NewPipeName(), timeoutMilliseconds: 200).PreviewAsync(Item(file));

        Assert.False(result.Success);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "the connect must be bounded by the short timeout");
        Assert.Contains("QuickLook", result.Message);
        // The pipe may simply be busy or owned by another session: never claim it is not installed.
        Assert.DoesNotContain("未安装", result.Message);
        Assert.DoesNotContain("没有安装", result.Message);
    }

    [Fact]
    public async Task An_already_cancelled_token_returns_a_cancelled_result_and_sends_nothing()
    {
        var file = Path.Combine(_directory, "report.txt");
        File.WriteAllText(file, "hello");
        using var server = new FakeQuickLook();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await Service(server.Name).PreviewAsync(Item(file), cancellation.Token);

        Assert.False(result.Success);
        Assert.Contains("取消", result.Message);
        await Task.Delay(100);
        Assert.False(server.Connected);
    }

    [Fact]
    public async Task Cancelling_a_pending_connect_returns_promptly_without_throwing()
    {
        var file = Path.Combine(_directory, "report.txt");
        File.WriteAllText(file, "hello");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();

        // The generous timeout proves it was the token, not the timeout, that ended the wait.
        var result = await Service(NewPipeName(), timeoutMilliseconds: 30_000).PreviewAsync(Item(file), cancellation.Token);

        Assert.False(result.Success);
        Assert.Contains("取消", result.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_server_that_accepts_but_never_reads_cannot_block_the_caller_forever()
    {
        // An unbuffered pipe only completes a write once the server reads it, so a wedged QuickLook
        // must end in the bounded write timeout instead of hanging the launcher.
        var file = Path.Combine(_directory, "report.txt");
        File.WriteAllText(file, "hello");
        var name = NewPipeName();
        using var server = new NamedPipeServerStream(
            name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0);
        var accepting = server.WaitForConnectionAsync();
        var stopwatch = Stopwatch.StartNew();

        var result = await Service(name, timeoutMilliseconds: 300).PreviewAsync(Item(file)).WaitAsync(Wait);

        await accepting.WaitAsync(Wait);
        Assert.False(result.Success);
        Assert.Contains("超时", result.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }
}
