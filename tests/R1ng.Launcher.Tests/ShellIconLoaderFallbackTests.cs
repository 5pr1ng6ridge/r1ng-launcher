using System.Windows.Media;
using System.Windows.Media.Imaging;
using R1ng.Launcher.Platform;

namespace R1ng.Launcher.Tests;

/// <summary>
/// The shell-namespace icon path must use a bounded STA attempt and must never launch a duplicate
/// extraction on the caller's apartment after that attempt timed out (the first worker is still
/// inside COM, so a second call could hang and would only race the same shell state).
/// </summary>
public class ShellIconLoaderFallbackTests
{
    [Fact]
    public void An_STA_caller_extracts_directly_without_a_helper_thread()
    {
        var icon = Pixel();
        var calls = 0;

        var result = ShellIconLoader.ResolveShellNamespaceIcon(
            () =>
            {
                Interlocked.Increment(ref calls);
                return icon;
            },
            TimeSpan.FromSeconds(5),
            currentApartmentIsSta: true
        );

        Assert.Same(icon, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_successful_STA_extraction_is_used_without_a_same_apartment_retry()
    {
        var icon = Pixel();
        var calls = 0;

        var result = ShellIconLoader.ResolveShellNamespaceIcon(
            () =>
            {
                Interlocked.Increment(ref calls);
                return icon;
            },
            TimeSpan.FromSeconds(5),
            currentApartmentIsSta: false
        );

        Assert.Same(icon, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_completed_but_empty_STA_extraction_falls_back_on_the_caller_apartment()
    {
        var icon = Pixel();
        var staCalls = 0;
        var fallbackCalls = 0;

        var result = ShellIconLoader.ResolveShellNamespaceIcon(
            () =>
            {
                if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                {
                    Interlocked.Increment(ref staCalls);
                    return null;
                }

                Interlocked.Increment(ref fallbackCalls);
                return icon;
            },
            TimeSpan.FromSeconds(5),
            currentApartmentIsSta: false
        );

        Assert.Same(icon, result);
        Assert.Equal(1, staCalls);
        Assert.Equal(1, fallbackCalls);
    }

    [Fact]
    public async Task A_timed_out_STA_extraction_is_not_retried_on_the_caller_apartment()
    {
        using var gate = new ManualResetEventSlim(false);
        var calls = 0;

        var result = ShellIconLoader.ResolveShellNamespaceIcon(
            () =>
            {
                Interlocked.Increment(ref calls);
                gate.Wait(TimeSpan.FromSeconds(2));
                return null;
            },
            TimeSpan.FromMilliseconds(30),
            currentApartmentIsSta: false
        );

        Assert.Null(result);
        Assert.Equal(1, Volatile.Read(ref calls));

        // Let the late worker finish; its completion must not spawn a duplicate fallback extraction.
        gate.Set();
        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void Url_shortcuts_always_use_the_generic_extension_icon()
    {
        // A .url with a rejected remote IconFile must never be re-read by the shell's InternetShortcut
        // handler (which could fetch the UNC icon over the network), so it is loaded by extension.
        Assert.True(ShellIconLoader.ShouldUseFileAttributes(@"C:\games\Celeste.url", exists: true));
        Assert.True(ShellIconLoader.ShouldUseFileAttributes(@"C:\games\Celeste.URL", exists: true));
        Assert.True(ShellIconLoader.ShouldUseFileAttributes(@"C:\games\missing.exe", exists: false));
        Assert.False(ShellIconLoader.ShouldUseFileAttributes(@"C:\games\game.exe", exists: true));
    }

    private static BitmapSource Pixel() =>
        BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], stride: 4);
}
