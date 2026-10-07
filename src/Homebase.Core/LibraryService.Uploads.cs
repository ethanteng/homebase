using System.Text.RegularExpressions;

namespace Homebase.Core;

/// <summary>
/// Files sent from the browser of whoever is signed in: from the computer or phone in front of them,
/// into their own folder on this host and nobody else's.
///
/// An upload arrives a file per request, so one that is stopped, or whose browser goes away, would
/// otherwise leave half a folder behind. Each is gathered instead in a folder of its own under the
/// account's <c>.homebase</c> — out of sight, never copied or synced, and on the same drive — and
/// moved into My files whole once the last file is in, the way a copy is.
/// </summary>
public sealed partial class LibraryService
{
    /// <summary>
    /// How long an upload nothing has arrived for is kept. Long enough for a slow connection to
    /// carry on; not so long that a tab closed partway holds on to its share of the drive.
    /// </summary>
    public static readonly TimeSpan KeepAbandonedUploadsFor = TimeSpan.FromDays(1);

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex UploadId();

    /// <summary>
    /// Starts an upload into <paramref name="destination"/>, refusing up front one the drive has no
    /// room for or that would land where an import is writing, rather than after it has been sent.
    /// </summary>
    /// <param name="bytes">What the browser says it is about to send, all of it together.</param>
    /// <returns>The id every file in this upload is sent under.</returns>
    public async Task<string> BeginUploadAsync(string? destination, long bytes, CancellationToken cancellationToken)
    {
        await _edits.WaitAsync(cancellationToken);
        try
        {
            AwayFromImport([], Folder(destination), "added");
            NeedRoomFor(Math.Max(bytes, 0), "add less at once");
            var uploads = UploadsFolder();
            PurgeUploads(uploads);
            var id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(uploads, id));
            return id;
        }
        finally { _edits.Release(); }
    }

    /// <summary>
    /// Keeps one file of an upload, at <paramref name="path"/> inside it: its name alone, or where it
    /// sits in a folder being uploaded. The same rules as anywhere in My files apply to that path.
    /// Sending a file again replaces what arrived of it before, so a failed one can simply be retried.
    /// </summary>
    /// <param name="length">How long the browser says the file is, when it says.</param>
    /// <param name="modified">When the file was last changed where it came from, which is the date people sort by.</param>
    /// <returns>How many bytes arrived.</returns>
    public async Task<long> ReceiveAsync(
        string? id, string? path, Stream content, long? length, DateTimeOffset? modified, CancellationToken cancellationToken)
    {
        // Not a turn of the edits: files arrive several at a time and an upload can take hours, and
        // nothing else reaches into the folder an upload is gathered in.
        var upload = Upload(id);
        var target = PathPolicy.Resolve(upload, path,
            "This upload is no longer here. Start it again.",
            "A file can only go inside the folder it’s being added to.",
            "Names starting with a dot are hidden in Uncloud, so they can’t be added.");
        if (target == upload) throw new LibraryException("Say which file this is.");
        if (length is { } expected) NeedRoomFor(expected, "add less at once");
        if (Directory.Exists(target))
            throw new LibraryException($"“{Path.GetFileName(target)}” is a folder in this upload already.", "conflict");

        // Counted as it arrives as well as checked up front: a request that doesn't say how long it is
        // has no length to check, and nothing else stops it before the drive is full.
        var room = Storage.Room(Space(root)?.FreeBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        long written = 0;
        try
        {
            await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                for (int read; (read = await content.ReadAsync(buffer, cancellationToken)) > 0;)
                {
                    written += read;
                    if (written > room)
                        throw new LibraryException(
                            $"There isn’t room on your Uncloud drive for “{Path.GetFileName(target)}”. "
                            + "Make some room, or add less at once.", "unavailable");
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            if (length is { } promised && written != promised)
                throw new LibraryException(
                    $"Only part of “{Path.GetFileName(target)}” arrived. Try adding it again.", "unavailable");
            if (modified is { } changed) File.SetLastWriteTimeUtc(target, changed.UtcDateTime);
        }
        catch
        {
            // Half a file is worse than none: it would look like the whole thing once placed.
            File.Delete(target);
            throw;
        }
        // Somebody is still sending to it, so it isn't one to clear away.
        Directory.SetLastWriteTimeUtc(upload, DateTime.UtcNow);
        return written;
    }

    /// <summary>
    /// Moves everything an upload gathered into <paramref name="destination"/>. Nothing already there
    /// is overwritten: something arriving under a name that's taken becomes “name 2”, a folder
    /// included, the way Finder keeps both.
    /// </summary>
    public async Task<IReadOnlyList<EditedEntry>> FinishUploadAsync(
        string? id, string? destination, CancellationToken cancellationToken)
    {
        await _edits.WaitAsync(cancellationToken);
        try
        {
            var upload = Upload(id);
            var folder = Folder(destination);
            AwayFromImport([], folder, "added");
            var placed = new List<EditedEntry>();
            // Not given up partway, even if whoever asked has gone: each move is a rename, so this is
            // quick, and stopping between two would leave some of the upload in My files and the rest
            // waiting to be thrown away.
            foreach (var entry in new DirectoryInfo(upload).EnumerateFileSystemInfos("*", Children)
                         .OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                // Nothing an upload is sent can be hidden or a link; this is only never trusting that.
                if (Unseen(entry)) continue;
                var isDirectory = entry is DirectoryInfo;
                var name = Place(entry.FullName, folder, entry.Name, isDirectory, copy: false);
                placed.Add(new EditedEntry(entry.Name, Join(folder, name)));
            }
            // A recursive delete removes a link it meets rather than following it.
            Directory.Delete(upload, recursive: true);
            return placed;
        }
        finally { _edits.Release(); }
    }

    /// <summary>
    /// Throws away an upload and whatever of it had arrived. One that has already gone, finished or
    /// stopped, is no reason to complain: either way it isn't here.
    /// </summary>
    public async Task CancelUploadAsync(string? id, CancellationToken cancellationToken)
    {
        if (id is null || !UploadId().IsMatch(id)) throw new LibraryException("That upload isn’t one Uncloud knows.", "not_found");
        await _edits.WaitAsync(cancellationToken);
        try
        {
            var upload = Path.Combine(UploadsFolder(), id);
            PathPolicy.RejectLink(upload);
            if (Directory.Exists(upload)) Directory.Delete(upload, recursive: true);
        }
        finally { _edits.Release(); }
    }

    /// <summary>Refuses something the drive has no room for, worded as what to do about it.</summary>
    private void NeedRoomFor(long bytes, string instead)
    {
        var free = Space(root)?.FreeBytes;
        if (!Storage.Fits(bytes, free))
            throw new LibraryException(
                $"This would add {Storage.Describe(bytes)} and only {Storage.Describe(free!.Value)} is free on "
                + $"your Uncloud drive. Make some room, or {instead}.", "unavailable");
    }

    /// <summary>
    /// The folder one upload is gathered in. An id is only ever matched and then joined to the
    /// uploads folder, so no request can name a folder of its own choosing.
    /// </summary>
    private string Upload(string? id)
    {
        if (id is null || !UploadId().IsMatch(id))
            throw new LibraryException("That upload isn’t one Uncloud knows.", "not_found");
        var upload = Path.Combine(UploadsFolder(), id);
        PathPolicy.RejectLink(upload);
        return Directory.Exists(upload)
            ? upload
            : throw new LibraryException("This upload is no longer here. Start it again.", "not_found");
    }

    private string UploadsFolder()
    {
        var uploads = Path.Combine(Path.GetDirectoryName(PathPolicy.PrepareMetadata(root))!, "uploads");
        PathPolicy.RejectLink(uploads);
        Directory.CreateDirectory(uploads);
        return uploads;
    }

    /// <summary>Clears away uploads nothing has arrived for in <see cref="KeepAbandonedUploadsFor"/>.</summary>
    private static void PurgeUploads(string uploads)
    {
        var cutoff = DateTime.UtcNow - KeepAbandonedUploadsFor;
        foreach (var upload in new DirectoryInfo(uploads).EnumerateDirectories())
        {
            if (!UploadId().IsMatch(upload.Name) || upload.LastWriteTimeUtc >= cutoff) continue;
            try
            {
                if (upload.LinkTarget is null) upload.Delete(recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Tried again the next time an upload starts.
            }
        }
    }
}
