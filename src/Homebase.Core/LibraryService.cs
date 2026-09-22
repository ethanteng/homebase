namespace Homebase.Core;

/// <summary>
/// One account's files. The root is fixed when this is made, from the session the request was
/// authenticated with, so no caller can point it at somebody else's folder. Each account gets
/// its own instance and its own gate, so one person browsing never holds up another.
/// </summary>
public sealed class LibraryService(string root, MetadataIndex index) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Root => root;

    public LibraryState State => new(root, new DirectoryInfo(root).Name);

    /// <summary>Makes the metadata database if this folder hasn't been opened before.</summary>
    public void Initialize() => index.Initialize(root);

    public async Task<DirectoryListing> BrowseAsync(string? relativePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fullPath = PathPolicy.Resolve(root, relativePath);
            if (!Directory.Exists(fullPath)) throw new LibraryException("This folder is no longer available.", "not_found");
            var normalized = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            if (normalized == ".") normalized = "";
            var entries = new List<LibraryEntry>();
            var skipped = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
            foreach (var entry in new DirectoryInfo(fullPath).EnumerateFileSystemInfos("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Name.StartsWith('.')) continue;
                try
                {
                    if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) { skipped++; continue; }
                    var isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
                    var path = normalized.Length == 0 ? entry.Name : $"{normalized}/{entry.Name}";
                    entries.Add(new(entry.Name, path, isDirectory, isDirectory ? null : ((FileInfo)entry).Length, entry.LastWriteTimeUtc));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skipped++; }
            }
            var listing = new DirectoryListing(normalized,
                entries.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                skipped, DateTimeOffset.UtcNow);
            index.ReplaceDirectory(root, listing, cancellationToken);
            return listing;
        }
        finally { _gate.Release(); }
    }

    public async Task<(FileStream Stream, string Name)> OpenFileAsync(string path, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fullPath = PathPolicy.Resolve(root, path);
            if (!File.Exists(fullPath)) throw new LibraryException("This file is no longer available.", "not_found");
            return (new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan), Path.GetFileName(fullPath));
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
