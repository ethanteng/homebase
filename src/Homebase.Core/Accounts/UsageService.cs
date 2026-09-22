using System.Collections.Concurrent;

namespace Homebase.Core.Accounts;

/// <summary>
/// How much of the shared volume one account is using. Measured by walking their folder rather
/// than kept as a running total, because files arrive by routes Uncloud doesn't see — a copy in
/// Finder — and a number maintained incrementally would quietly drift away from the truth.
/// </summary>
public sealed class UsageService
{
    /// <summary>How long an answer stands before the walk is repeated.</summary>
    public TimeSpan Freshness { get; init; } = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (long Bytes, DateTimeOffset At)> _measured = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _walks = new(StringComparer.Ordinal);

    public long UsedBytes(string root)
    {
        var now = DateTimeOffset.UtcNow;
        if (_measured.TryGetValue(root, out var cached) && now - cached.At < Freshness) return cached.Bytes;

        // One walk per folder at a time: a page that asks twice shouldn't walk the tree twice.
        var gate = _walks.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            if (_measured.TryGetValue(root, out cached) && DateTimeOffset.UtcNow - cached.At < Freshness) return cached.Bytes;
            var bytes = Walk(root);
            _measured[root] = (bytes, DateTimeOffset.UtcNow);
            return bytes;
        }
        finally { gate.Release(); }
    }

    /// <summary>Forgets a measurement, so the next answer is taken fresh.</summary>
    public void Invalidate(string root) => _measured.TryRemove(root, out _);

    private static long Walk(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        var folders = new Queue<string>();
        folders.Enqueue(root);
        while (folders.Count > 0)
        {
            var folder = folders.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(folder).EnumerateFileSystemInfos("*",
                    new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                try
                {
                    // Links are never followed: the bytes they point at belong to wherever they
                    // really live, and following one could walk out of the folder entirely.
                    if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (entry.Attributes.HasFlag(FileAttributes.Directory)) folders.Enqueue(entry.FullName);
                    else total += ((FileInfo)entry).Length;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A file that vanished mid-walk simply isn't counted.
                }
            }
        }
        return total;
    }
}
