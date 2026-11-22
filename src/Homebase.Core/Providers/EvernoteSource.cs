using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Homebase.Core.Providers;

/// <summary>An attachment as it will be kept: its name in the note's folder, and which of the note's attachments it is.</summary>
public sealed record EvernoteFile(int Resource, string Name, string Mime, long Size);

/// <summary>
/// One note, as read from its export file and before it has a name in My files.
/// <paramref name="Key"/> says which note it is from one export of the notebook to the next: Evernote's
/// exports carry no id, so it is when the note was made and what it is called. <paramref name="Title"/>
/// is the title made safe to be a file's name, which may still be shared with another note.
/// </summary>
public sealed record EvernoteNote(
    EvernoteExport.Span Span,
    string Key,
    string Title,
    DateTimeOffset? Created,
    IReadOnlyList<EvernoteFile> Files,
    long PageBase,
    long PagePerCharacter,
    DateTimeOffset? Modified,
    bool Problem)
{
    /// <summary>
    /// How big the note's page is. The page links to its attachments by their folder's name, which
    /// isn't settled until the note is named, and that is the only part of it that depends on it.
    /// </summary>
    public long PageBytes(string? folder) =>
        folder is null ? PageBase : PageBase + PagePerCharacter * Uri.EscapeDataString(folder).Length;
}

/// <summary>A note with the name it has, or will have, in My files: a page, and a folder of attachments when it has any.</summary>
public sealed record NamedNote(EvernoteNote Note, string Page, string? Folder);

/// <summary>What one export file holds, worked out once and kept until the file changes.</summary>
/// <param name="Name">The folder its notes go into, named after the file.</param>
/// <param name="IsExport">Whether it is an Evernote export at all, rather than some other file with the same ending.</param>
public sealed record EvernoteNotebook(
    string Name,
    string File,
    string FileName,
    bool IsExport,
    IReadOnlyList<EvernoteNote> Notes,
    string Rev,
    DateTimeOffset Modified);

/// <summary>
/// Evernote, as somewhere to bring things home from. Evernote no longer lets new apps sign in to
/// it, but its own app saves any notebook as an export file (.enex), and that is all this needs:
/// export files somebody has put in the Evernote folder of their own files.
///
/// Each export file is a notebook, and each note in it is a web page, with its attachments in a
/// folder beside it — so a notebook brought home is a folder anybody can open, search and keep with
/// nothing from Evernote. The export files themselves stay where they are, beside the notebooks
/// made from them: they are the complete copy, and the thing another notes app can bring back in.
///
/// A note is known by its key, which is the path the import log records it under; its name is only
/// where it lands. A note that has arrived keeps the name it arrived with, and a note that hasn't is
/// never given a name one that has arrived already has — so a newer export of the same notebook,
/// with notes added or in another order, brings each new note under a name of its own and nothing
/// twice. A note's name never depends on whether another note has arrived yet, so names don't
/// change while a notebook is coming in.
///
/// Like every other place, it is only ever read. The paths it hands out are made up of names it
/// chose itself, and a path is only ever looked up among them, never joined onto a folder on disk.
/// </summary>
public sealed partial class EvernoteSource(string libraryRoot, ImportLog log) : IImportSource
{
    public const string ProviderName = "evernote";

    /// <summary>Where the export files are kept, and where the notebooks made from them go.</summary>
    public const string FolderName = "Evernote";

    /// <summary>
    /// The most a name is allowed, in bytes, before the date, number and ending it may still get.
    /// Disks count a name's length in bytes, 255 of them, and a character can take four.
    /// </summary>
    private const int LongestName = 180;

    public string ProviderId => ProviderName;
    public string DestinationPrefix => FolderName;

    // What each export file holds, by its path, its size and when it last changed. Shared between
    // everybody looking, so a notebook being measured and listed at once is read once.
    private readonly ConcurrentDictionary<string, Lazy<Task<EvernoteNotebook>>> _read = new(StringComparer.Ordinal);

    // The names each notebook's notes were last given. Worked out afresh whenever the notebook is
    // listed, which every import does before it opens anything, and kept for the opening: asking the
    // log once per note would make a large notebook take as long as the square of its size.
    private readonly ConcurrentDictionary<string, IReadOnlyList<NamedNote>> _named = new(StringComparer.Ordinal);

    /// <summary>The notebooks in the Evernote folder: one for each export file there.</summary>
    public async Task<IReadOnlyList<EvernoteNotebook>> NotebooksAsync(CancellationToken cancellationToken)
    {
        var notebooks = new List<EvernoteNotebook>();
        foreach (var (name, file) in Exports())
            notebooks.Add(await ReadAsync(name, file, cancellationToken));
        return notebooks;
    }

    public async Task<SourceEntry> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        var parts = Parts(path);
        if (parts.Length == 0)
            return new SourceEntry("/", FolderName, "/", "/", true, null, null, null);
        var parent = "/" + string.Join('/', parts[..^1]);
        var normalized = "/" + string.Join('/', parts);
        return (await ListFolderAsync(parent, cancellationToken)).FirstOrDefault(entry => entry.Path == normalized)
               ?? throw Gone();
    }

    public async Task<IReadOnlyList<SourceEntry>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        var parts = Parts(path);
        var notebooks = await NotebooksAsync(cancellationToken);
        if (parts.Length == 0) return notebooks.Select(Describe).ToArray();

        var notebook = notebooks.FirstOrDefault(candidate => candidate.Name == parts[0]) ?? throw Gone();
        if (parts.Length == 1)
        {
            var entries = new List<SourceEntry>();
            foreach (var named in Named(notebook, afresh: true))
            {
                var note = named.Note;
                var page = $"/{notebook.Name}/{note.Key}.html";
                entries.Add(new SourceEntry(page, named.Page, page, $"/{notebook.Name}/{named.Page}", false,
                    note.PageBytes(named.Folder), notebook.Rev, note.Modified));
                if (named.Folder is { } folder)
                {
                    var files = $"/{notebook.Name}/{note.Key} files";
                    entries.Add(new SourceEntry(files, folder, files, $"/{notebook.Name}/{folder}", true, null, null, note.Modified));
                }
            }
            return entries
                .OrderByDescending(entry => entry.IsFolder)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (parts.Length == 2)
        {
            var named = Named(notebook, afresh: false).FirstOrDefault(candidate =>
                candidate.Folder is not null && $"{candidate.Note.Key} files" == parts[1]) ?? throw Gone();
            return named.Note.Files.Select(file =>
            {
                var at = $"/{notebook.Name}/{named.Note.Key} files/{file.Name}";
                return new SourceEntry(at, file.Name, at, $"/{notebook.Name}/{named.Folder}/{file.Name}", false,
                    file.Size, notebook.Rev, named.Note.Modified);
            }).ToArray();
        }
        throw Gone();
    }

    public async Task<Stream> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var parts = Parts(path);
        if (parts.Length is < 2 or > 3) throw Gone();
        var notebook = (await NotebooksAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Name == parts[0])
                       ?? throw Gone();
        if (parts.Length == 2)
        {
            var named = Named(notebook, afresh: false).FirstOrDefault(candidate => $"{candidate.Note.Key}.html" == parts[1])
                        ?? throw Gone();
            var html = await Task.Run(() => Render(notebook, named, cancellationToken), cancellationToken);
            return new MemoryStream(Encoding.UTF8.GetBytes(html), writable: false);
        }
        var owner = notebook.Notes.FirstOrDefault(candidate => $"{candidate.Key} files" == parts[1]) ?? throw Gone();
        var attachment = owner.Files.FirstOrDefault(file => file.Name == parts[2]) ?? throw Gone();
        var window = new FileWindow(notebook.File, owner.Span.Start, owner.Span.Length);
        try
        {
            return EvernoteExport.OpenResource(window, attachment.Resource);
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The export files in the Evernote folder, each with the name of the notebook it becomes. Links
    /// and hidden files are left out, as they are everywhere in Uncloud.
    /// </summary>
    private IReadOnlyList<(string Name, FileInfo File)> Exports()
    {
        var folder = PathPolicy.Resolve(libraryRoot, FolderName);
        if (!Directory.Exists(folder)) return [];
        var files = new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, ReturnSpecialDirectories = false })
            .Where(file => !file.Name.StartsWith('.')
                           && file.LinkTarget is null && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                           && file.Extension.Equals(".enex", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return files.Select(file =>
        {
            var name = Unique(Clean(Path.GetFileNameWithoutExtension(file.Name), 120) is { Length: > 0 } cleaned ? cleaned : "Notebook", taken, "");
            return (name, file);
        }).ToArray();
    }

    private async Task<EvernoteNotebook> ReadAsync(string name, FileInfo file, CancellationToken cancellationToken)
    {
        var rev = $"{file.Length}-{file.LastWriteTimeUtc.Ticks}";
        var key = $"{file.FullName}\n{rev}\n{name}";
        // A file replaced by a newer export is a different notebook; what was worked out for the old
        // one is no use to anybody now.
        foreach (var stale in _read.Keys)
            if (stale.StartsWith(file.FullName + "\n", StringComparison.Ordinal) && stale != key)
                _read.TryRemove(stale, out _);
        // Read once for everybody asking, and not abandoned because the first to ask went away.
        var reading = _read.GetOrAdd(key, _ => new Lazy<Task<EvernoteNotebook>>(
            () => Task.Run(() => Read(name, file.FullName, file.Name, rev, file.LastWriteTimeUtc))));
        try
        {
            return await reading.Value.WaitAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Not kept: a file that couldn't be read just now — still being copied in, say — is tried
            // again next time rather than remembered as broken.
            _read.TryRemove(key, out _);
            throw;
        }
    }

    /// <summary>Goes through one export file for its notes.</summary>
    internal static EvernoteNotebook Read(string name, string path, string fileName, string rev, DateTime modified)
    {
        // Listed only when it wasn't a link; looked at again in case it has become one since.
        PathPolicy.RejectLink(path);
        using (var head = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (!EvernoteExport.LooksLikeExport(head))
                return new EvernoteNotebook(name, path, fileName, false, [], rev, modified);

        IReadOnlyList<EvernoteExport.Span> spans;
        using (var whole = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                   1 << 16, FileOptions.SequentialScan))
            spans = EvernoteExport.FindNotes(whole, CancellationToken.None);

        var read = new List<EnexNote>(spans.Count);
        foreach (var span in spans)
        {
            using var window = new FileWindow(path, span.Start, span.Length);
            read.Add(EvernoteExport.ReadNote(window, span.Unfinished, CancellationToken.None));
        }

        var keys = Keys(read);
        var notes = new List<EvernoteNote>(read.Count);
        for (var index = 0; index < read.Count; index++)
        {
            var note = read[index];
            var files = AttachmentNames(note.Resources)
                .Select((file, resource) => new EvernoteFile(resource, file, note.Resources[resource].Mime, note.Resources[resource].Size))
                .ToArray();
            long Size(string? folder) => Encoding.UTF8.GetByteCount(EvernotePage.Render(note, name, fileName, Linked(folder, files)));
            // Rendered with folder names one and two characters long, which is enough to know the
            // page's size whatever its folder ends up called.
            var (pageBase, perCharacter) = (Size(null), 0L);
            if (files.Length > 0)
            {
                var (one, two) = (Size("a"), Size("aa"));
                (pageBase, perCharacter) = (2 * one - two, two - one);
            }
            notes.Add(new EvernoteNote(spans[index], keys[index],
                Clean(note.Title, LongestName) is { Length: > 0 } title ? title : "Untitled note",
                note.Created, files, pageBase, perCharacter, note.Updated ?? note.Created, note.Problem is not null));
        }
        return new EvernoteNotebook(name, path, fileName, true, notes, rev, modified);
    }

    /// <summary>The names the notebook's notes have, or will have: from the log, afresh, or as last worked out.</summary>
    private IReadOnlyList<NamedNote> Named(EvernoteNotebook notebook, bool afresh)
    {
        var key = $"{notebook.File}\n{notebook.Rev}\n{notebook.Name}";
        if (!afresh && _named.TryGetValue(key, out var kept)) return kept;
        foreach (var stale in _named.Keys)
            if (stale.StartsWith(notebook.File + "\n", StringComparison.Ordinal) && stale != key)
                _named.TryRemove(stale, out _);
        var named = Name(notebook, log.Arrived(libraryRoot, ProviderName));
        _named[key] = named;
        return named;
    }

    /// <summary>
    /// Names every note in a notebook. One that has arrived — its page or any of its attachments —
    /// keeps the name it arrived with. The rest are named for their title, never taking a name
    /// something that arrived has, and those sharing a title with another note in the export are told
    /// apart by the day each was made, then numbered. Names are compared ignoring case, as a Mac's
    /// disk does.
    /// </summary>
    /// <param name="arrived">What has arrived from Evernote, by its path here, with where it landed.</param>
    internal static IReadOnlyList<NamedNote> Name(EvernoteNotebook notebook, IReadOnlyDictionary<string, string> arrived)
    {
        var prefix = $"/{notebook.Name}/";
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (remote, local) in arrived)
        {
            if (!remote.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var parts = remote[prefix.Length..].Split('/');
            var landed = local.Split('/');
            string? key = null, name = null;
            if (parts.Length == 1 && parts[0].EndsWith(".html", StringComparison.Ordinal) && landed.Length >= 1
                && landed[^1].EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                (key, name) = (parts[0][..^".html".Length], landed[^1][..^".html".Length]);
            else if (parts.Length == 2 && parts[0].EndsWith(" files", StringComparison.Ordinal) && landed.Length >= 2
                     && landed[^2].EndsWith(" files", StringComparison.Ordinal))
                (key, name) = (parts[0][..^" files".Length], landed[^2][..^" files".Length]);
            if (key is null || name is null) continue;
            kept.TryAdd(key, name);
            taken.Add(name);
        }

        // Counted across the whole export, whether arrived or not, so a note's name doesn't change
        // when another with the same title arrives before it.
        var shared = notebook.Notes.GroupBy(note => note.Title, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return notebook.Notes.Select(note =>
        {
            if (!kept.TryGetValue(note.Key, out var name))
            {
                var wanted = shared.Contains(note.Title) || taken.Contains(note.Title)
                    ? $"{note.Title} ({note.Created?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "undated"})"
                    : note.Title;
                name = Unique(wanted, taken, "");
            }
            return new NamedNote(note, name + ".html", note.Files.Count > 0 ? name + " files" : null);
        }).ToArray();
    }

    private static string Render(EvernoteNotebook notebook, NamedNote named, CancellationToken cancellationToken)
    {
        var note = named.Note;
        using var window = new FileWindow(notebook.File, note.Span.Start, note.Span.Length);
        var read = EvernoteExport.ReadNote(window, note.Span.Unfinished, cancellationToken);
        return EvernotePage.Render(read, notebook.Name, notebook.FileName, Linked(named.Folder, note.Files));
    }

    private static IReadOnlyList<EvernotePage.Attachment> Linked(string? folder, IReadOnlyList<EvernoteFile> files) =>
        folder is null
            ? []
            : files.Select(file => new EvernotePage.Attachment(
                $"{Uri.EscapeDataString(folder)}/{Uri.EscapeDataString(file.Name)}", file.Name, file.Mime, file.Size)).ToArray();

    /// <summary>
    /// A key for every note: when it was made and a hash of its title, the same in every export of
    /// the notebook. Two notes made the same second with the same title are numbered in file order.
    /// </summary>
    internal static IReadOnlyList<string> Keys(IReadOnlyList<EnexNote> notes)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        return notes.Select(note =>
        {
            var made = note.Created?.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) ?? "undated";
            var title = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(note.Title.Normalize(NormalizationForm.FormC))))[..12]
                .ToLowerInvariant();
            var key = $"{made}-{title}";
            var candidate = key;
            for (var number = 2; !taken.Add(candidate); number++) candidate = $"{key}-{number}";
            return candidate;
        }).ToArray();
    }

    /// <summary>A name for every attachment, from the one it had where possible, and none the same.</summary>
    internal static IReadOnlyList<string> AttachmentNames(IReadOnlyList<EnexResource> resources)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return resources.Select(resource =>
        {
            var given = Clean(resource.FileName ?? "", int.MaxValue);
            var ending = Path.GetExtension(given);
            // Anything longer is a name with a dot in it, not an ending.
            if (ending.Length < 2 || Encoding.UTF8.GetByteCount(ending) > 16) ending = "";
            var stem = given[..^ending.Length];
            if (ending.Length == 0) ending = Extension(resource.Mime);
            stem = Clean(stem, LongestName - Encoding.UTF8.GetByteCount(ending));
            if (stem.Length == 0)
                stem = resource.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "Picture"
                    : resource.Mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "Recording"
                    : "Attachment";
            return Unique(stem, taken, ending) + ending;
        }).ToArray();
    }

    /// <summary>The first of <paramref name="stem"/>, "stem 2", "stem 3"… that isn't taken, which it then takes.</summary>
    private static string Unique(string stem, ISet<string> taken, string ending)
    {
        var candidate = stem;
        for (var number = 2; !taken.Add(candidate + ending); number++) candidate = $"{stem} {number}";
        return candidate;
    }

    /// <summary>
    /// A title or file name as a file can be called, on a Mac and on any computer the folder syncs to:
    /// nothing a filesystem reads as a separator or refuses, no hidden-file dot at the start, and no
    /// more than <paramref name="longest"/> bytes, which is how disks measure a name.
    /// </summary>
    internal static string Clean(string name, int longest)
    {
        var replaced = new StringBuilder(name.Length);
        foreach (var character in name.Normalize(NormalizationForm.FormC))
            replaced.Append(character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(character)
                ? ' '
                : character);
        var cleaned = Spaces().Replace(replaced.ToString(), " ").Trim(' ', '.');
        if (Encoding.UTF8.GetByteCount(cleaned) <= longest) return cleaned;
        var kept = new StringBuilder();
        var bytes = 0;
        foreach (var rune in cleaned.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > longest) break;
            kept.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return kept.ToString().TrimEnd(' ', '.');
    }

    private static string Extension(string mime) => mime.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/heic" => ".heic",
        "image/svg+xml" => ".svg",
        "image/tiff" => ".tif",
        "image/bmp" => ".bmp",
        "application/pdf" => ".pdf",
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/mp4" or "audio/x-m4a" or "audio/m4a" => ".m4a",
        "audio/amr" => ".amr",
        "audio/aac" => ".aac",
        "audio/ogg" => ".ogg",
        "video/mp4" => ".mp4",
        "video/quicktime" => ".mov",
        "text/plain" => ".txt",
        "text/html" => ".html",
        "text/csv" => ".csv",
        "application/msword" => ".doc",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.ms-excel" => ".xls",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        "application/vnd.ms-powerpoint" => ".ppt",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
        "application/zip" => ".zip",
        _ => "",
    };

    private static SourceEntry Describe(EvernoteNotebook notebook)
    {
        var path = $"/{notebook.Name}";
        var count = notebook.Notes.Count;
        var partly = notebook.Notes.Count(note => note.Problem);
        var detail = !notebook.IsExport ? "Not an Evernote export"
            : count == 0 ? "No notes"
            : $"{count:N0} {(count == 1 ? "note" : "notes")}"
              + (partly > 0 ? $" · {partly:N0} couldn’t be fully read" : "");
        return new SourceEntry(path, notebook.Name, path, path, true, null, null, notebook.Modified, detail);
    }

    private static string[] Parts(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static LibraryException Gone() => new("This is no longer in your Evernote files.", "not_found");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
