using System.Collections.Concurrent;
using System.Globalization;

namespace Homebase.Core.Providers;

/// <summary>One zip that has been added to an export's folder, and what it holds.</summary>
/// <param name="Problem">Why it can't be read as a zip at all, or null when it can.</param>
public sealed record ExportZip(string File, string FileName, IReadOnlyList<ZipItem> Items, int Unreadable, string? Problem);

/// <summary>
/// One file an export brings home. <paramref name="Key"/> is which file it is, the same however the
/// export was split into parts and in every export of the same account, so a file is never brought
/// twice; <paramref name="Folder"/> and <paramref name="Name"/> are where it would like to go, which
/// is only settled once every other file has a name too.
/// </summary>
/// <param name="Date">When it was taken or last changed, for the file to carry once it's home.</param>
public sealed record ExportFile(string Key, string Folder, string Name, ExportZip Zip, ZipItem Item, DateTimeOffset? Date);

/// <summary>
/// An album, by the keys of the photos and videos in it. Photos arrive once each, in the folder for
/// the year they were taken, so an album becomes a page that shows its photos where they are rather
/// than a second copy of every one of them.
/// </summary>
public sealed record ExportAlbum(string Key, string Name, IReadOnlyList<string> Files);

/// <summary>What one kind of export makes of the zips added to its folder.</summary>
/// <param name="Details">What to say about each folder at the top, such as how many photos are in it.</param>
/// <param name="Unrecognised">Zips that aren't this kind of export, with why, by file name.</param>
public sealed record ExportReading(
    IReadOnlyList<ExportFile> Files,
    IReadOnlyDictionary<string, string> Details,
    IReadOnlyDictionary<string, string> Unrecognised,
    IReadOnlyList<ExportAlbum>? Albums = null);

/// <summary>How one service lays out the zips it exports, and so where everything in them belongs.</summary>
public interface IExportLayout
{
    /// <summary>What imports from it are recorded under.</summary>
    string ProviderId { get; }

    /// <summary>The folder in My files its zips are added to, and what's in them goes into.</summary>
    string FolderName { get; }

    /// <summary>
    /// Works out what the zips hold together. Files can refer to each other across zips — a photo
    /// in one, its details in another — so this always sees every zip at once.
    /// </summary>
    ExportReading Read(IReadOnlyList<ExportZip> zips, CancellationToken cancellationToken);
}

/// <summary>
/// An export downloaded from somewhere that can't be signed in to, as somewhere to bring things home
/// from: zips somebody has added to a folder of their own files, such as iCloud. What the zips hold
/// is read in place, and arrives in that same folder sorted the way its layout says — photos by
/// year, documents in the folders they had — with each file carrying the date it was taken or last
/// changed rather than the day it was exported. The zips themselves stay where they are until
/// somebody deletes them.
///
/// Like the notebooks Evernote's export files become, a file is known by a key that says which
/// file it is, and its name is only where it lands. A file that has arrived keeps the name it
/// arrived with, and one that hasn't never takes a name an arrived one has — so adding a part that
/// was missing, or a newer export of the same account, brings only what's new, under names of its
/// own, and nothing twice.
///
/// Every path this hands out is made of names it chose itself, and is only ever looked up among
/// them, never joined onto a folder on disk.
/// </summary>
public sealed class ExportSource(string libraryRoot, ImportLog log, IExportLayout layout) : IImportSource
{
    /// <summary>Files are known by a path that starts with this, which no folder's name can.</summary>
    private const string FileMark = "/:";

    /// <summary>A zip that isn't this kind of export, shown so it isn't silently left out.</summary>
    private const string OtherMark = "/?";

    /// <summary>Bytes, as disks count them, a name may have before " 2" and its ending.</summary>
    private const int LongestName = 180;

    /// <summary>The folder album pages go in, beside the photos.</summary>
    public const string AlbumsFolder = "Albums";

    public string ProviderId => layout.ProviderId;
    public string DestinationPrefix => layout.FolderName;
    public bool KeepsDates => true;

    private readonly ConcurrentDictionary<string, Lazy<Task<ExportZip>>> _zips = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private (string Key, Lazy<Task<ExportReading>> Reading)? _reading;
    private (ExportReading Reading, Tree Tree)? _named;

    /// <summary>
    /// A file with where it lands, relative to the export's folder: one of the export's own, or the
    /// page an album becomes.
    /// </summary>
    private sealed record Landed(string Key, string Folder, string Name, ExportFile? File, ExportAlbum? Album)
    {
        public string Path => Folder.Length == 0 ? Name : $"{Folder}/{Name}";

        /// <summary>An album's page, worked out once everything it shows has a name.</summary>
        public string? Page { get; init; }

        public long Size => File?.Item.Size ?? System.Text.Encoding.UTF8.GetByteCount(Page ?? "");
    }

    /// <summary>Everything named, by folder: what each folder holds, files and the folders under it.</summary>
    private sealed record Tree(
        IReadOnlyDictionary<string, Landed> Files,
        IReadOnlyDictionary<string, List<string>> Folders,
        IReadOnlyDictionary<string, List<Landed>> FilesIn);

    /// <summary>What the export holds, for somebody looking at it before adding it.</summary>
    public async Task<ExportReading> ReadAsync(CancellationToken cancellationToken)
    {
        var zips = new List<ExportZip>();
        foreach (var file in Zips())
            zips.Add(await ZipAsync(file, cancellationToken));

        var key = string.Join('\n', zips.Select(zip => $"{zip.File}\t{zip.Items.Count}\t{Revision(new FileInfo(zip.File))}"));
        Lazy<Task<ExportReading>> reading;
        lock (_lock)
        {
            if (_reading is not { } current || current.Key != key)
            {
                // Read once for everybody asking, and not abandoned because the first to ask went away.
                var snapshot = zips.ToArray();
                current = (key, new Lazy<Task<ExportReading>>(() => Task.Run(() => layout.Read(snapshot, CancellationToken.None))));
                _reading = current;
            }
            reading = current.Reading;
        }
        try
        {
            return await reading.Value.WaitAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_lock)
                if (_reading is { } failed && ReferenceEquals(failed.Reading, reading))
                    _reading = null;
            throw;
        }
    }

    public async Task<SourceEntry> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        var (reading, tree) = await NamedAsync(afresh: true, cancellationToken);
        if (path.StartsWith(OtherMark, StringComparison.Ordinal))
            return reading.Unrecognised.TryGetValue(path[OtherMark.Length..], out var why)
                ? Other(path[OtherMark.Length..], why)
                : throw Gone();
        if (path.StartsWith(FileMark, StringComparison.Ordinal))
            return tree.Files.TryGetValue(path[FileMark.Length..], out var file) ? Describe(file) : throw Gone();
        var folder = Folder(path);
        if (folder.Length == 0) return new SourceEntry("/", layout.FolderName, "/", "/", true, null, null, null);
        if (!tree.Folders.ContainsKey(folder) && !Said(reading, folder)) throw Gone();
        return Describe(reading, folder);
    }

    public async Task<IReadOnlyList<SourceEntry>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        if (path.StartsWith(FileMark, StringComparison.Ordinal)) throw Gone();
        // A zip that isn't this export holds nothing to bring.
        if (path.StartsWith(OtherMark, StringComparison.Ordinal)) return [];
        var folder = Folder(path);
        var (reading, tree) = await NamedAsync(afresh: folder.Length == 0, cancellationToken);
        if (folder.Length > 0 && !tree.Folders.ContainsKey(folder)) return Said(reading, folder) ? [] : throw Gone();

        var entries = new List<SourceEntry>();
        if (tree.Folders.TryGetValue(folder, out var folders))
            entries.AddRange(folders.Select(child => Describe(reading, child)));
        if (folder.Length == 0)
            // A folder with something to say and nothing in it, such as photos Apple left out, is
            // still shown, so what it says is seen.
            entries.AddRange(reading.Details.Keys.Where(said => Said(reading, said) && !tree.Folders.ContainsKey(said))
                .Select(said => Describe(reading, said)));
        if (tree.FilesIn.TryGetValue(folder, out var files))
            entries.AddRange(files.Select(Describe));
        entries.Sort((one, two) =>
            one.IsFolder != two.IsFolder ? (one.IsFolder ? -1 : 1) : StringComparer.OrdinalIgnoreCase.Compare(one.Name, two.Name));
        if (folder.Length == 0)
            // Zips that aren't this export are shown, with why, so a wrong one isn't silently ignored.
            entries.AddRange(reading.Unrecognised.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => Other(pair.Key, pair.Value)));
        return entries;
    }

    /// <summary>Whether a folder at the top has something said about it, whether or not anything is in it.</summary>
    private static bool Said(ExportReading reading, string folder) => !folder.Contains('/') && reading.Details.ContainsKey(folder);

    private static SourceEntry Other(string zip, string why) =>
        new(OtherMark + zip, zip, OtherMark + zip, "/" + zip, true, null, null, null, why);

    /// <summary>What a folder holds, worked out from what has already been read rather than by walking it.</summary>
    public async Task<FolderSize> SizeAsync(string path, CancellationToken cancellationToken)
    {
        var (_, tree) = await NamedAsync(afresh: false, cancellationToken);
        if (path.StartsWith(OtherMark, StringComparison.Ordinal)) return new FolderSize(0, 0);
        if (path.StartsWith(FileMark, StringComparison.Ordinal))
            return tree.Files.TryGetValue(path[FileMark.Length..], out var one) ? new FolderSize(one.Size, 1) : throw Gone();
        var folder = Folder(path);
        var prefix = folder.Length == 0 ? "" : folder + "/";
        long bytes = 0;
        var count = 0;
        foreach (var file in tree.Files.Values)
            if (prefix.Length == 0 || file.Folder == folder || file.Folder.StartsWith(prefix, StringComparison.Ordinal))
            {
                bytes += file.Size;
                count++;
            }
        return new FolderSize(bytes, count);
    }

    public async Task<Stream> OpenAsync(string path, CancellationToken cancellationToken)
    {
        if (!path.StartsWith(FileMark, StringComparison.Ordinal)) throw Gone();
        var (_, tree) = await NamedAsync(afresh: false, cancellationToken);
        if (!tree.Files.TryGetValue(path[FileMark.Length..], out var file)) throw Gone();
        if (file.File is { } exported) return ZipIndex.Open(exported.Zip.File, exported.Item);
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(file.Page ?? ""), writable: false);
    }

    /// <summary>
    /// Every file with the name it has, or will have. Worked out afresh whenever somebody starts from
    /// the top, which every import does, and otherwise kept: asking the log for every file would make
    /// a large export take as long as the square of its size.
    /// </summary>
    private async Task<(ExportReading Reading, Tree Tree)> NamedAsync(bool afresh, CancellationToken cancellationToken)
    {
        var reading = await ReadAsync(cancellationToken);
        lock (_lock)
            if (!afresh && _named is { } kept && ReferenceEquals(kept.Reading, reading))
                return kept;
        var tree = Name(reading, log.Arrived(libraryRoot, ProviderId), layout.FolderName, ShownBefore);
        lock (_lock) _named = (reading, tree);
        return (reading, tree);
    }

    /// <summary>
    /// Names every file. One that has arrived keeps the name it arrived with. The rest take the name
    /// they'd like, never one an arrived file has, and files that would share one are numbered, in
    /// the order of their keys so the same export always names the same way. Names are compared
    /// ignoring case, as a Mac's disk does.
    /// </summary>
    /// <param name="shownBefore">
    /// What an album's page that has already arrived shows, by where it landed. A page only ever
    /// gains photos: one added again with a part missing, or after its zips were deleted, still shows
    /// everything it did.
    /// </param>
    private static Tree Name(ExportReading reading, IReadOnlyDictionary<string, string> arrived, string prefix,
        Func<string, IReadOnlyList<(string Path, string Name)>> shownBefore)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (remote, local) in arrived)
        {
            if (!local.StartsWith(prefix + "/", StringComparison.Ordinal)) continue;
            var landed = local[(prefix.Length + 1)..];
            taken.Add(landed);
            if (remote.StartsWith(FileMark, StringComparison.Ordinal)) kept.TryAdd(remote[FileMark.Length..], landed);
        }

        var files = new Dictionary<string, Landed>(StringComparer.Ordinal);
        Landed Place(string key, string wantedFolder, string wantedName, ExportFile? file, ExportAlbum? album)
        {
            if (kept.TryGetValue(key, out var landed))
            {
                var split = landed.LastIndexOf('/');
                return new Landed(key, split < 0 ? "" : landed[..split], landed[(split + 1)..], file, album);
            }
            var folder = string.Join('/', wantedFolder.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => Clean(part, "Untitled folder")));
            var (stem, ending) = Split(Clean(wantedName, "Untitled"));
            var candidate = stem;
            for (var number = 2; !taken.Add(Join(folder, candidate + ending)); number++) candidate = $"{stem} {number}";
            return new Landed(key, folder, candidate + ending, file, album);
        }

        foreach (var file in reading.Files.OrderBy(file => file.Key, StringComparer.Ordinal))
            if (!files.ContainsKey(file.Key))
                files[file.Key] = Place(file.Key, file.Folder, file.Name, file, null);
        // Pages last: what they show has to have its name first.
        foreach (var album in (reading.Albums ?? []).OrderBy(album => album.Key, StringComparer.Ordinal))
        {
            if (files.ContainsKey(album.Key)) continue;
            var shown = album.Files.Select(key => files.GetValueOrDefault(key))
                .OfType<Landed>().Where(landed => landed.File is not null).DistinctBy(landed => landed.Key).ToArray();
            if (shown.Length == 0) continue;
            var page = Place(album.Key, AlbumsFolder, Clean(album.Name, "Album") + ".html", null, album);
            var before = kept.ContainsKey(album.Key) ? shownBefore(page.Path) : [];
            var showing = before.Concat(shown.Select(landed => (landed.Path, landed.Name)))
                .DistinctBy(photo => photo.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            files[album.Key] = page with { Page = AlbumPage.Render(album.Name, prefix, page.Folder, showing) };
        }

        var folders = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var filesIn = new Dictionary<string, List<Landed>>(StringComparer.Ordinal);
        foreach (var file in files.Values)
        {
            if (!filesIn.TryGetValue(file.Folder, out var here)) filesIn[file.Folder] = here = [];
            here.Add(file);
            for (var child = file.Folder; child.Length > 0;)
            {
                var split = child.LastIndexOf('/');
                var parent = split < 0 ? "" : child[..split];
                if (!folders.TryGetValue(parent, out var children)) folders[parent] = children = [];
                if (children.Contains(child)) break;
                children.Add(child);
                // The folder itself has to be known even when it holds no files of its own.
                folders.TryAdd(child, []);
                child = parent;
            }
        }
        return new Tree(files, folders, filesIn);
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    /// <summary>What the page that landed at a place in the export's folder shows, or nothing if it isn't there.</summary>
    private IReadOnlyList<(string Path, string Name)> ShownBefore(string landed)
    {
        try
        {
            var full = PathPolicy.Resolve(libraryRoot, $"{layout.FolderName}/{landed}");
            var file = new FileInfo(full);
            if (!file.Exists || file.Length > 16 << 20) return [];
            var folder = landed.LastIndexOf('/') is var split and >= 0 ? landed[..split] : "";
            return AlbumPage.Shown(File.ReadAllText(full), folder);
        }
        catch (Exception failure) when (failure is LibraryException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A name as a file can be called, or <paramref name="otherwise"/> when nothing of it is left.</summary>
    private static string Clean(string name, string otherwise) =>
        EvernoteSource.Clean(name, LongestName) is { Length: > 0 } cleaned ? cleaned : otherwise;

    /// <summary>A file's name as the part to number and its ending, which the number goes before.</summary>
    private static (string Stem, string Ending) Split(string name)
    {
        var dot = name.LastIndexOf('.');
        // No ending, a name that is all ending, or something after a dot too long to be one.
        if (dot <= 0 || name.Length - dot > 12) return (name, "");
        return (name[..dot], name[dot..]);
    }

    private static SourceEntry Describe(Landed file)
    {
        var path = FileMark + file.Key;
        if (file.File is not { } exported)
        {
            var page = System.Text.Encoding.UTF8.GetBytes(file.Page ?? "");
            // Made here from what the zips hold, so brought again when that changes: an album with
            // photos in a part added later shows them once that part is in.
            return new SourceEntry(path, file.Name, path, "/" + file.Path, false, page.Length,
                $"{page.Length}-{Crc32.Append(0, page).ToString("x8", CultureInfo.InvariantCulture)}", null,
                Refreshable: true);
        }
        var item = exported.Item;
        return new SourceEntry(path, file.Name, path, "/" + file.Path, false, item.Size,
            $"{item.Size}-{item.Crc.ToString("x8", CultureInfo.InvariantCulture)}", exported.Date ?? item.Modified);
    }

    private static SourceEntry Describe(ExportReading reading, string folder)
    {
        var path = "/" + folder;
        var name = folder[(folder.LastIndexOf('/') + 1)..];
        return new SourceEntry(path, name, path, path, true, null, null, null,
            reading.Details.TryGetValue(folder, out var detail) ? detail : null);
    }

    /// <summary>The zips in the export's folder. Links and hidden files are left out, as they are everywhere in Uncloud.</summary>
    private IReadOnlyList<FileInfo> Zips()
    {
        var folder = PathPolicy.Resolve(libraryRoot, layout.FolderName);
        if (!Directory.Exists(folder)) return [];
        return new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, ReturnSpecialDirectories = false })
            .Where(file => !file.Name.StartsWith('.')
                           && file.LinkTarget is null && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                           && file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Revision(FileInfo file) => $"{file.Length}-{file.LastWriteTimeUtc.Ticks}";

    /// <summary>One zip's table of contents, read once and kept until the zip changes.</summary>
    private async Task<ExportZip> ZipAsync(FileInfo file, CancellationToken cancellationToken)
    {
        var key = $"{file.FullName}\n{Revision(file)}";
        foreach (var stale in _zips.Keys)
            if (stale.StartsWith(file.FullName + "\n", StringComparison.Ordinal) && stale != key)
                _zips.TryRemove(stale, out _);
        var reading = _zips.GetOrAdd(key, _ => new Lazy<Task<ExportZip>>(() => Task.Run(() =>
        {
            try
            {
                var contents = ZipIndex.Read(file.FullName);
                return new ExportZip(file.FullName, file.Name, contents.Items, contents.Unreadable, null);
            }
            catch (InvalidDataException)
            {
                return new ExportZip(file.FullName, file.Name, [], 0,
                    "This isn’t a zip Uncloud can open. If it’s still downloading, wait for it to finish, then add it again.");
            }
        })));
        try
        {
            return await reading.Value.WaitAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Not kept: a zip that couldn't be read just now is tried again next time.
            _zips.TryRemove(key, out _);
            throw;
        }
    }

    private static string Folder(string path) => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));

    private LibraryException Gone() => new($"This is no longer in your {layout.FolderName} export.", "not_found");
}
