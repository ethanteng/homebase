using System.Security.Cryptography;

namespace Homebase.Core.Providers;

/// <summary>
/// Copies files and folders out of a provider and into the library, once. After an import the
/// library's copy is the one that counts — Homebase does not go back to the provider for it,
/// so nothing here ever overwrites a file that is already on disk.
/// </summary>
public sealed class ImportService(LibraryService library, ImportLog log, IDropboxApi dropbox)
{
    public const string Provider = "dropbox";
    public const string DestinationPrefix = "Files/Dropbox";

    /// <summary>
    /// A folder import walks the remote tree; the cap guards against a pathological account
    /// rather than expressing a considered limit. Reaching it is always reported, never silent.
    /// </summary>
    public int MaxEntries { get; init; } = 20000;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public IReadOnlyList<ImportedFile> Imported() => log.List(RequireRoot());

    /// <summary>Imports one file, or every ordinary file beneath one folder.</summary>
    public async Task<ImportResult> ImportAsync(string remotePath, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new LibraryException("Homebase is already importing. Let that finish first.", "busy");
        try
        {
            var root = RequireRoot();
            var entry = await dropbox.GetMetadataAsync(remotePath, cancellationToken);
            var imported = new List<ImportedItem>();
            var skipped = new List<SkippedItem>();
            long bytes = 0;

            foreach (var file in await CollectAsync(entry, skipped, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var item = await ImportOneAsync(root, file, cancellationToken);
                    if (item is null) skipped.Add(new SkippedItem(file.PathDisplay, "Already imported."));
                    else
                    {
                        imported.Add(item);
                        bytes += item.Size;
                    }
                }
                catch (Exception failure) when (failure is LibraryException or HttpRequestException)
                {
                    // One unreachable or refused file must not abandon the rest of the folder.
                    skipped.Add(new SkippedItem(file.PathDisplay, failure.Message));
                }
            }
            return new ImportResult(imported, skipped, bytes);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Flattens a file or folder into the ordinary files worth importing.</summary>
    private async Task<IReadOnlyList<DropboxEntry>> CollectAsync(
        DropboxEntry entry, List<SkippedItem> skipped, CancellationToken cancellationToken)
    {
        if (!entry.IsFolder) return [entry];

        var files = new List<DropboxEntry>();
        var folders = new Queue<string>();
        folders.Enqueue(entry.PathLower);
        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders.Dequeue();
            IReadOnlyList<DropboxEntry> children;
            try
            {
                children = await dropbox.ListFolderAsync(folder, cancellationToken);
            }
            catch (Exception failure) when (failure is LibraryException or HttpRequestException)
            {
                // A folder that vanished or can't be read must not abandon the files already found,
                // which collection would otherwise discard before a single download began.
                skipped.Add(new SkippedItem(folder, failure.Message));
                continue;
            }
            foreach (var child in children)
            {
                if (child.Name.StartsWith('.'))
                {
                    skipped.Add(new SkippedItem(child.PathDisplay, "Hidden files aren’t imported yet."));
                    continue;
                }
                if (child.IsFolder)
                {
                    folders.Enqueue(child.PathLower);
                    continue;
                }
                if (files.Count >= MaxEntries)
                {
                    // Stopping quietly here would read as a complete import. Say what was left.
                    skipped.Add(new SkippedItem(child.PathDisplay,
                        $"Homebase brings at most {MaxEntries:N0} files at a time, so the rest of this folder wasn’t visited."));
                    return files;
                }
                files.Add(child);
            }
        }
        return files;
    }

    private async Task<ImportedItem?> ImportOneAsync(string root, DropboxEntry entry, CancellationToken cancellationToken)
    {
        if (log.Contains(root, Provider, entry.PathLower)) return null;

        var localPath = DestinationFor(entry);
        var fullPath = PathPolicy.Resolve(root, localPath);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new LibraryException(
                $"A file already exists at {localPath}. Homebase won’t overwrite files it didn’t put there.", "conflict");

        var metadata = Path.GetDirectoryName(PathPolicy.PrepareMetadata(root))!;
        var temporary = Path.Combine(metadata, $"import.{Guid.NewGuid():N}.tmp");
        PathPolicy.RejectLink(temporary);
        try
        {
            var parent = Path.GetDirectoryName(fullPath)!;
            PathPolicy.RejectLink(parent);
            Directory.CreateDirectory(parent);

            // Written beside the destination and moved into place, so an interrupted transfer
            // cannot leave a half-written file where a whole one belongs.
            await using (var remote = await dropbox.DownloadAsync(entry.PathLower, cancellationToken))
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await remote.CopyToAsync(file, cancellationToken);
            }
            var hash = await HashAsync(temporary, cancellationToken);

            PathPolicy.RejectLink(fullPath);
            try
            {
                // Never an overwriting move: the destination is judged here, after the download,
                // and anything that arrived meanwhile wins.
                File.Move(temporary, fullPath);
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                throw new LibraryException(
                    $"Something else created {localPath} while Homebase was downloading it, so it was left alone.", "conflict");
            }

            var written = new FileInfo(fullPath);
            log.Record(root, new ImportedFile(
                Provider, entry.PathLower, entry.Rev ?? "", localPath, written.Length, hash, DateTimeOffset.UtcNow));
            return new ImportedItem(localPath, entry.PathDisplay, written.Length);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string DestinationFor(DropboxEntry entry)
    {
        var relative = entry.PathDisplay.TrimStart('/');
        if (relative.Length == 0) relative = entry.Name;
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith('.'))
                throw new LibraryException(
                    $"Homebase doesn’t import hidden files or folders yet, and “{part}” is hidden.", "unsupported");
        return $"{DestinationPrefix}/{relative}";
    }

    private string RequireRoot() =>
        library.State.RootPath ?? throw new LibraryException("Choose your Homebase folder first.", "not_configured");
}
