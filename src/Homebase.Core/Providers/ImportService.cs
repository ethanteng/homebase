using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Providers;

/// <summary>
/// Copies files and folders out of somewhere and into the library, once. After an import the
/// library's copy is the one that counts — Homebase does not go back for it, so nothing here ever
/// overwrites a file that is already on disk.
///
/// Where the files come from is the caller's choice and nothing here depends on it: an online
/// account and a folder on this computer are both an <see cref="IImportSource"/>, and get the same
/// walk, the same room check, the same never-overwrite rule and the same log.
/// </summary>
public sealed class ImportService(LibraryService library, ImportLog log, ILogger<ImportService> logger)
{
    /// <summary>
    /// A folder import walks the remote tree; the cap guards against a pathological account
    /// rather than expressing a considered limit. Reaching it is always reported, never silent.
    /// </summary>
    public int MaxEntries { get; init; } = 20000;

    /// <summary>
    /// The most room to leave alone on the drive. Filling a disk to the last byte breaks far more
    /// than this import — the metadata index lives on the same disk and needs somewhere to write.
    /// </summary>
    public long Headroom { get; init; } = 256L * 1024 * 1024;

    /// <summary>Free space on the library's drive; replaced in tests.</summary>
    public Func<string, StorageReport?> Space { get; init; } = Storage.For;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public IReadOnlyList<ImportedFile> Imported() => log.List(RequireRoot());

    /// <summary>
    /// Throws unless a library folder has been chosen. An import that cannot possibly work is
    /// refused by the request that asked for it, rather than started and failed out of sight.
    /// </summary>
    public void RequireLibrary() => RequireRoot();

    /// <summary>Imports one file, or every ordinary file beneath one folder.</summary>
    public Task<ImportResult> ImportAsync(IImportSource source, string remotePath, CancellationToken cancellationToken) =>
        ImportAsync(source, remotePath, null, cancellationToken);

    /// <summary>
    /// As above, reporting its way through so something running in the background can be watched.
    /// </summary>
    public async Task<ImportResult> ImportAsync(
        IImportSource source, string remotePath, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new LibraryException("Uncloud is already importing. Let that finish first.", "busy");
        try
        {
            var root = RequireRoot();
            var entry = await source.GetMetadataAsync(remotePath, cancellationToken);
            var imported = new List<ImportedItem>();
            var skipped = new List<SkippedItem>();
            long bytes = 0;

            var collected = await CollectAsync(source, entry, skipped, cancellationToken);
            RequireRoomFor(source, root, collected);
            var done = 0;
            progress?.Report(new ImportProgress(collected.Count, done, bytes, null));

            foreach (var file in collected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ImportProgress(collected.Count, done, bytes, file.DisplayPath));
                try
                {
                    var item = await ImportOneAsync(source, root, file, cancellationToken);
                    if (item is null) skipped.Add(new SkippedItem(file.DisplayPath, "Already imported.", Expected: true));
                    else
                    {
                        imported.Add(item);
                        bytes += item.Size;
                    }
                }
                catch (Exception failure) when (Recoverable(failure, cancellationToken))
                {
                    // One unreachable or refused file must not abandon the rest of the folder.
                    logger.LogWarning(failure, "{Provider} file {RemotePath} was not brought home",
                        source.ProviderId, file.DisplayPath);
                    skipped.Add(new SkippedItem(file.DisplayPath, Reason(failure)));
                }
                done++;
            }
            progress?.Report(new ImportProgress(collected.Count, done, bytes, null));
            return new ImportResult(imported, skipped, bytes);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// What this file or folder would bring home, measured without downloading anything. The walk
    /// is the same one an import does, so the answer is the one the import will act on.
    /// </summary>
    public async Task<ImportEstimate> MeasureAsync(IImportSource source, string remotePath, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        var entry = await source.GetMetadataAsync(remotePath, cancellationToken);
        var files = await CollectAsync(source, entry, [], cancellationToken);
        var arriving = Arriving(source, root, files);
        var newBytes = arriving.Sum(file => file.Size ?? 0);
        var free = Space(root)?.FreeBytes;
        return new ImportEstimate(
            files.Count, files.Sum(file => file.Size ?? 0), arriving.Count, newBytes, free, Fits(newBytes, free));
    }

    /// <summary>
    /// The files an import would actually fetch. One already recorded, or one whose destination is
    /// taken by something Uncloud didn't write, is passed over without downloading, so counting it
    /// would hold a folder back over room it was never going to need.
    /// </summary>
    private IReadOnlyList<SourceEntry> Arriving(IImportSource source, string root, IReadOnlyList<SourceEntry> files)
    {
        var home = log.List(root).Where(file => file.Provider == source.ProviderId)
            .Select(file => file.RemotePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return files.Where(file => !home.Contains(file.Path) && !Occupied(source, root, file)).ToArray();
    }

    private static bool Occupied(IImportSource source, string root, SourceEntry file)
    {
        try
        {
            var destination = PathPolicy.Resolve(root, DestinationFor(source, file));
            if (File.Exists(destination) || Directory.Exists(destination)) return true;
            // A file standing where one of the folders above it belongs blocks the download just
            // as surely: the import fails making that folder rather than fetching anything.
            for (var above = Path.GetDirectoryName(destination);
                 above is not null && above.Length > root.Length;
                 above = Path.GetDirectoryName(above))
                if (File.Exists(above))
                    return true;
            return false;
        }
        catch (LibraryException)
        {
            // A path the import will refuse for its own reasons costs nothing on disk either way.
            return true;
        }
    }

    /// <summary>
    /// Whether this much can be brought home without running the drive down to nothing. The room
    /// held back shrinks with the space left, so a drive that is already tight still takes a small
    /// file rather than refusing everything on principle.
    /// </summary>
    private bool Fits(long needed, long? free)
    {
        if (needed == 0 || free is null) return true;
        return needed <= free.Value - Math.Min(Headroom, free.Value / 10);
    }

    /// <summary>
    /// Refuses before a single byte is downloaded when the files can't fit. Running a drive out of
    /// space halfway through a folder is a far worse outcome than not starting it.
    /// </summary>
    private void RequireRoomFor(IImportSource source, string root, IReadOnlyList<SourceEntry> files)
    {
        var needed = Arriving(source, root, files).Sum(file => file.Size ?? 0);
        var free = Space(root)?.FreeBytes;
        if (Fits(needed, free)) return;
        throw new LibraryException(
            $"This would bring {Storage.Describe(needed)} home and only {Storage.Describe(free!.Value)} is free on "
            + "your Uncloud drive. Make some room, or bring part of the folder instead.", "unavailable");
    }

    /// <summary>Flattens a file or folder into the ordinary files worth importing.</summary>
    private async Task<IReadOnlyList<SourceEntry>> CollectAsync(
        IImportSource source, SourceEntry entry, List<SkippedItem> skipped, CancellationToken cancellationToken)
    {
        if (!entry.IsFolder) return [entry];

        var files = new List<SourceEntry>();
        var folders = new Queue<string>();
        folders.Enqueue(entry.Path);
        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders.Dequeue();
            IReadOnlyList<SourceEntry> children;
            try
            {
                children = await source.ListFolderAsync(folder, cancellationToken);
            }
            catch (Exception failure) when (Recoverable(failure, cancellationToken))
            {
                // A folder that vanished or can't be read must not abandon the files already found,
                // which collection would otherwise discard before a single download began. Losing a
                // whole subtree is worth a line in the log: the import itself reads as a success.
                logger.LogWarning(failure,
                    "{Provider} folder {RemotePath} couldn’t be listed, so it and everything under it was left behind",
                    source.ProviderId, folder);
                skipped.Add(new SkippedItem(folder, Reason(failure)));
                continue;
            }
            foreach (var child in children)
            {
                if (child.Name.StartsWith('.'))
                {
                    skipped.Add(new SkippedItem(child.DisplayPath, "Hidden files aren’t imported yet."));
                    continue;
                }
                if (child.IsFolder)
                {
                    folders.Enqueue(child.Path);
                    continue;
                }
                if (files.Count >= MaxEntries)
                {
                    // Stopping quietly here would read as a complete import. Say what was left.
                    logger.LogWarning("{Provider} folder {RemotePath} holds more than {MaxEntries} files, so the rest wasn’t visited",
                        source.ProviderId, entry.DisplayPath, MaxEntries);
                    skipped.Add(new SkippedItem(child.DisplayPath,
                        $"Uncloud brings at most {MaxEntries:N0} files at a time, so the rest of this folder wasn’t visited."));
                    return files;
                }
                files.Add(child);
            }
        }
        return files;
    }

    /// <summary>
    /// Whether one failure can be set aside so the rest of the import continues. A request that
    /// times out surfaces as cancellation, so the caller's own cancellation is told apart first and
    /// always wins: an abandoned import must never read as a folder full of skipped files.
    /// </summary>
    private static bool Recoverable(Exception failure, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && failure is LibraryException or HttpRequestException
            or IOException or UnauthorizedAccessException or OperationCanceledException;

    /// <summary>
    /// What to tell someone reading the skipped list. A timeout's own message says only that a task
    /// was cancelled, which explains nothing about their file. Worded without naming where the file
    /// came from, because by here it could be any of them.
    /// </summary>
    private static string Reason(Exception failure) =>
        failure is OperationCanceledException ? "Getting this file took too long." : failure.Message;

    private async Task<ImportedItem?> ImportOneAsync(
        IImportSource source, string root, SourceEntry entry, CancellationToken cancellationToken)
    {
        if (log.Contains(root, source.ProviderId, entry.Path)) return null;

        var localPath = DestinationFor(source, entry);
        var fullPath = PathPolicy.Resolve(root, localPath);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new LibraryException(
                $"A file already exists at {localPath}. Uncloud won’t overwrite files it didn’t put there.", "conflict");

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
            await using (var remote = await source.OpenAsync(entry.Path, cancellationToken))
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
                    $"Something else created {localPath} while Uncloud was downloading it, so it was left alone.", "conflict");
            }

            var written = new FileInfo(fullPath);
            log.Record(root, new ImportedFile(
                source.ProviderId, entry.Path, entry.Rev ?? "", localPath, written.Length, hash, DateTimeOffset.UtcNow));
            return new ImportedItem(localPath, entry.DisplayPath, written.Length);
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

    private static string DestinationFor(IImportSource source, SourceEntry entry)
    {
        var relative = entry.DisplayPath.TrimStart('/');
        if (relative.Length == 0) relative = entry.Name;
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith('.'))
                throw new LibraryException(
                    $"Uncloud doesn’t import hidden files or folders yet, and “{part}” is hidden.", "unsupported");
        return $"{source.DestinationPrefix}/{relative}";
    }

    /// <summary>
    /// The account's folder. Which folder that is was settled when the workspace was made, from
    /// an authenticated session, so all that is left to check here is that it is still there.
    /// </summary>
    private string RequireRoot()
    {
        PathPolicy.Resolve(library.Root, "");
        return library.Root;
    }
}
