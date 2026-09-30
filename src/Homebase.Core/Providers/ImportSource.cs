namespace Homebase.Core.Providers;

/// <summary>
/// One file or folder as the place it came from describes it. <paramref name="Path"/> is how that
/// place addresses the entry and is the key an import is recorded under; <paramref name="Rev"/> is
/// whatever the place uses to say "these contents changed".
/// </summary>
public sealed record SourceEntry(
    string Id,
    string Name,
    string Path,
    string DisplayPath,
    bool IsFolder,
    long? Size,
    string? Rev,
    DateTimeOffset? Modified);

/// <summary>
/// Somewhere files can be brought home from. The import engine knows only this much, so an online
/// account and a folder on the host's own disk go through exactly the same walk, room check,
/// download, and log.
///
/// Every path an implementation is handed came from one of its own listings, and an implementation
/// is responsible for refusing anything that reaches outside the place it stands for.
/// </summary>
public interface IImportSource
{
    /// <summary>What imports from here are recorded under, and so what "already home" means.</summary>
    string ProviderId { get; }

    /// <summary>The folder at the top of the library that files from here land in, such as Dropbox.</summary>
    string DestinationPrefix { get; }

    Task<SourceEntry> GetMetadataAsync(string path, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceEntry>> ListFolderAsync(string path, CancellationToken cancellationToken);
    Task<Stream> OpenAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// What a folder here holds, counted the way an import collects it: every file beneath it,
    /// hidden ones left out. This walks the tree a folder at a time, which is right for a folder on
    /// this computer; a place that can list a whole tree in one go should do that instead.
    /// </summary>
    async Task<FolderSize> SizeAsync(string path, CancellationToken cancellationToken)
    {
        long bytes = 0;
        var files = 0;
        var folders = new Queue<string>([path]);
        while (folders.TryDequeue(out var folder))
            foreach (var child in await ListFolderAsync(folder, cancellationToken))
            {
                if (child.Name.StartsWith('.')) continue;
                if (child.IsFolder) folders.Enqueue(child.Path);
                else
                {
                    bytes += child.Size ?? 0;
                    files++;
                }
            }
        return new FolderSize(bytes, files);
    }
}
