using System.IO;
using System.Text;

namespace R1ng.Launcher.Platform;

internal readonly record struct QuickLookWindowCandidate(IntPtr Handle, ScreenPlacement.PixelsRect Bounds);

/// <summary>Returns conservative, visible top-level QuickLook preview candidates without touching them.</summary>
internal interface IQuickLookWindowLocator
{
    Task<IReadOnlyList<QuickLookWindowCandidate>> FindVisiblePreviewsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Finds QuickLook preview candidates by executable identity and the documented preview title shape.
/// It never chooses a window to move: the launcher only uses these rectangles to place itself.
/// </summary>
internal sealed class QuickLookWindowLocator : IQuickLookWindowLocator
{
    private const string QuickLookExecutable = "QuickLook.exe";
    private const string GenericPreviewTitle = "QuickLook";
    private const string NamedPreviewTitlePrefix = "QuickLook - ";
    private const int MaxCandidates = 32;
    private const int MaxTitleCharacters = 512;
    private const int MaxImagePathCharacters = 32768;

    public Task<IReadOnlyList<QuickLookWindowCandidate>> FindVisiblePreviewsAsync(CancellationToken cancellationToken) =>
        Task.Run(() => FindVisiblePreviews(cancellationToken), cancellationToken);

    internal static bool IsPreviewIdentity(string? processImagePath, string? title)
    {
        if (string.IsNullOrWhiteSpace(processImagePath) || string.IsNullOrWhiteSpace(title))
            return false;

        var imageName = Path.GetFileName(processImagePath);
        var previewTitle = string.Equals(title, GenericPreviewTitle, StringComparison.Ordinal)
            || title.StartsWith(NamedPreviewTitlePrefix, StringComparison.Ordinal);
        return string.Equals(imageName, QuickLookExecutable, StringComparison.OrdinalIgnoreCase) && previewTitle;
    }

    internal static bool IsEligiblePreviewWindow(
        bool isTopLevel,
        bool isVisible,
        bool isMinimized,
        bool cloakingWasRead,
        bool isCloaked,
        int width,
        int height,
        string? processImagePath,
        string? title) =>
        isTopLevel
        && isVisible
        && !isMinimized
        && cloakingWasRead
        && !isCloaked
        && width > 0
        && height > 0
        && IsPreviewIdentity(processImagePath, title);

    private static IReadOnlyList<QuickLookWindowCandidate> FindVisiblePreviews(CancellationToken cancellationToken)
    {
        var candidates = new List<QuickLookWindowCandidate>();
        NativeMethods.EnumWindowsCallback callback = (window, _) =>
        {
            if (cancellationToken.IsCancellationRequested || candidates.Count >= MaxCandidates)
                return false;
            if (!NativeMethods.IsWindow(window))
                return true;
            var isVisible = NativeMethods.IsWindowVisible(window);
            var isMinimized = NativeMethods.IsIconic(window);
            var cloakingWasRead = NativeMethods.TryGetWindowCloaked(window, out var cloaked);
            if (!isVisible || isMinimized || !cloakingWasRead || cloaked
                || !ScreenPlacement.TryGetWindowBounds(window, out var bounds)
                || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return true;
            }

            var threadId = NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (threadId == 0 || processId == 0)
                return true;

            var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, inheritHandle: false, processId);
            if (process == IntPtr.Zero)
                return true;

            string? imagePath;
            try
            {
                var image = new StringBuilder(MaxImagePathCharacters);
                var size = (uint)image.Capacity;
                imagePath = NativeMethods.QueryFullProcessImageNameW(process, 0, image, ref size)
                    ? image.ToString()
                    : null;
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }

            if (imagePath is null)
                return true;

            var titleBuffer = new StringBuilder(MaxTitleCharacters + 1);
            if (NativeMethods.GetWindowTextW(window, titleBuffer, titleBuffer.Capacity) <= 0
                || !IsEligiblePreviewWindow(
                    isTopLevel: true, // EnumWindows enumerates top-level HWNDs only.
                    isVisible,
                    isMinimized,
                    cloakingWasRead,
                    cloaked,
                    bounds.Width,
                    bounds.Height,
                    imagePath,
                    titleBuffer.ToString()))
            {
                return true;
            }

            // The HWND is informational only; the caller never moves it. A later snapshot can outlive
            // this window, so consumers use only its captured bounds and always move their own HWND.
            candidates.Add(new QuickLookWindowCandidate(window, bounds));
            return candidates.Count < MaxCandidates;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        return cancellationToken.IsCancellationRequested ? [] : candidates.ToArray();
    }
}
