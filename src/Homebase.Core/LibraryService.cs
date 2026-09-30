namespace Homebase.Core;

/// <summary>
/// One account's files. The root is fixed when this is made, from the session the request was
/// authenticated with, so no caller can point it at somebody else's folder. Each account gets
/// its own instance and its own gate, so one person browsing never holds up another.
/// </summary>
public sealed partial class LibraryService(string root, MetadataIndex index) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Copying, moving and deleting take turns with each other but not with browsing: a large copy
    // must not leave the folder unreadable while it runs. An import takes a turn too, to say where
    // it is about to write; see BeginImportAsync.
    private readonly SemaphoreSlim _edits = new(1, 1);
    // Where an import is writing, relative to the root, or null while none is.
    private volatile string? _arrivingIn;

    private sealed record Item(string Relative, string FullPath, bool IsDirectory)
    {
        public string Name => Path.GetFileName(FullPath);
    }

    public string Root => root;

    public LibraryState State => new(root, new DirectoryInfo(root).Name);

    /// <summary>
    /// Makes the metadata database if this folder hasn't been opened before, and lets go of
    /// whatever has been in the bin past its time rather than waiting for somebody to look.
    /// </summary>
    public void Initialize()
    {
        index.Initialize(root);
        // The bin is tidied again whenever it is opened or added to, so a folder that won't answer
        // now is no reason to refuse to open the library.
        try { Purge(BinFolder()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or LibraryException) { }
    }

    public async Task<DirectoryListing> BrowseAsync(string? relativePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fullPath = PathPolicy.Resolve(root, relativePath);
            if (!Directory.Exists(fullPath)) throw new LibraryException("This folder is no longer available.", "not_found");
            var normalized = Relative(fullPath);
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

    /// <summary>
    /// Marks the folder an import is about to write into, so edits keep out of it until
    /// <see cref="EndImport"/>. It takes the edits' own turn to do it: an edit already under way
    /// finishes first, and every edit after it sees the folder is taken. Asking a running job
    /// instead left a gap between an edit finding the folder free and an import starting to fill it.
    /// </summary>
    public async Task BeginImportAsync(string destination, CancellationToken cancellationToken)
    {
        await _edits.WaitAsync(cancellationToken);
        try { _arrivingIn = destination; }
        finally { _edits.Release(); }
    }

    /// <summary>
    /// Frees the folder again. It doesn't wait for a turn: an edit elsewhere may run for a while,
    /// and letting go only ever allows more.
    /// </summary>
    public void EndImport() => _arrivingIn = null;

    /// <summary>
    /// Copies files and folders into another folder, leaving the originals where they are. Nothing
    /// is ever overwritten: a copy whose name is taken arrives as “name copy”, the way Finder keeps
    /// both. Hidden entries and links inside a folder are left behind, as they are when browsing.
    /// </summary>
    public async Task<IReadOnlyList<EditedEntry>> CopyAsync(
        IReadOnlyList<string>? paths, string? destination, CancellationToken cancellationToken)
    {
        await _edits.WaitAsync(cancellationToken);
        try
        {
            var items = Items(paths);
            var folder = Folder(destination);
            AwayFromImport(items, folder, "copied");
            var needed = items.Sum(item => Measure(item, cancellationToken));
            var free = Storage.For(root)?.FreeBytes;
            if (!Storage.Fits(needed, free))
                throw new LibraryException(
                    $"This would copy {Storage.Describe(needed)} and only {Storage.Describe(free!.Value)} is free on "
                    + "your Uncloud drive. Make some room, or copy less at once.", "unavailable");

            var metadata = Path.GetDirectoryName(PathPolicy.PrepareMetadata(root))!;
            var copied = new List<EditedEntry>();
            foreach (var item in items)
            {
                // Built out of sight in Uncloud's own folder and moved into place whole, so a copy
                // that fails or is abandoned partway never leaves half a folder behind. It is on the
                // same drive, so the final move is a rename.
                var temporary = Path.Combine(metadata, $"copy.{Guid.NewGuid():N}.tmp");
                PathPolicy.RejectLink(temporary);
                try
                {
                    if (item.IsDirectory) CopyFolder(item.FullPath, temporary, cancellationToken);
                    else CopyFile(item.FullPath, temporary);
                    var name = Place(temporary, folder, item.Name, item.IsDirectory, copy: true);
                    copied.Add(new EditedEntry(item.Relative, Join(folder, name)));
                }
                finally
                {
                    if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
                    else if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            return copied;
        }
        finally { _edits.Release(); }
    }

    /// <summary>
    /// Moves files and folders into another folder. A name already taken there is kept, and the
    /// arrival becomes “name 2”. Something already in that folder stays where it is.
    /// </summary>
    /// <param name="synced">
    /// The folders this account syncs with its computers, relative to its root. One of those, or a
    /// folder holding one, can't be moved: Syncthing would lose the folder it was told to keep.
    /// </param>
    public async Task<IReadOnlyList<EditedEntry>> MoveAsync(
        IReadOnlyList<string>? paths, string? destination, IReadOnlyCollection<string> synced, CancellationToken cancellationToken)
    {
        await _edits.WaitAsync(cancellationToken);
        try
        {
            var items = Items(paths);
            var folder = Folder(destination);
            AwayFromImport(items, folder, "moved");
            foreach (var item in items)
            {
                if (item.IsDirectory && (folder == item.FullPath || folder.StartsWith(item.FullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                    throw new LibraryException($"“{item.Name}” can’t be moved into itself.");
                KeepSynced(item, synced, "moved");
            }

            var moved = new List<EditedEntry>();
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetDirectoryName(item.FullPath) == folder) continue;
                var name = Place(item.FullPath, folder, item.Name, item.IsDirectory, copy: false);
                moved.Add(new EditedEntry(item.Relative, Join(folder, name)));
            }
            return moved;
        }
        finally { _edits.Release(); }
    }

    /// <summary>
    /// Deletes files, and folders with everything in them, into the bin: gone from My files at
    /// once, and for good after <see cref="KeepDeletedFor"/> unless put back first.
    /// </summary>
    /// <param name="synced">As for <see cref="MoveAsync"/>: a synced folder, or one holding one, stays.</param>
    public async Task<int> DeleteAsync(
        IReadOnlyList<string>? paths, IReadOnlyCollection<string> synced, CancellationToken cancellationToken)
    {
        await _edits.WaitAsync(cancellationToken);
        try
        {
            var items = Items(paths);
            AwayFromImport(items, null, "deleted");
            foreach (var item in items) KeepSynced(item, synced, "deleted");
            var bin = BinFolder();
            Purge(bin);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ToBin(bin, item);
            }
            return items.Count;
        }
        finally { _edits.Release(); }
    }

    /// <summary>
    /// What a request names, checked the way browsing checks it and required to exist. Something
    /// inside a folder that is also named goes wherever the folder goes, so it is dropped here
    /// rather than found missing halfway through.
    /// </summary>
    private IReadOnlyList<Item> Items(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) throw new LibraryException("Choose at least one file or folder.");
        var items = new List<Item>();
        foreach (var path in paths)
        {
            var fullPath = PathPolicy.Resolve(root, path);
            var relative = Relative(fullPath);
            if (relative.Length == 0) throw new LibraryException("Choose files or folders inside My files.");
            var isDirectory = Directory.Exists(fullPath);
            if (!isDirectory && !File.Exists(fullPath))
                throw new LibraryException($"“{Path.GetFileName(fullPath)}” is no longer here. Refresh and try again.", "not_found");
            if (items.All(item => item.FullPath != fullPath)) items.Add(new Item(relative, fullPath, isDirectory));
        }
        return items.Where(item => !items.Any(other => other.IsDirectory
            && item.FullPath.StartsWith(other.FullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))).ToArray();
    }

    private string Folder(string? destination)
    {
        var fullPath = PathPolicy.Resolve(root, destination);
        return Directory.Exists(fullPath)
            ? fullPath
            : throw new LibraryException("That folder is no longer here. Refresh and try again.", "not_found");
    }

    /// <summary>
    /// Refuses an edit that reaches the folder an import is writing into. The import makes the
    /// folders it needs as it goes, so moving or deleting there would be undone file by file — the
    /// folder brought back, or its files split between two places — and a copy would catch it half
    /// full. Everything else in the library can be changed meanwhile.
    /// </summary>
    private void AwayFromImport(IReadOnlyList<Item> items, string? destination, string verb)
    {
        if (_arrivingIn is not { Length: > 0 } area) return;
        var into = destination is not null && Within(Relative(destination), area);
        if (!into && !items.Any(item => Within(item.Relative, area) || Within(area, item.Relative))) return;
        var name = area[(area.LastIndexOf('/') + 1)..];
        throw new LibraryException(
            $"Uncloud is still bringing files into “{name}”, so nothing there can be {verb} yet. "
            + "Try again once that’s finished, or stop it.", "busy");
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    private static bool Within(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    private static void KeepSynced(Item item, IReadOnlyCollection<string> synced, string verb)
    {
        foreach (var folder in synced)
        {
            if (folder.Equals(item.Relative, StringComparison.OrdinalIgnoreCase))
                throw new LibraryException(
                    $"“{item.Name}” syncs with your computers, so it can’t be {verb} here. Stop syncing it in My computers first.",
                    "conflict");
            if (folder.StartsWith(item.Relative + "/", StringComparison.OrdinalIgnoreCase))
            {
                var inside = folder[(folder.LastIndexOf('/') + 1)..];
                throw new LibraryException(
                    $"“{item.Name}” holds “{inside}”, which syncs with your computers, so it can’t be {verb} here. "
                    + $"Stop syncing “{inside}” in My computers first.", "conflict");
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="from"/> into <paramref name="folder"/> under the first free name, and
    /// says which. Neither move overwrites, so something arriving at that name in the meantime wins
    /// and the next name is tried.
    /// </summary>
    private static string Place(string from, string folder, string name, bool isDirectory, bool copy)
    {
        while (true)
        {
            var candidate = FreeName(folder, name, isDirectory, copy);
            var to = Path.Combine(folder, candidate);
            try
            {
                if (isDirectory) Directory.Move(from, to);
                else File.Move(from, to, overwrite: false);
                return candidate;
            }
            catch (IOException) when (Taken(to)) { }
        }
    }

    /// <summary>
    /// The name itself when it is free, and otherwise the next of “name copy”, “name copy 2”… for
    /// a copy or “name 2”, “name 3”… for a move, with a file's extension kept at the end.
    /// </summary>
    private static string FreeName(string folder, string name, bool isDirectory, bool copy)
    {
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isDirectory ? "" : Path.GetExtension(name);
        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? name
                : copy ? $"{stem} copy{(attempt == 2 ? "" : $" {attempt - 1}")}{extension}"
                : $"{stem} {attempt}{extension}";
            if (!Taken(Path.Combine(folder, candidate))) return candidate;
        }
    }

    // A dangling link isn't there as far as File.Exists is concerned, but its name is still taken.
    private static bool Taken(string path) =>
        File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static readonly EnumerationOptions Children = new() { IgnoreInaccessible = false, AttributesToSkip = 0 };

    /// <summary>What browsing would leave out of a folder, and so what copying it leaves behind.</summary>
    private static bool Unseen(FileSystemInfo entry) =>
        entry.Name.StartsWith('.') || entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static long Measure(Item item, CancellationToken cancellationToken)
    {
        if (!item.IsDirectory) return new FileInfo(item.FullPath).Length;
        long total = 0;
        var folders = new Stack<DirectoryInfo>([new DirectoryInfo(item.FullPath)]);
        while (folders.TryPop(out var folder))
            foreach (var entry in folder.EnumerateFileSystemInfos("*", Children))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Unseen(entry)) continue;
                if (entry is DirectoryInfo directory) folders.Push(directory);
                else total += ((FileInfo)entry).Length;
            }
        return total;
    }

    private static void CopyFolder(string from, string to, CancellationToken cancellationToken)
    {
        var source = new DirectoryInfo(from);
        Directory.CreateDirectory(to);
        foreach (var entry in source.EnumerateFileSystemInfos("*", Children))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Unseen(entry)) continue;
            var target = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo) CopyFolder(entry.FullName, target, cancellationToken);
            else CopyFile(entry.FullName, target);
        }
        // Last, because filling the folder is itself a change to it.
        Directory.SetLastWriteTimeUtc(to, source.LastWriteTimeUtc);
    }

    /// <summary>A copy keeps the date it was last changed, which is the date people sort by.</summary>
    private static void CopyFile(string from, string to)
    {
        File.Copy(from, to, overwrite: false);
        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
    }

    private string Relative(string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        return relative == "." ? "" : relative;
    }

    private string Join(string folder, string name) => Relative(Path.Combine(folder, name));

    public void Dispose()
    {
        _gate.Dispose();
        _edits.Dispose();
    }
}
