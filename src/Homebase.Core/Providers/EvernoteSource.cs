using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Homebase.Core.Providers;

/// <summary>An attachment as it will be kept: its name in the note's folder, and which of the note's attachments it is.</summary>
public sealed record EvernoteFile(int Resource, string Name, string Mime, long Size);

/// <summary>
/// One note as it will be kept: a page, and — when it has attachments — a folder of them beside it.
/// Where in the export file it is, so it can be read again by itself.
/// </summary>
public sealed record EvernoteNote(
    EvernoteExport.Span Span,
    string Page,
    string? Folder,
    IReadOnlyList<EvernoteFile> Files,
    long PageBytes,
    DateTimeOffset? Modified,
    bool Problem);

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
/// Like every other place, it is only ever read. The paths it hands out are made up of names it
/// chose itself, and a path is only ever looked up among them, never joined onto a folder on disk.
/// </summary>
public sealed partial class EvernoteSource(string libraryRoot) : IImportSource
{
    public const string ProviderName = "evernote";

    /// <summary>Where the export files are kept, and where the notebooks made from them go.</summary>
    public const string FolderName = "Evernote";

    public string ProviderId => ProviderName;
    public string DestinationPrefix => FolderName;

    // What each export file holds, by its path, its size and when it last changed. Shared between
    // everybody looking, so a notebook being measured and listed at once is read once.
    private readonly ConcurrentDictionary<string, Lazy<Task<EvernoteNotebook>>> _read = new(StringComparer.Ordinal);

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
            foreach (var note in notebook.Notes)
            {
                var page = $"/{notebook.Name}/{note.Page}";
                entries.Add(new SourceEntry(page, note.Page, page, page, false, note.PageBytes, notebook.Rev, note.Modified));
                if (note.Folder is { } folder)
                {
                    var files = $"/{notebook.Name}/{folder}";
                    entries.Add(new SourceEntry(files, folder, files, files, true, null, null, note.Modified));
                }
            }
            return entries
                .OrderByDescending(entry => entry.IsFolder)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (parts.Length == 2)
        {
            var note = notebook.Notes.FirstOrDefault(candidate => candidate.Folder == parts[1]) ?? throw Gone();
            return note.Files.Select(file =>
            {
                var at = $"/{notebook.Name}/{note.Folder}/{file.Name}";
                return new SourceEntry(at, file.Name, at, at, false, file.Size, notebook.Rev, note.Modified);
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
            var note = notebook.Notes.FirstOrDefault(candidate => candidate.Page == parts[1]) ?? throw Gone();
            var html = await Task.Run(() => Render(notebook, note, cancellationToken), cancellationToken);
            return new MemoryStream(Encoding.UTF8.GetBytes(html), writable: false);
        }
        var owner = notebook.Notes.FirstOrDefault(candidate => candidate.Folder == parts[1]) ?? throw Gone();
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
            var name = Unique(Clean(Path.GetFileNameWithoutExtension(file.Name), 60) is { Length: > 0 } cleaned ? cleaned : "Notebook", taken, "");
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

    /// <summary>Goes through one export file for its notes and the names they'll have.</summary>
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

        var pages = PageNames(read);
        var notes = new List<EvernoteNote>(read.Count);
        for (var index = 0; index < read.Count; index++)
        {
            var note = read[index];
            var page = pages[index];
            var folder = note.Resources.Count > 0 ? page[..^".html".Length] + " files" : null;
            var files = AttachmentNames(note.Resources)
                .Select((file, resource) => new EvernoteFile(resource, file, note.Resources[resource].Mime, note.Resources[resource].Size))
                .ToArray();
            var html = EvernotePage.Render(note, name, fileName, Linked(folder, files));
            notes.Add(new EvernoteNote(spans[index], page, folder, files, Encoding.UTF8.GetByteCount(html),
                note.Updated ?? note.Created, note.Problem is not null));
        }
        return new EvernoteNotebook(name, path, fileName, true, notes, rev, modified);
    }

    private static string Render(EvernoteNotebook notebook, EvernoteNote note, CancellationToken cancellationToken)
    {
        using var window = new FileWindow(notebook.File, note.Span.Start, note.Span.Length);
        var read = EvernoteExport.ReadNote(window, note.Span.Unfinished, cancellationToken);
        return EvernotePage.Render(read, notebook.Name, notebook.FileName, Linked(note.Folder, note.Files));
    }

    private static IReadOnlyList<EvernotePage.Attachment> Linked(string? folder, IReadOnlyList<EvernoteFile> files) =>
        folder is null
            ? []
            : files.Select(file => new EvernotePage.Attachment(
                $"{Uri.EscapeDataString(folder)}/{Uri.EscapeDataString(file.Name)}", file.Name, file.Mime, file.Size)).ToArray();

    /// <summary>
    /// A page name for every note, from its title. Two notes can have the same title, so those that
    /// do are told apart by the day each was made, and failing that numbered in the order the export
    /// has them. Names are compared ignoring case, as a Mac's disk does.
    /// </summary>
    internal static IReadOnlyList<string> PageNames(IReadOnlyList<EnexNote> notes)
    {
        var titles = notes.Select(note => Clean(note.Title, 100) is { Length: > 0 } title ? title : "Untitled note").ToArray();
        var shared = titles.GroupBy(title => title, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return titles.Select((title, index) =>
        {
            var named = shared.Contains(title)
                ? $"{title} ({notes[index].Created?.UtcDateTime.ToString("yyyy-MM-dd") ?? "undated"})"
                : title;
            return Unique(named, taken, ".html") + ".html";
        }).ToArray();
    }

    /// <summary>A name for every attachment, from the one it had where possible, and none the same.</summary>
    internal static IReadOnlyList<string> AttachmentNames(IReadOnlyList<EnexResource> resources)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return resources.Select(resource =>
        {
            var extension = Extension(resource.Mime);
            var name = Clean(resource.FileName ?? "", 100);
            if (name.Length == 0)
                name = (resource.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "Picture"
                    : resource.Mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "Recording"
                    : "Attachment") + extension;
            else if (Path.GetExtension(name).Length == 0) name += extension;
            var ending = Path.GetExtension(name);
            return Unique(name[..^ending.Length], taken, ending) + ending;
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
    /// nothing a filesystem reads as a separator or refuses, no hidden-file dot at the start, and not
    /// so long that the whole path stops fitting.
    /// </summary>
    internal static string Clean(string name, int longest)
    {
        var replaced = new StringBuilder(name.Length);
        foreach (var character in name.Normalize(NormalizationForm.FormC))
            replaced.Append(character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(character)
                ? ' '
                : character);
        var cleaned = Spaces().Replace(replaced.ToString(), " ").Trim(' ', '.');
        if (cleaned.Length > longest)
        {
            var cut = char.IsHighSurrogate(cleaned[longest - 1]) ? longest - 1 : longest;
            cleaned = cleaned[..cut].TrimEnd(' ', '.');
        }
        return cleaned;
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

    private static LibraryException Gone() =>
        new("This is no longer in your Evernote files.", "not_found");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
