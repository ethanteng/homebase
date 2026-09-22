namespace Homebase.Core.Providers;

/// <summary>
/// A folder on this computer, read the same way an online account is. Nothing here authenticates or
/// reaches the network: the person already has these files, and bringing them in is a copy onto
/// storage they own, which is the whole of what Uncloud is for.
///
/// Every path is relative to the place's own folder and is resolved through the same policy the
/// library uses, so a listing cannot walk upwards, cannot follow a symbolic link out, and cannot
/// name a hidden file. The place's folder is the boundary; a caller only ever holds paths inside it.
/// </summary>
public sealed class LocalFolderSource(ImportPlace place) : IImportSource
{
    public string ProviderId => place.ProviderId;
    public string DestinationPrefix => place.DestinationPrefix;

    public Task<SourceEntry> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        if (Directory.Exists(full)) return Task.FromResult(Describe(new DirectoryInfo(full), path));
        if (File.Exists(full)) return Task.FromResult(Describe(new FileInfo(full), path));
        throw new LibraryException($"There’s nothing at {Display(path)} in “{place.Name}” any more.", "not_found");
    }

    public Task<IReadOnlyList<SourceEntry>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        if (!Directory.Exists(full))
            throw new LibraryException($"There’s no folder at {Display(path)} in “{place.Name}”.", "not_found");
        var entries = new List<SourceEntry>();
        foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos("*",
                     new EnumerationOptions { IgnoreInaccessible = true, ReturnSpecialDirectories = false }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Links are not followed anywhere in Uncloud, and one here could point clean out of
            // the place — including at the host's folder, which is the one thing a place may not
            // reach. Left out of the listing rather than refused, so one link in somebody's
            // Dropbox folder doesn't make the whole folder unbrowsable.
            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            entries.Add(Describe(entry, Join(path, entry.Name)));
        }
        return Task.FromResult<IReadOnlyList<SourceEntry>>(entries
            // Folders first, then names, which is how the file browser already orders a directory.
            .OrderByDescending(entry => entry.IsFolder)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public Task<Stream> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        // Read-only and shared: the file stays exactly where it is, and a sync app writing to it
        // at the same moment is no reason to refuse to read it.
        return Task.FromResult<Stream>(new FileStream(full, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16,
            FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    // The leading slash is presentation, matching how an online account names its own root; the
    // policy below wants a relative path and treats a rooted one as an attempt to escape.
    private string Resolve(string path) => PathPolicy.Resolve(place.Path, path.TrimStart('/'),
        $"“{place.Name}” isn’t on this computer right now. Plug the drive back in and try again.",
        $"Use a path inside “{place.Name}”.",
        "Hidden files and folders aren’t brought in.");

    private static SourceEntry Describe(FileSystemInfo entry, string path)
    {
        var folder = entry is DirectoryInfo;
        return new SourceEntry(
            Id: path,
            Name: entry.Name,
            Path: path,
            DisplayPath: Display(path),
            IsFolder: folder,
            Size: folder ? null : Length(entry),
            // What stands in for a revision: a file whose size or modification time is unchanged
            // is the file that was imported. Only ever compared, never parsed.
            Rev: folder ? null : $"{Length(entry)}-{entry.LastWriteTimeUtc.Ticks}",
            Modified: entry.LastWriteTimeUtc);
    }

    private static long? Length(FileSystemInfo entry)
    {
        try { return entry is FileInfo file ? file.Length : null; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A file that won't say how big it is still imports; it just can't be measured first.
            return null;
        }
    }

    private static string Join(string path, string name) =>
        path.Length == 0 || path == "/" ? $"/{name}" : $"{path.TrimEnd('/')}/{name}";

    private static string Display(string path) => path.Length == 0 ? "/" : path;
}
