using System.Collections.Concurrent;

namespace Homebase.Core.Providers;

/// <summary>
/// Folder sizes measured in the places one account brings files in from, kept for a few minutes.
/// Measuring an online folder costs a listing of everything under it, and the same folders are
/// asked about again every time somebody steps back out to where they were.
/// </summary>
public sealed class SourceSizes
{
    public TimeSpan Freshness { get; init; } = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (FolderSize Size, DateTimeOffset At)> _measured = new(StringComparer.Ordinal);

    public async Task<FolderSize> MeasureAsync(IImportSource source, string path, CancellationToken cancellationToken)
    {
        var key = $"{source.ProviderId}\n{path}";
        if (_measured.TryGetValue(key, out var kept) && DateTimeOffset.UtcNow - kept.At < Freshness) return kept.Size;
        var size = await source.SizeAsync(path, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        // Somebody browsing a large account for a long while shouldn't grow this without end.
        if (_measured.Count >= 2000)
            foreach (var (stale, entry) in _measured)
                if (now - entry.At >= Freshness) _measured.TryRemove(stale, out _);
        _measured[key] = (size, now);
        return size;
    }
}
