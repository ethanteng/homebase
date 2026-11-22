using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Homebase.Core.Providers;

/// <summary>
/// The zips Apple sends when somebody asks for a copy of their data at privacy.apple.com. Each kind
/// of data comes in parts, each part a whole zip with one folder at its top named after it, such as
/// "iCloud Photos Part 2 of 5" — in the person's own language, so what a part holds is told from
/// what's in it rather than what it's called. Photos arrive once each, in a folder for the year
/// they were taken, with each album a page that shows them; iCloud Drive keeps the folders it had;
/// anything else Apple sent keeps its own folder, under the name Apple gave it.
///
/// Apple says when each photo was taken in spreadsheets beside the photos ("Photo Details.csv"),
/// one row a photo by its name. Each part's spreadsheets are about that part's photos only, and the
/// same name can be a different photo in another part, so a row is only ever matched to a photo in
/// its own part. Which photos each album holds is in a spreadsheet for each album, by name alone and
/// often in a different part from the photos, which is why every part should be added together.
/// The spreadsheets are read for what they say and aren't brought home themselves. Photos in
/// Recently Deleted keep that folder, rather than turning up among the rest. The dates on the files
/// in Apple's zips are only when the copy was made, so a file nothing else gives a date for carries none.
/// </summary>
public sealed partial class AppleExport : IExportLayout
{
    public const string ProviderName = "icloud";
    public const string Folder = "iCloud";

    public string ProviderId => ProviderName;
    public string FolderName => Folder;

    /// <summary>Where iCloud Drive's files land, relative to the export's folder.</summary>
    public const string DriveFolder = "iCloud Drive";

    /// <summary>Where photos that were in Recently Deleted land, under the name iCloud gives it.</summary>
    public const string DeletedFolder = "Recently Deleted";

    /// <summary>Where the shared albums Apple sends as zips of their own land, as they came.</summary>
    public const string SharedFolder = "Shared Albums";

    private enum Kind { Photos, Drive, Other }

    /// <summary>One entry in one of Apple's zips: which part it's in, and where in that part.</summary>
    private sealed record Entry(ExportZip Zip, ZipItem Item, string Top, string Path);

    /// <summary>Whether a zip is one of Apple's, by what's at its top.</summary>
    public static bool LooksLikeApple(ExportZip zip) =>
        zip.Items.GroupBy(item => Top(item.Path)).Any(top => top.Key.Length > 0
            && (Named(top.Key, zip.FileName) || KindOf(top.Key, top.Select(item => Inside(item.Path))) != Kind.Other));

    private static string Top(string path) => path.IndexOf('/') is var split and > 0 ? path[..split] : "";
    private static string Inside(string path) => path[(path.IndexOf('/') + 1)..];

    /// <summary>
    /// Whether a folder at the top of a zip is named the way Apple names its parts: for iCloud, or
    /// after the zip it's in, which is how every one of Apple's parts is laid out.
    /// </summary>
    private static bool Named(string top, string zipName)
    {
        if (top.StartsWith("iCloud", StringComparison.OrdinalIgnoreCase)) return true;
        var stem = Regex.Replace(Path.GetFileNameWithoutExtension(zipName), @"\s*\(\d+\)$", "");
        return stem.Replace('_', ' ').Equals(top.Replace('_', ' '), StringComparison.OrdinalIgnoreCase)
               && ExportPhotos.Part(top) is not null;
    }

    /// <summary>What one of Apple's folders holds, from the files in it.</summary>
    private static Kind KindOf(string top, IEnumerable<string> inside)
    {
        foreach (var path in inside)
        {
            if (path.StartsWith("Photos/Photo Details", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("Albums/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("Memories/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(DeletedFolder + "/", StringComparison.OrdinalIgnoreCase))
                return Kind.Photos;
            if (path.Equals("Drive/Drive Details.csv", StringComparison.OrdinalIgnoreCase)) return Kind.Drive;
        }
        var name = ExportPhotos.Part(top)?.Name ?? top;
        return name.StartsWith("iCloud Photo", StringComparison.OrdinalIgnoreCase) ? Kind.Photos
            : name.StartsWith("iCloud Drive", StringComparison.OrdinalIgnoreCase) ? Kind.Drive
            : Kind.Other;
    }

    public ExportReading Read(IReadOnlyList<ExportZip> zips, CancellationToken cancellationToken)
    {
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        // Everything, by the name of what it is with the part taken off, such as "iCloud Photos".
        var categories = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        var parts = new Dictionary<string, (HashSet<int> Seen, int Of)>(StringComparer.OrdinalIgnoreCase);
        foreach (var zip in zips)
        {
            if (zip.Problem is { } problem)
            {
                notes[zip.FileName] = problem;
                continue;
            }
            if (!LooksLikeApple(zip))
            {
                notes[zip.FileName] = zip.Items.Any(item => item.Path.StartsWith("Takeout/", StringComparison.Ordinal))
                    ? "This is from Google. Add it under Google instead."
                    : "This isn’t a zip file from Apple.";
                continue;
            }
            foreach (var item in zip.Items)
            {
                var top = Top(item.Path);
                // Apple puts nothing at the very top of a part; anything there is passed over.
                if (top.Length == 0) continue;
                var part = ExportPhotos.Part(top);
                var name = part?.Name ?? top;
                if (!categories.TryGetValue(name, out var list)) categories[name] = list = [];
                list.Add(new Entry(zip, item, top, Inside(item.Path)));
                if (!parts.TryGetValue(name, out var counted)) parts[name] = counted = ([], part?.Parts ?? 1);
                counted.Seen.Add(part?.Part ?? 1);
            }
        }

        var files = new List<ExportFile>();
        var albums = new List<ExportAlbum>();
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        string? photosSaid = null;
        foreach (var (category, entries) in categories.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var missing = parts.TryGetValue(category, out var counted) ? ExportPhotos.Missing(counted.Seen, counted.Of) : null;
            switch (KindOf(category, entries.Select(entry => entry.Path)))
            {
                case Kind.Photos:
                {
                    var (photos, found, rows) = ReadPhotos(entries, cancellationToken);
                    files.AddRange(photos);
                    albums.AddRange(found);
                    var count = photos.Count(file => file.Key.StartsWith("photos/", StringComparison.Ordinal));
                    var deleted = photos.Count - count - photos.Count(file => file.Folder == SharedFolder);
                    if (deleted > 0) details[DeletedFolder] = ExportPhotos.Count(deleted);
                    photosSaid = count == 0 && rows > 0
                        // What Apple sends when Advanced Data Protection is on: the spreadsheets, and no photos.
                        ? "No photos in these zip files. Apple leaves them out if Advanced Data Protection is on."
                        : Join(ExportPhotos.Count(count), missing);
                    break;
                }
                case Kind.Drive:
                {
                    var drive = ReadDrive(entries);
                    files.AddRange(drive);
                    details[DriveFolder] = Join(ExportPhotos.Files(drive.Count), missing);
                    break;
                }
                default:
                {
                    foreach (var (zip, item, _, path) in entries)
                    {
                        var split = path.LastIndexOf('/');
                        files.Add(new ExportFile($"files/{category}/{path}", split < 0 ? category : $"{category}/{path[..split]}",
                            path[(split + 1)..], zip, item, null));
                    }
                    details[category] = Join(ExportPhotos.Files(entries.Count), missing);
                    break;
                }
            }
        }
        if (photosSaid is not null) details[ExportPhotos.Folder] = photosSaid;
        if (albums.Count > 0) details[ExportSource.AlbumsFolder] = albums.Count == 1 ? "1 album" : $"{albums.Count:N0} albums";
        return new ExportReading(files, details, notes, albums);
    }

    private static string Join(string said, string? missing) => missing is null ? said : $"{said} · {missing}";

    private static (IReadOnlyList<ExportFile> Files, IReadOnlyList<ExportAlbum> Albums, int Rows) ReadPhotos(
        List<Entry> entries, CancellationToken cancellationToken)
    {
        // When each photo was taken, by the part it's in, whether it was deleted, and its name.
        var taken = new Dictionary<(string Zip, string Top, bool Deleted, string Name), DateTimeOffset>();
        var rows = 0;
        // Each album's photos, by name, from its own spreadsheet.
        var albumNames = new List<(string Key, string Name, List<string> Photos)>();
        var media = new List<Entry>();
        var shared = new List<ExportFile>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.Path[(entry.Path.LastIndexOf('/') + 1)..];
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Shared albums come as zips inside the part. They arrive as they came, to be opened as they are.
                shared.Add(new ExportFile($"shared/{name}/{entry.Item.Size:x}-{entry.Item.Crc:x8}", SharedFolder, name,
                    entry.Zip, entry.Item, null));
                continue;
            }
            if (!name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                media.Add(entry);
                continue;
            }
            if (ZipIndex.ReadSmall(entry.Zip.File, entry.Item, 256 << 20) is not { } bytes) continue;
            var table = Csv(bytes);
            if (table.Count == 0) continue;
            var header = table[0].Select(cell => cell.Trim()).ToArray();
            var nameColumn = Column(header, "imgName");
            var dateColumn = Column(header, "originalCreationDate");
            if (nameColumn >= 0 && dateColumn >= 0)
            {
                var deletedColumn = Column(header, "deleted");
                foreach (var row in table.Skip(1))
                {
                    if (row.Length <= Math.Max(nameColumn, dateColumn)) continue;
                    rows++;
                    var deleted = deletedColumn >= 0 && row.Length > deletedColumn
                                  && row[deletedColumn].Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
                    if (Date(row[dateColumn]) is { } date)
                        taken.TryAdd((entry.Zip.File, entry.Top, deleted, row[nameColumn].Trim()), date);
                }
                continue;
            }
            // An album's spreadsheet is a column of the names of its photos, under whatever heading.
            if (entry.Path.StartsWith("Albums/", StringComparison.OrdinalIgnoreCase))
            {
                var title = Path.GetFileNameWithoutExtension(name);
                albumNames.Add(($"albums/{title}", title, table.Skip(1).Where(row => row.Length > 0)
                    .Select(row => row[0].Trim()).Where(cell => cell.Length > 0).ToList()));
            }
        }

        var copies = media.Select(entry =>
        {
            var name = entry.Path[(entry.Path.LastIndexOf('/') + 1)..];
            var deleted = entry.Path.StartsWith(DeletedFolder + "/", StringComparison.OrdinalIgnoreCase);
            var date = taken.TryGetValue((entry.Zip.File, entry.Top, deleted, name), out var known) ? known
                : taken.TryGetValue((entry.Zip.File, entry.Top, !deleted, name), out var other) ? other
                : (DateTimeOffset?)null;
            return new ExportPhotos.Copy(entry.Zip, entry.Item, name, date, Preferred: true, Deleted: deleted);
        }).ToArray();
        // A name in an album's spreadsheet stands for every photo of that name, since Apple's
        // spreadsheets say nothing else about which one.
        var byName = copies.Where(copy => !copy.Deleted).GroupBy(copy => copy.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(copy => ExportPhotos.Key(copy.Item)).Distinct().ToArray(),
                StringComparer.OrdinalIgnoreCase);
        // Spreadsheets for albums of the same name, in different parts, are put together: Apple's
        // spreadsheets say nothing that tells two albums of one name apart, and leaving one out
        // would leave its photos off the page.
        var albums = albumNames
            .GroupBy(album => album.Key, StringComparer.Ordinal)
            .Select(same => new ExportAlbum(same.Key, same.First().Name,
                same.SelectMany(album => album.Photos).SelectMany(photo => byName.GetValueOrDefault(photo) ?? [])
                    .Distinct().ToArray()))
            .Where(album => album.Files.Count > 0)
            .ToArray();
        return ([.. ExportPhotos.Once(copies, DeletedFolder), .. shared], albums, rows);
    }

    /// <summary>
    /// iCloud Drive's files, which Apple puts in a "Drive" folder inside the part with a spreadsheet
    /// of when each was last changed. That spreadsheet names files without saying where they are,
    /// so a row is only used where its name and size are one file's alone.
    /// </summary>
    private static IReadOnlyList<ExportFile> ReadDrive(List<Entry> entries)
    {
        var changed = new Dictionary<(string Name, long Size), DateTimeOffset?>();
        foreach (var entry in entries)
        {
            if (!entry.Path.Equals("Drive/Drive Details.csv", StringComparison.OrdinalIgnoreCase)) continue;
            if (ZipIndex.ReadSmall(entry.Zip.File, entry.Item, 256 << 20) is not { } bytes) continue;
            var table = Csv(bytes);
            if (table.Count == 0) continue;
            var header = table[0].Select(cell => cell.Trim()).ToArray();
            int title = Column(header, "Title"), size = Column(header, "Size"), modified = Column(header, "Modified On");
            if (title < 0 || size < 0 || modified < 0) continue;
            foreach (var row in table.Skip(1))
            {
                if (row.Length <= Math.Max(title, Math.Max(size, modified))
                    || !long.TryParse(row[size].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytesLong)) continue;
                var key = (row[title].Trim().ToLowerInvariant(), bytesLong);
                var when = DriveDate(row[modified]);
                // The same name and size twice with different dates says nothing about either.
                changed[key] = changed.TryGetValue(key, out var already) && already != when ? null : when;
            }
        }

        var files = new List<ExportFile>();
        foreach (var (zip, item, _, path) in entries)
        {
            if (path.Equals("Drive/Drive Details.csv", StringComparison.OrdinalIgnoreCase)) continue;
            var inside = path.StartsWith("Drive/", StringComparison.OrdinalIgnoreCase) ? path["Drive/".Length..] : path;
            var split = inside.LastIndexOf('/');
            var name = inside[(split + 1)..];
            var date = changed.TryGetValue((name.ToLowerInvariant(), item.Size), out var known) ? known : null;
            files.Add(new ExportFile($"files/{DriveFolder}/{inside}", split < 0 ? DriveFolder : $"{DriveFolder}/{inside[..split]}",
                name, zip, item, date));
        }
        return files;
    }

    private static int Column(string[] header, string name) =>
        Array.FindIndex(header, cell => cell.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A date from iCloud Drive's spreadsheet, such as "08-14-2026 09:41:07". It says no time zone,
    /// and the ones seen read as California time, where Apple is.
    /// </summary>
    internal static DateTimeOffset? DriveDate(string text)
    {
        if (!DateTime.TryParseExact(text.Trim(), ["MM-dd-yyyy HH:mm:ss", "MM-dd-yyyy H:mm:ss", "MM-dd-yyyy HH:mm"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        try
        {
            var pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
            return new DateTimeOffset(local, pacific.GetUtcOffset(local));
        }
        catch (Exception failure) when (failure is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return new DateTimeOffset(local, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// A date as Apple writes them in its spreadsheets, such as "Saturday June 1,2019 1:23 PM GMT",
    /// or in any of the plainer ways a date can be written.
    /// </summary>
    internal static DateTimeOffset? Date(string text)
    {
        var cleaned = Weekday().Replace(text.Trim(), "");
        cleaned = Comma().Replace(cleaned, ", ");
        cleaned = Zone().Replace(cleaned, " +00:00").Trim();
        string[] formats =
        [
            "MMMM d, yyyy h:mm tt zzz", "MMMM d, yyyy h:mm:ss tt zzz", "MMMM d, yyyy H:mm zzz", "MMMM d, yyyy H:mm:ss zzz",
            "MMM d, yyyy h:mm tt zzz", "MMM d, yyyy h:mm:ss tt zzz", "MMMM d, yyyy zzz",
        ];
        if (DateTimeOffset.TryParseExact(cleaned, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact))
            return exact;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var plain) ? plain : null;
    }

    /// <summary>A spreadsheet's rows, as the plain comma-separated text Apple writes, with quoted cells.</summary>
    internal static List<string[]> Csv(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else quoted = false;
                }
                else cell.Append(character);
                continue;
            }
            switch (character)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
                    row.Clear();
                    break;
                default:
                    cell.Append(character);
                    break;
            }
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
        }
        return rows;
    }

    [GeneratedRegex(@"^[A-Za-z]+day[,\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex Weekday();

    [GeneratedRegex(@"\s*,\s*")]
    private static partial Regex Comma();

    [GeneratedRegex(@"\s*\b(?:GMT|UTC|Z)\b\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Zone();
}
