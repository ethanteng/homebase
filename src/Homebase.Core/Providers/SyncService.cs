using System.Security.Cryptography;

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

            await DownloadAsync(root, entry, localPath, fullPath, replacing: null, cancellationToken);
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
            var remoteKnown = false;
            try
            {
                remoteRev = (await dropbox.GetMetadataAsync(file.RemotePath, cancellationToken)).Rev;
                remoteKnown = true;
            }
            catch (Exception failure) when (failure is LibraryException or HttpRequestException)
            {
                // A failed check is reported as unknown. Saying "up to date" without having asked
                // Dropbox would be the one answer that is never safe to guess.
            }
            statuses.Add(new SyncedFileStatus(file, StateFor(root, file, remoteRev, remoteKnown), remoteRev));
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
                // One unreachable or deleted file must not strand every file after it.
                try
                {
                    outcomes.Add(await RefreshOneAsync(root, file, cancellationToken));
                }
                catch (Exception failure) when (failure is LibraryException or HttpRequestException)
                {
                    outcomes.Add(new SyncOutcome(file.LocalPath,
                        failure is LibraryException { Code: "conflict" } ? SyncState.LocalEdited : SyncState.RemoteUnavailable,
                        false, failure.Message));
                }
            }
            return outcomes;
        }
        finally { _gate.Release(); }
    }

    private async Task<SyncOutcome> RefreshOneAsync(string root, SyncedFile file, CancellationToken cancellationToken)
    {
        var entry = await dropbox.GetMetadataAsync(file.RemotePath, cancellationToken);
        var state = StateFor(root, file, entry.Rev, remoteKnown: true);
        if (state == SyncState.LocalEdited)
            return new SyncOutcome(file.LocalPath, state, false,
                "You changed this copy after Homebase wrote it, so it was left as it is.");
        if (state == SyncState.Current)
            return new SyncOutcome(file.LocalPath, state, false, null);

        var fullPath = PathPolicy.Resolve(root, file.LocalPath);
        await DownloadAsync(root, entry, file.LocalPath, fullPath, file, cancellationToken);
        return new SyncOutcome(file.LocalPath, SyncState.Current, true,
            state == SyncState.LocalMissing ? "The local copy was missing, so it was downloaded again." : null);
    }

    public void Forget(string remotePath) => store.Remove(RequireRoot(), Provider, remotePath);

    /// <summary>
    /// The cheap check, for display: size and timestamp only, so polling doesn't read every
    /// tracked file. The exact check happens in <see cref="StillOursAsync"/>, immediately before
    /// anything is overwritten.
    /// </summary>
    private static SyncState StateFor(string root, SyncedFile file, string? remoteRev, bool remoteKnown)
    {
        var info = new FileInfo(FullPath(root, file));
        if (!info.Exists) return SyncState.LocalMissing;
        var drift = file.LocalModifiedAt - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (info.Length != file.LocalSize || drift.Duration() > TimestampTolerance) return SyncState.LocalEdited;
        if (!remoteKnown) return SyncState.RemoteUnavailable;
        return remoteRev != file.RemoteRev ? SyncState.RemoteChanged : SyncState.Current;
    }

    /// <summary>
    /// Whether the file on disk is still byte-for-byte the one Homebase wrote. Size and timestamp
    /// can both match a real edit on a volume with coarse timestamps, so before destroying a
    /// file the bytes themselves decide.
    /// </summary>
    private static async Task<bool> StillOursAsync(string root, SyncedFile file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(FullPath(root, file));
        if (!info.Exists) return true;
        if (info.Length != file.LocalSize) return false;
        if (file.LocalHash.Length == 0) return true;
        return await HashAsync(info.FullName, cancellationToken) == file.LocalHash;
    }

    /// <summary>Downloads beside the destination and moves into place, so a failed transfer
    /// can never leave a half-written file where a whole one belongs.</summary>
    private async Task DownloadAsync(string root, DropboxEntry entry, string localPath, string fullPath,
        SyncedFile? replacing, CancellationToken cancellationToken)
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
            var hash = await HashAsync(temporary, cancellationToken);

            // A download takes as long as it takes, so the destination is judged now rather than
            // on what it looked like before the transfer started.
            if (replacing is null)
            {
                PathPolicy.RejectLink(fullPath);
                try
                {
                    File.Move(temporary, fullPath);
                }
                catch (IOException) when (File.Exists(fullPath))
                {
                    throw new LibraryException(
                        $"Something else created {localPath} while Homebase was downloading it, so it was left alone.", "conflict");
                }
            }
            else
            {
                if (!await StillOursAsync(root, replacing, cancellationToken))
                    throw new LibraryException(
                        $"{localPath} changed while Homebase was downloading, so your copy was left as it is.", "conflict");
                PathPolicy.RejectLink(fullPath);
                File.Move(temporary, fullPath, overwrite: true);
            }

            var written = new FileInfo(fullPath);
            store.Save(root, new SyncedFile(
                Provider, entry.PathLower, entry.Rev ?? "", localPath, entry.Size ?? written.Length,
                written.Length, new DateTimeOffset(written.LastWriteTimeUtc, TimeSpan.Zero), hash, DateTimeOffset.UtcNow));
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

    private static string FullPath(string root, SyncedFile file) =>
        Path.Combine(root, file.LocalPath.Replace('/', Path.DirectorySeparatorChar));

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
