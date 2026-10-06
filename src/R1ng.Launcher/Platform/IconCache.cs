using System.Collections.Concurrent;
using System.Windows.Media;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.Platform;

/// <summary>Single-flight successful icons; transient failures expire after the retry cooldown.</summary>
internal sealed class IconCache
{
    internal static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, Lazy<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _cooldown;

    public IconCache(Func<DateTime>? clock = null, TimeSpan? cooldown = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _cooldown = cooldown ?? FailureCooldown;
    }

    public ImageSource? Load(string path, bool isFolder, int iconIndex, Func<ImageSource?> extract)
    {
        var key = LaunchIdentity.Digest(LaunchIdentity.ForTarget(path), isFolder.ToString(), iconIndex.ToString());
        if (_entries.TryGetValue(key, out var previous) && previous.IsValueCreated)
        {
            var entry = previous.Value;
            if (entry.Image is null && _clock() - entry.CompletedAtUtc >= _cooldown)
                ((ICollection<KeyValuePair<string, Lazy<Entry>>>)_entries).Remove(new(key, previous));
        }
        var current = _entries.GetOrAdd(key, _ => new Lazy<Entry>(() =>
        {
            ImageSource? image;
            try
            {
                image = extract();
                if (image is not null && !image.IsFrozen)
                    image.Freeze();
            }
            catch (Exception)
            {
                image = null;
            }
            return new Entry(image, _clock());
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        return current.Value.Image;
    }

    private sealed record Entry(ImageSource? Image, DateTime CompletedAtUtc);
}
