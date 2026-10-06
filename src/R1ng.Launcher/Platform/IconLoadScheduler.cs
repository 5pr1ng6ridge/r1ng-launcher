using System.Windows.Media;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Platform;

/// <summary>A shared four-slot queue across snapshots, with one cooled-down retry per row.</summary>
internal sealed class IconLoadScheduler
{
    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly Func<string, bool, int, ImageSource?> _load;
    private readonly TimeSpan _retryDelay;

    public IconLoadScheduler(Func<string, bool, int, ImageSource?>? load = null, TimeSpan? retryDelay = null)
    {
        _load = load ?? ShellIconLoader.Load;
        _retryDelay = retryDelay ?? IconCache.FailureCooldown;
    }

    public async Task<ImageSource?> LoadAsync(SearchResultItem item, CancellationToken cancellationToken)
    {
        var path = item.IconPath ?? item.Target;
        if (string.IsNullOrWhiteSpace(path))
            return null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            ImageSource? image;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                image = await Task.Run(() => _load(path, item.IsFolder, item.IconIndex)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                image = null;
            }
            finally
            {
                _slots.Release();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (image is not null || attempt == 1)
                return image;
            // Waiting outside the slot keeps unrelated icons moving. A cancelled batch never retries.
            await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }
}
