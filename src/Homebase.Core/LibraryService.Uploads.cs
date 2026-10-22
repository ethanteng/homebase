using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Homebase.Core;

/// <summary>
/// Files sent from the browser of whoever is signed in: from the computer or phone in front of them,
/// into their own folder on this host and nobody else's.
///
/// An upload arrives a file per request, so one that is stopped, or whose browser goes away, would
/// otherwise leave half a folder behind. Each is gathered instead in a folder of its own under the
/// account's <c>.homebase</c> — out of sight, never copied or synced, and on the same drive — and
/// moved into My files whole once the last file is in, the way a copy is. One whose page went away
/// partway is kept for a while, so that adding the same files again sends only what hadn't arrived.
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

    /// <summary>How often, in bytes written, a file arriving asks the drive again how much room is left.</summary>
    private const int AskDriveEvery = 1024 * 1024;

    /// <summary>How many files one account can have arriving at once.</summary>
    public const int ReceivingAtOnce = 8;

    private readonly SemaphoreSlim _receiving = new(ReceivingAtOnce, ReceivingAtOnce);

    /// <summary>How many files are arriving now for each upload, by its id.</summary>
    private readonly ConcurrentDictionary<string, int> _arriving = new();

    /// <summary>
    /// How long a file can go with nothing more of it arriving before it's given up on. A connection
    /// can go without either end hearing of it, and the file would otherwise wait for the rest of
    /// itself for ever, holding one of the account's turns. Shorter than the page waits before trying
    /// a file again, so the turn is back by then.
    /// </summary>
    public TimeSpan StalledAfter { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Where an upload is going, kept in it so it can still be put there once the page that began it has gone.</summary>
    private sealed record Intent(string? Destination, bool Merge);

    /// <summary>The name of the file in an upload saying where it's going: hidden, so it's never sent or placed.</summary>
    private const string IntentFile = ".upload.json";

    /// <summary>Starts an upload of files nothing is known of beforehand, which is never one picked up again.</summary>
    /// <returns>The id every file in this upload is sent under.</returns>
    public async Task<string> BeginUploadAsync(string? destination, long bytes, CancellationToken cancellationToken) =>
        (await BeginUploadAsync(destination, bytes, merge: false, files: null, cancellationToken)).Id;

    /// <summary>
    /// Starts an upload into <paramref name="destination"/>, refusing up front one the drive has no
    /// room for or that would land where an import is writing, rather than after it has been sent.
    /// When the same files were being added before and didn't all arrive, that upload is picked up
    /// instead, with which of them are there already, so that only the rest is sent.
    /// </summary>
    /// <param name="bytes">What the browser says it is about to send, all of it together.</param>
    /// <param name="merge">Whether it's to be finished by bringing what's there up to date, kept for after the page has gone.</param>
    /// <param name="files">What the browser is about to send, which is how an upload of the same files is found.</param>
    public async Task<BegunUpload> BeginUploadAsync(
        string? destination, long bytes, bool merge, IReadOnlyList<UploadFile>? files, CancellationToken cancellationToken)
    {
        await _edits.WaitAsync(cancellationToken);
        try
        {
            var folder = Folder(destination);
            AwayFromImport([], folder, "added");
            var uploads = UploadsFolder();
            PurgeUploads(uploads);
            var resumed = files is null ? null : Resumable(uploads, files);
            // What arrived before is on the drive already; only the rest needs room.
            NeedRoomFor(Math.Max(bytes - (resumed?.Bytes ?? 0), 0), "add less at once");
            var id = resumed?.Id ?? Guid.NewGuid().ToString("N");
            var upload = Path.Combine(uploads, id);
            Directory.CreateDirectory(upload);
            // Picked up again, it goes where it's being added now and is finished the way chosen now.
            var intent = Path.Combine(upload, IntentFile);
            PathPolicy.RejectLink(intent);
            File.WriteAllText(intent, JsonSerializer.Serialize(new Intent(Relative(folder), merge), RecordFormat));
            return new BegunUpload(id, resumed?.Arrived ?? []);
        }
        finally { _edits.Release(); }
    }

    /// <summary>
    /// An upload nobody finished — its page closed partway, or its connection gave out — every file of
    /// which is among <paramref name="files"/>, the same size and last changed at the same time: the
    /// same files being added again, of which only the rest need be sent. Where it was going before
    /// doesn't matter; the same files are the same files. One with a file arriving now is a page still
    /// sending it, and is left to that page.
    /// </summary>
    private (string Id, List<string> Arrived, long Bytes)? Resumable(string uploads, IReadOnlyList<UploadFile> files)
    {
        // Compared as the same characters however they were composed: a Mac's names often aren't
        // composed the way the browser's are.
        var sending = new Dictionary<string, UploadFile>(StringComparer.Ordinal);
        foreach (var file in files)
            if (file.Path is { Length: > 0 } path) sending.TryAdd(path.Normalize(NormalizationForm.FormC), file);
        if (sending.Count == 0) return null;
        // The latest first: the one most likely to be what's being added again.
        var candidates = new DirectoryInfo(uploads).EnumerateDirectories()
            .Where(upload => UploadId().IsMatch(upload.Name) && upload.LinkTarget is null && !Arriving(upload.Name))
            .OrderByDescending(upload => upload.LastWriteTimeUtc)
            .ToArray();
        foreach (var upload in candidates)
        {
            List<string>? arrived = [];
            long bytes = 0;
            try
            {
                foreach (var (path, there) in ArrivedIn(upload.FullName))
                {
                    if (!sending.TryGetValue(path.Normalize(NormalizationForm.FormC), out var file)
                        || file.Bytes != there.Length || When(file.Modified) is not { } changed
                        || (there.LastWriteTimeUtc - changed.UtcDateTime).Duration() > SameTime)
                    {
                        // Something in it isn't being sent now, so finishing it would add that too.
                        arrived = null;
                        break;
                    }
                    arrived.Add(file.Path!);
                    bytes += there.Length;
                }
            }
            // Finished or thrown away while it was looked at.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            if (arrived is { Count: > 0 }) return (upload.Name, arrived, bytes);
        }
        return null;
    }

    /// <summary>
    /// The uploads begun and never finished, with what of each arrived, for as long as they're kept:
    /// a page closed partway, or a connection that gave out. One with nothing in it isn't listed,
    /// having nothing to keep. One with a file arriving now is still being sent — by another page,
    /// perhaps — and says so.
    /// </summary>
    public IReadOnlyList<UnfinishedUpload> UnfinishedUploads(CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - KeepAbandonedUploadsFor;
        var unfinished = new List<UnfinishedUpload>();
        foreach (var upload in new DirectoryInfo(UploadsFolder()).EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Past its time it's as good as gone: the next upload begun clears it away.
            if (!UploadId().IsMatch(upload.Name) || upload.LinkTarget is not null || upload.LastWriteTimeUtc < cutoff) continue;
            try
            {
                var files = 0;
                long bytes = 0;
                foreach (var (_, file) in ArrivedIn(upload.FullName))
                {
                    files++;
                    bytes += file.Length;
                }
                if (files == 0) continue;
                var names = Gathered(upload.FullName).Select(entry => entry.Name).ToArray();
                Intent? intent = null;
                try { intent = JsonSerializer.Deserialize<Intent>(File.ReadAllText(Path.Combine(upload.FullName, IntentFile)), RecordFormat); }
                // Begun before uploads said where they were going, it can still be picked up, but not put anywhere by itself.
                catch (Exception error) when (error is FileNotFoundException or JsonException) { }
                var lastArrived = new DateTimeOffset(upload.LastWriteTimeUtc, TimeSpan.Zero);
                unfinished.Add(new UnfinishedUpload(upload.Name, intent?.Destination, intent?.Merge ?? false, names, files, bytes,
                    lastArrived, lastArrived + KeepAbandonedUploadsFor, Arriving(upload.Name)));
            }
            // Finished or thrown away while it was looked at.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return unfinished.OrderByDescending(upload => upload.LastArrived).ToArray();
    }

    /// <summary>Every file that has arrived whole in an upload, by its path in it: not the parts still arriving, which are hidden.</summary>
    private static IEnumerable<(string Path, FileInfo File)> ArrivedIn(string upload)
    {
        var folders = new Stack<(DirectoryInfo Folder, string Above)>([(new DirectoryInfo(upload), "")]);
        while (folders.TryPop(out var next))
            foreach (var entry in next.Folder.EnumerateFileSystemInfos("*", Children))
            {
                if (Unseen(entry)) continue;
                var path = next.Above.Length == 0 ? entry.Name : $"{next.Above}/{entry.Name}";
                if (entry is DirectoryInfo folder) folders.Push((folder, path));
                else yield return (path, (FileInfo)entry);
            }
    }

    private bool Arriving(string id) => _arriving.TryGetValue(id, out var count) && count > 0;

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
        // Before the path goes anywhere near the drive, which would answer a name longer than it takes
        // with an error that reads as the drive having gone. Another device can allow one: a name in
        // Chinese, say, takes three bytes a character here.
        foreach (var part in (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (Encoding.UTF8.GetByteCount(part) > MaxNameBytes)
                throw new LibraryException($"“{part}” has a name too long for this Uncloud’s drive, so it can’t be added.");
        var target = PathPolicy.Resolve(upload, path,
            "This upload is no longer here. Start it again.",
            "A file can only go inside the folder it’s being added to.",
            "Names starting with a dot are hidden in Uncloud, so they can’t be added.");
        if (target == upload) throw new LibraryException("Say which file this is.");
        if (Directory.Exists(target))
            throw new LibraryException($"“{Path.GetFileName(target)}” is a folder in this upload already.", "conflict");
        // A drive that ignores capitals — a Mac's, usually — has one place for “photo.jpg” and
        // “Photo.jpg”, which a computer that doesn't can send side by side. The second is refused
        // rather than written over the first. The same name sent again is only ever a retry.
        if (TakenByAnother(target)) throw Indistinct(target);

        // A few at a time, which is all the page ever sends. Each file asks the drive how much room is
        // left only every so often, so without a limit enough small ones at once could all see the
        // same free space and, between them, take the room held back.
        if (!_receiving.Wait(0))
            throw new LibraryException("Too many files are arriving at once. Wait for some to finish, then try again.", "busy");
        // Counted, so one still arriving is never picked up by another page, nor said to have stopped.
        _arriving.AddOrUpdate(id!, 1, (_, count) => count + 1);
        try
        {
            // A retry: what arrived of this file before is replaced, so it goes first and its room with it.
            if (File.Exists(target)) File.Delete(target);
            if (length is { } expected) NeedRoomFor(expected, "add less at once");

            // Counted as it arrives as well as checked up front: a request that doesn't say how long it
            // is has no length to check. And the drive is asked again as it fills, because files arriving
            // side by side — or an import, or a laptop syncing — each started from the same free space,
            // and only the drive knows what they've taken between them. Nothing goes into the room held back.
            var free = Space(root)?.FreeBytes;
            var room = Storage.Room(free);
            var keep = free - room;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Written somewhere of its own and moved into place once whole, so nothing else ever sees
            // it half-written: not a retry of it still arriving, which writes somewhere of its own too,
            // nor finishing the upload, which leaves this hidden folder behind.
            var parts = Path.Combine(upload, ".parts");
            PathPolicy.RejectLink(parts);
            Directory.CreateDirectory(parts);
            var arriving = Path.Combine(parts, Guid.NewGuid().ToString("N"));
            long written = 0;
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                await using (var file = new FileStream(arriving, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    var buffer = new byte[81920];
                    long sinceAsked = 0;
                    for (;;)
                    {
                        quiet.CancelAfter(StalledAfter);
                        int read;
                        try { read = await content.ReadAsync(buffer, quiet.Token); }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new LibraryException(
                                $"Nothing more of “{Path.GetFileName(target)}” arrived for a while. Try adding it again.", "timeout");
                        }
                        if (read == 0) break;
                        written += read;
                        sinceAsked += read;
                        var full = written > room;
                        if (sinceAsked >= AskDriveEvery)
                        {
                            sinceAsked = 0;
                            full |= Space(root)?.FreeBytes - read < keep;
                            // Still arriving, however long one file takes, so not one to clear away.
                            Directory.SetLastWriteTimeUtc(upload, DateTime.UtcNow);
                        }
                        if (full)
                            throw new LibraryException(
                                $"There isn’t room on your Uncloud drive for “{Path.GetFileName(target)}”. "
                                + "Make some room, or add less at once.", "unavailable");
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }
                if (length is { } promised && written != promised)
                    throw new LibraryException(
                        $"Only part of “{Path.GetFileName(target)}” arrived. Try adding it again.", "unavailable");
                if (modified is { } changed) File.SetLastWriteTimeUtc(arriving, changed.UtcDateTime);
                // Asked again now it's whole: one differing only in capitals may have arrived meanwhile.
                if (TakenByAnother(target)) throw Indistinct(target);
                File.Move(arriving, target, overwrite: true);
            }
            catch
            {
                // Half a file is worse than none: it would look like the whole thing once placed. Its
                // folder is gone already if the upload was stopped or finished while it arrived.
                try { File.Delete(arriving); }
                catch (DirectoryNotFoundException) { }
                throw;
            }
            // Somebody is still sending to it, so it isn't one to clear away.
            Directory.SetLastWriteTimeUtc(upload, DateTime.UtcNow);
            return written;
        }
        finally
        {
            if (_arriving.AddOrUpdate(id!, 0, (_, count) => count - 1) == 0) _arriving.TryRemove(new(id!, 0));
            _receiving.Release();
        }
    }

    /// <summary>When the browser says a file was last changed, unless it's a time it can't have meant.</summary>
    private static DateTimeOffset? When(long? modified) =>
        modified is > 0 and < 253402300800000 ? DateTimeOffset.FromUnixTimeMilliseconds(modified.Value) : null;

    /// <summary>How far apart two times a file was last changed can be and still be the same time: a
    /// drive formatted for Windows keeps them only to two seconds.</summary>
    private static readonly TimeSpan SameTime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Says which of the files an upload is about to send are already in <paramref name="destination"/>,
    /// so that adding a folder again need only send what is new or has changed in it. A file counts as
    /// the same when its size and the time it was last changed both match, the way rsync judges it:
    /// reading every file on both sides to compare them would take longer than sending them.
    /// </summary>
    /// <param name="files">What the browser is about to send, each by where it sits in the upload.</param>
    public UploadComparison CompareUpload(string? destination, IReadOnlyList<UploadFile>? files, CancellationToken cancellationToken)
    {
        var folder = Folder(destination);
        List<string> same = [], changed = [], newer = [];
        var tops = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Path is not { Length: > 0 } path) continue;
            if (path.IndexOf('/') is > 0 and var slash) tops.Add(path[..slash]);
            FileInfo there;
            // A path Uncloud won't keep is refused on its own when it's sent; here it simply isn't there.
            try { there = new FileInfo(PathPolicy.Resolve(folder, path)); }
            catch (Exception error) when (error is LibraryException or IOException) { continue; }
            if (!there.Exists) continue;
            var sent = When(file.Modified);
            // Without a time there's no telling it's the same, nor which is newer; sending it replaces
            // the one there, which the bin keeps.
            if (sent is null) changed.Add(path);
            else if (there.Length == file.Bytes && (there.LastWriteTimeUtc - sent.Value.UtcDateTime).Duration() <= SameTime)
                same.Add(path);
            // Changed here since: sending this one would put back what somebody changed it from.
            else if (there.LastWriteTimeUtc - sent.Value.UtcDateTime > SameTime) newer.Add(path);
            else changed.Add(path);
        }
        var folders = tops.Where(top =>
        {
            try { return Directory.Exists(PathPolicy.Resolve(folder, top)); }
            catch (LibraryException) { return false; }
        }).Order(StringComparer.Ordinal).ToArray();
        return new UploadComparison(same, changed, newer, folders);
    }

    /// <summary>
    /// Moves everything an upload gathered into <paramref name="destination"/>. Nothing already there
    /// is overwritten: something arriving under a name that's taken becomes “name 2”, a folder
    /// included, the way Finder keeps both — unless the upload is to be merged, when a folder joins
    /// the one already there and a file takes the place of the one there, which goes to the bin.
    /// </summary>
    /// <param name="merge">
    /// Whether this is a folder added again, to bring what is new or changed in it into the copy
    /// already here. Only what differed was sent, so whatever wasn't is left as it is.
    /// </param>
    public async Task<FinishedUpload> FinishUploadAsync(
        string? id, string? destination, bool merge, CancellationToken cancellationToken)
    {
        await _edits.WaitAsync(cancellationToken);
        try
        {
            var upload = Upload(id);
            var folder = Folder(destination);
            AwayFromImport([], folder, "added");
            var placed = new List<EditedEntry>();
            var replaced = 0;
            // Not given up partway, even if whoever asked has gone: each move is a rename, so this is
            // quick, and stopping between two would leave some of the upload in My files and the rest
            // waiting to be thrown away.
            if (merge) replaced = Merge(upload, folder, "", BinFolder(), placed);
            else
                foreach (var entry in Gathered(upload))
                {
                    var name = Place(entry.FullName, folder, entry.Name, entry is DirectoryInfo, copy: false);
                    placed.Add(new EditedEntry(entry.Name, Join(folder, name)));
                }
            // A recursive delete removes a link it meets rather than following it.
            Directory.Delete(upload, recursive: true);
            return new FinishedUpload(placed, replaced);
        }
        finally { _edits.Release(); }
    }

    /// <summary>
    /// Moves what an upload gathered in <paramref name="from"/> into <paramref name="into"/>, joining a
    /// folder already there rather than arriving beside it, and taking the place of a file already
    /// there, which goes to the bin. Something arriving where the other kind is — a folder where there's
    /// a file, say — or where there's a link still arrives beside it, under a new name, and so does a
    /// file arriving where one was changed more recently than it.
    /// </summary>
    /// <param name="above">Where <paramref name="from"/> is in the upload; empty for the top of it.</param>
    /// <param name="placed">Where each thing at the top of the upload went, and anything deeper that took a new name.</param>
    /// <returns>How many files already there were replaced.</returns>
    private int Merge(string from, string into, string above, string bin, List<EditedEntry> placed)
    {
        var replaced = 0;
        foreach (var entry in Gathered(from))
        {
            var path = above.Length == 0 ? entry.Name : $"{above}/{entry.Name}";
            var target = Path.Combine(into, entry.Name);
            var name = entry.Name;
            var joining = entry is DirectoryInfo && Directory.Exists(target) && !IsLink(target);
            if (!joining)
            {
                // Asked again now, in the edits' turn: the comparison was made before anything was sent,
                // and a laptop syncing, or another browser, may have changed the one there since. One
                // changed more recently than what arrived is kept, and what arrived goes beside it.
                var replacing = entry is FileInfo arrived && File.Exists(target) && !IsLink(target)
                    && File.GetLastWriteTimeUtc(target) - arrived.LastWriteTimeUtc <= SameTime;
                // In beside the one it replaces before that goes to the bin, so that something going
                // wrong in between leaves both in the folder rather than neither.
                name = Place(entry.FullName, into, entry.Name, entry is DirectoryInfo, copy: false);
                if (replacing)
                {
                    ToBin(bin, new Item(Relative(target), target, IsDirectory: false));
                    replaced++;
                    try
                    {
                        File.Move(Path.Combine(into, name), target, overwrite: false);
                        name = entry.Name;
                    }
                    // Something took the name meanwhile: what arrived keeps the one it has, and says so.
                    catch (IOException) when (Taken(target)) { }
                }
            }
            if (above.Length == 0 || name != entry.Name) placed.Add(new EditedEntry(path, Join(into, name)));
            if (joining) replaced += Merge(entry.FullName, target, path, bin, placed);
        }
        return replaced;
    }

    /// <summary>What an upload has gathered in one of its folders, in a settled order.</summary>
    private static IEnumerable<FileSystemInfo> Gathered(string folder) =>
        new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Children)
            // Nothing an upload is sent can be hidden or a link; this is only never trusting that.
            .Where(entry => !Unseen(entry))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            // Read whole first, since what is read is then moved away.
            .ToArray();

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

    /// <summary>
    /// Whether the drive answers to <paramref name="target"/> with a file of another name — the same
    /// apart from capitals, or accents written another way — which is some other file in this upload,
    /// not an earlier try at this one. Only looked into when something is there, which is rare.
    /// </summary>
    private static bool TakenByAnother(string target)
    {
        if (!File.Exists(target)) return false;
        var name = Path.GetFileName(target);
        return !Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(target)!)
            .Any(entry => Path.GetFileName(entry) == name);
    }

    private static LibraryException Indistinct(string target) => new(
        $"Another file here is called “{Path.GetFileName(target)}” apart from capitals, and this Uncloud’s drive "
        + "can’t keep both in one folder. Rename one of them and add it again.");

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
