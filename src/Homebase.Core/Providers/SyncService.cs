namespace Homebase.Core.Providers;

/// <summary>
/// Mirrors chosen provider files into the library, one way: provider to disk.
/// Homebase only ever overwrites a file it wrote itself and still recognises. Anything else —
/// a file already at the destination, or a synced copy edited since Homebase wrote it — is
/// reported and left alone.
/// </summary>
public sealed class SyncService(LibraryService library, SyncedFileStore store, IDropboxApi dropbox)
{
    public const string Provider = "dropbox";
    public const string DestinationPrefix = "Files/Dropbox";

    // External volumes can store coarse timestamps (exFAT rounds to two seconds), so a local
    // edit is judged by a margin rather than exact equality.
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public IReadOnlyList<SyncedFile> Tracked() => store.List(RequireRoot());

    public async Task<SyncOutcome> TrackAsync(string remotePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = RequireRoot();
            var entry = await dropbox.GetMetadataAsync(remotePath, cancellationToken);
            if (entry.IsFolder)
                throw new LibraryException("Choose a file to sync, not a folder.");
            if (store.Find(root, Provider, entry.PathLower) is not null)
                throw new LibraryException("Homebase is already syncing this file.");

            var localPath = DestinationFor(entry);
            var fullPath = PathPolicy.Resolve(root, localPath);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
                throw new LibraryException(
                    $"A file already exists at {localPath}. Homebase won’t overwrite files it didn’t put there.", "conflict");

            await DownloadAsync(root, entry, localPath, fullPath, cancellationToken);
            return new SyncOutcome(localPath, SyncState.Current, true, null);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SyncedFileStatus>> StatusAsync(CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        var statuses = new List<SyncedFileStatus>();
        foreach (var file in store.List(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? remoteRev = null;
            try
            {
                remoteRev = (await dropbox.GetMetadataAsync(file.RemotePath, cancellationToken)).Rev;
            }
            catch (LibraryException)
            {
                // A provider that can't answer right now shouldn't hide the local state below.
            }
            statuses.Add(new SyncedFileStatus(file, LocalState(root, file, remoteRev), remoteRev));
        }
        return statuses;
    }

    public async Task<IReadOnlyList<SyncOutcome>> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = RequireRoot();
            var outcomes = new List<SyncOutcome>();
            foreach (var file in store.List(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = await dropbox.GetMetadataAsync(file.RemotePath, cancellationToken);
                var state = LocalState(root, file, entry.Rev);
                if (state == SyncState.LocalEdited)
                {
                    outcomes.Add(new SyncOutcome(file.LocalPath, state, false,
                        "You changed this copy after Homebase wrote it, so it was left as it is."));
                    continue;
                }
                if (state == SyncState.Current)
                {
                    outcomes.Add(new SyncOutcome(file.LocalPath, state, false, null));
                    continue;
                }
                var fullPath = PathPolicy.Resolve(root, file.LocalPath);
                await DownloadAsync(root, entry, file.LocalPath, fullPath, cancellationToken);
                outcomes.Add(new SyncOutcome(file.LocalPath, SyncState.Current, true,
                    state == SyncState.LocalMissing ? "The local copy was missing, so it was downloaded again." : null));
            }
            return outcomes;
        }
        finally { _gate.Release(); }
    }

    public void Forget(string remotePath) => store.Remove(RequireRoot(), Provider, remotePath);

    private SyncState LocalState(string root, SyncedFile file, string? remoteRev)
    {
        var fullPath = Path.Combine(root, file.LocalPath.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(fullPath);
        if (!info.Exists) return SyncState.LocalMissing;
        var drift = file.LocalModifiedAt - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (info.Length != file.LocalSize || drift.Duration() > TimestampTolerance) return SyncState.LocalEdited;
        if (remoteRev is not null && remoteRev != file.RemoteRev) return SyncState.RemoteChanged;
        return SyncState.Current;
    }

    /// <summary>Downloads beside the destination and moves into place, so a failed transfer
    /// can never leave a half-written file where a whole one belongs.</summary>
    private async Task DownloadAsync(string root, DropboxEntry entry, string localPath, string fullPath, CancellationToken cancellationToken)
    {
        var metadata = Path.GetDirectoryName(PathPolicy.PrepareMetadata(root))!;
        var temporary = Path.Combine(metadata, $"download.{Guid.NewGuid():N}.tmp");
        PathPolicy.RejectLink(temporary);
        try
        {
            var parent = Path.GetDirectoryName(fullPath)!;
            PathPolicy.RejectLink(parent);
            Directory.CreateDirectory(parent);

            await using (var remote = await dropbox.DownloadAsync(entry.PathLower, cancellationToken))
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await remote.CopyToAsync(file, cancellationToken);
            }
            File.Move(temporary, fullPath, overwrite: true);

            var written = new FileInfo(fullPath);
            store.Save(root, new SyncedFile(
                Provider, entry.PathLower, entry.Rev ?? "", localPath, entry.Size ?? written.Length,
                written.Length, new DateTimeOffset(written.LastWriteTimeUtc, TimeSpan.Zero), DateTimeOffset.UtcNow));
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string DestinationFor(DropboxEntry entry)
    {
        var relative = entry.PathDisplay.TrimStart('/');
        if (relative.Length == 0) relative = entry.Name;
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith('.'))
                throw new LibraryException(
                    $"Homebase doesn’t sync hidden files or folders yet, and “{part}” is hidden.", "unsupported");
        return $"{DestinationPrefix}/{relative}";
    }

    private string RequireRoot() =>
        library.State.RootPath ?? throw new LibraryException("Choose your Homebase folder first.", "not_configured");
}
