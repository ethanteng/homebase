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
    /// Files already here don't count towards it, so adding the same folder again picks up where
    /// the last one stopped rather than stopping at the same place every time.
    /// </summary>
    public int MaxEntries { get; init; } = 20000;

    /// <summary>The most room to leave alone on the drive; see <see cref="Storage.Headroom"/>.</summary>
    public long Headroom { get; init; } = Storage.Headroom;

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
        ImportAsync(source, [remotePath], null, cancellationToken);

    /// <summary>
    /// As above, reporting its way through so something running in the background can be watched.
    /// </summary>
    public Task<ImportResult> ImportAsync(
        IImportSource source, string remotePath, IProgress<ImportProgress>? progress, CancellationToken cancellationToken) =>
        ImportAsync(source, [remotePath], progress, cancellationToken);

    /// <summary>
    /// Several files and folders from one place as one import: one walk, one room check for all of
    /// them together, and one result, rather than one at a time with a wait between each.
    /// </summary>
    public async Task<ImportResult> ImportAsync(
        IImportSource source, IReadOnlyList<string> remotePaths, IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new LibraryException("Uncloud is already importing. Let that finish first.", "busy");
        try
        {
            // Copying, moving and deleting keep out of where this writes until it is done, and one
            // already under way there is waited for rather than raced.
            await library.BeginImportAsync(source.DestinationPrefix, cancellationToken);
            var root = RequireRoot();
            var skipped = new List<SkippedItem>();
            var chosen = await DescribeAsync(source, remotePaths, skipped, cancellationToken);
            var imported = new List<ImportedItem>();
            long bytes = 0;

            var walked = await CollectAsync(source, chosen, AlreadyHome(source, root), skipped, cancellationToken);
            var collected = walked.Files;
            RequireRoomFor(source, root, collected);
            // Read once for the whole import rather than per file: what another place already
            // brought home cannot change while this holds the gate.
            var alreadyHome = log.LocalPaths(root);
            var done = 0;
            progress?.Report(new ImportProgress(collected.Count, done, bytes, null));

            foreach (var file in collected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ImportProgress(collected.Count, done, bytes, file.DisplayPath));
                try
                {
                    var item = await ImportOneAsync(source, root, alreadyHome, file, cancellationToken);
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
            return new ImportResult(imported, skipped, bytes, walked.HomeFiles);
        }
        finally
        {
            library.EndImport();
            _gate.Release();
        }
    }

    /// <summary>
    /// What this file or folder would bring home, measured without downloading anything. The walk
    /// is the same one an import does, so the answer is the one the import will act on.
    /// </summary>
    public async Task<ImportEstimate> MeasureAsync(IImportSource source, string remotePath, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        var entry = await source.GetMetadataAsync(remotePath, cancellationToken);
        var walked = await CollectAsync(source, [entry], AlreadyHome(source, root), [], cancellationToken);
        var arriving = Arriving(source, root, walked.Files);
        var newBytes = arriving.Sum(file => file.Size ?? 0);
        var free = Space(root)?.FreeBytes;
        return new ImportEstimate(
            walked.Files.Count + walked.HomeFiles, walked.Files.Sum(file => file.Size ?? 0) + walked.HomeBytes,
            arriving.Count, newBytes, free, Fits(newBytes, free));
    }

    /// <summary>
    /// The files an import would actually fetch. One already recorded, or one whose destination is
    /// taken by something Uncloud didn't write, is passed over without downloading, so counting it
    /// would hold a folder back over room it was never going to need.
    /// </summary>
    private IReadOnlyList<SourceEntry> Arriving(IImportSource source, string root, IReadOnlyList<SourceEntry> files)
    {
        var home = AlreadyHome(source, root);
        return files.Where(file => !home(file) && !Occupied(source, root, file)).ToArray();
    }

    /// <summary>
    /// Whether a file is already here: brought in from this place before, or put in the same spot
    /// by another place that writes there too. An import passes either over as the ordinary
    /// outcome, so a walk counts them rather than carrying them — adding a large folder again would
    /// otherwise carry every file it already brought, however many that is.
    /// </summary>
    private Func<SourceEntry, bool> AlreadyHome(IImportSource source, string root)
    {
        // Exactly how the log compares them. The log is SQLite, whose default text comparison is
        // case-sensitive, so matching case-insensitively here made the two disagree: a file the
        // estimate wrote off as already home was one the import then went and fetched, over room
        // the drive was never checked for. Dropbox hands back lowercased paths either way.
        var fromHere = log.List(root).Where(file => file.Provider == source.ProviderId)
            .Select(file => file.RemotePath).ToHashSet(StringComparer.Ordinal);
        var arrived = log.LocalPaths(root);
        return file => fromHere.Contains(file.Path) || ArrivedFromElsewhere(source, root, arrived, file);
    }

    /// <summary>The same test <see cref="ImportOneAsync"/> makes before calling a file already home.</summary>
    private static bool ArrivedFromElsewhere(IImportSource source, string root, IReadOnlySet<string> arrived, SourceEntry file)
    {
        try
        {
            var local = DestinationFor(source, file);
            if (!arrived.Contains(local)) return false;
            var full = PathPolicy.Resolve(root, local);
            return File.Exists(full) || Directory.Exists(full);
        }
        catch (LibraryException)
        {
            // Refused for its own reasons, which the import reports when it gets there.
            return false;
        }
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

    private bool Fits(long needed, long? free) => Storage.Fits(needed, free, Headroom);

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

    /// <summary>
    /// What each thing asked for is. One thing that can't be found fails the import, as it always
    /// has; among several chosen together, one that has gone or is refused by name is passed over
    /// so the rest still arrive. Anything that isn't about that one item — an expired connection,
    /// Dropbox being unreachable — fails the import all the same, rather than finishing it with
    /// nothing brought and nothing said.
    /// </summary>
    private async Task<IReadOnlyList<SourceEntry>> DescribeAsync(
        IImportSource source, IReadOnlyList<string> remotePaths, List<SkippedItem> skipped,
        CancellationToken cancellationToken)
    {
        var paths = remotePaths.Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) throw new LibraryException("Choose something to add.", "invalid");
        if (paths.Length == 1) return [await source.GetMetadataAsync(paths[0], cancellationToken)];
        var entries = new List<SourceEntry>();
        foreach (var path in paths)
            try
            {
                entries.Add(await source.GetMetadataAsync(path, cancellationToken));
            }
            catch (LibraryException failure) when (failure.Code is "not_found" or "invalid_path")
            {
                skipped.Add(new SkippedItem(path, failure.Message));
            }
        return entries;
    }

    /// <summary>What a walk found: the files to go through, and how much was already here.</summary>
    private sealed record Collected(IReadOnlyList<SourceEntry> Files, int HomeFiles, long HomeBytes);

    /// <summary>Flattens files and folders into the ordinary files worth importing.</summary>
    private async Task<Collected> CollectAsync(
        IImportSource source, IReadOnlyList<SourceEntry> chosen, Func<SourceEntry, bool> alreadyHome,
        List<SkippedItem> skipped, CancellationToken cancellationToken)
    {
        var files = new List<SourceEntry>();
        var homeFiles = 0;
        long homeBytes = 0;
        Collected Done() => new(files, homeFiles, homeBytes);
        // Takes one file in, or says to stop here. A file already here is counted rather than kept,
        // and doesn't count towards the cap, which is what lets adding the same again carry on past
        // it. Stopping quietly at the cap would read as a complete import, so what was left is said.
        bool StopAt(SourceEntry file)
        {
            if (alreadyHome(file))
            {
                homeFiles++;
                homeBytes += file.Size ?? 0;
                return false;
            }
            if (files.Count < MaxEntries)
            {
                files.Add(file);
                return false;
            }
            logger.LogWarning("{Provider} import has more than {MaxEntries} new files, so it stopped at {RemotePath}",
                source.ProviderId, MaxEntries, file.DisplayPath);
            skipped.Add(new SkippedItem(file.DisplayPath,
                $"Uncloud brings at most {MaxEntries:N0} new files at a time, so the rest wasn’t visited. Add the same again to bring the rest."));
            return true;
        }

        var folders = new Queue<string>();
        foreach (var entry in chosen)
        {
            // A file chosen by itself is fetched as asked; a hidden one is refused on the way in.
            if (entry.IsFolder) folders.Enqueue(entry.Path);
            else if (StopAt(entry)) return Done();
        }
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
                if (StopAt(child)) return Done();
            }
        }
        return Done();
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
        IImportSource source, string root, IReadOnlySet<string> alreadyHome, SourceEntry entry,
        CancellationToken cancellationToken)
    {
        if (log.Contains(root, source.ProviderId, entry.Path)) return null;

        var localPath = DestinationFor(source, entry);
        var fullPath = PathPolicy.Resolve(root, localPath);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            // Something Uncloud brought home from somewhere else is not a problem to report. Two
            // places are allowed to share a destination, and often should: a Dropbox folder synced
            // onto this computer and the same account online are the same files, and a person who
            // uses both wants one copy, not a page of warnings about the second.
            return alreadyHome.Contains(localPath)
                ? null
                : throw new LibraryException(
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
