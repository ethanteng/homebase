using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Homebase.Core.Providers;

/// <summary>
/// The zips Google Takeout makes. Everything in them is under <c>Takeout/</c>, then a folder for
/// each Google product: Drive's files keep the folders they had, and Google Photos' photos arrive
/// once each, in a folder for the year they were taken, with each album a page that shows them.
///
/// Takeout keeps a photo's details — above all when it was taken — in a small file beside it,
/// whose name is the photo's own with more on the end (".json", or since late 2024
/// ".supplemental-metadata.json"), cut short when the whole would pass 51 characters, and with any
/// "(1)" Takeout added to tell two photos apart moved after the photo's ending. Those are matched
/// back to their photos here, and are what each photo's date comes from: the dates on the files in
/// the zips are only when the export was made. They aren't brought home themselves.
///
/// A photo in an album is usually in the export twice, once in its year's folder and once in the
/// album's, and arrives once; in some exports the album's folder has only the details files, and
/// its photos are found by what those say. An album is told from a year's folder by the small file
/// of its own that says what it is called, whatever that file is called in the person's language.
/// Photos that were in the bin keep to a Trash folder of their own.
/// </summary>
public sealed partial class GoogleTakeout : IExportLayout
{
    public const string ProviderName = "google";
    public const string Folder = "Google";

    public string ProviderId => ProviderName;
    public string FolderName => Folder;

    /// <summary>Google Photos' folder in a Takeout, in the languages it's been seen in.</summary>
    private static readonly HashSet<string> PhotosNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Google Photos", "Google Fotos", "Google Foto", "Google Photo", "Zdjęcia Google", "Google Фото",
        "Google Kuvat", "Google Fotók", "Fotky Google", "Google 相簿", "Google 相册", "Google フォト", "Google 포토",
    };

    /// <summary>What Google adds to a photo's name when it is an edited copy of another.</summary>
    private static readonly string[] Edited =
    [
        "-edited", "-effects", "-smile", "-mix", "-bearbeitet", "-modifié", "-editado", "-modificato", "-bewerkt",
        "-redigeret", "-redigert", "-redigerad", "-muokattu", "-edytowane", "-upraveno", "-編集済み", "-편집됨", "-已编辑",
    ];

    /// <summary>Where photos that were in Google Photos' bin land.</summary>
    public const string TrashFolder = "Trash";

    /// <summary>A photo's details, as Takeout keeps them beside it.</summary>
    private sealed record Details(string File, string? Title, DateTimeOffset? Taken, bool Trashed);

    public ExportReading Read(IReadOnlyList<ExportZip> zips, CancellationToken cancellationToken)
    {
        var unrecognised = new Dictionary<string, string>(StringComparer.Ordinal);
        // Takeout numbers its zips -001, -002 and so on, without saying how many there are, so a gap
        // is all that can be told missing.
        var numbered = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        // Everything in every zip, by the product it belongs to and where it is in that product.
        var products = new Dictionary<string, List<(ExportZip Zip, ZipItem Item, string Path)>>(StringComparer.Ordinal);
        foreach (var zip in zips)
        {
            if (zip.Problem is { } problem)
            {
                unrecognised[zip.FileName] = problem;
                continue;
            }
            var any = false;
            foreach (var item in zip.Items)
            {
                if (!item.Path.StartsWith("Takeout/", StringComparison.Ordinal)) continue;
                any = true;
                var rest = item.Path["Takeout/".Length..];
                var split = rest.IndexOf('/');
                // Takeout's own index of the archive, at the top, isn't anybody's file.
                if (split < 0) continue;
                var product = rest[..split];
                if (!products.TryGetValue(product, out var list)) products[product] = list = [];
                list.Add((zip, item, rest[(split + 1)..]));
            }
            if (!any)
                unrecognised[zip.FileName] = AppleExport.LooksLikeApple(zip)
                    ? "This is from iCloud, not Google. Add it under iCloud instead."
                    : "This isn’t a Google Takeout zip.";
            else if (Numbered().Match(zip.FileName) is { Success: true } number)
            {
                if (!numbered.TryGetValue(number.Groups["export"].Value, out var seen)) numbered[number.Groups["export"].Value] = seen = [];
                seen.Add(int.Parse(number.Groups["number"].Value, CultureInfo.InvariantCulture));
            }
        }
        var gaps = numbered.Values.Sum(seen => Enumerable.Range(1, seen.Max()).Count(part => !seen.Contains(part)));
        var missing = gaps == 0 ? null : gaps == 1 ? "a part is missing" : $"{gaps} parts are missing";

        var files = new List<ExportFile>();
        var albums = new List<ExportAlbum>();
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (product, items) in products.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPhotos(product, items.Select(entry => entry.Path)))
            {
                var (photos, found) = ReadPhotos(items, cancellationToken);
                files.AddRange(photos);
                albums.AddRange(found);
                continue;
            }
            // Anything else — Drive above all — keeps the folders it had, under the product's name.
            var name = Path.GetFileName(product);
            var count = 0;
            foreach (var (zip, item, path) in items)
            {
                var split = path.LastIndexOf('/');
                files.Add(new ExportFile($"files/{product}/{path}", split < 0 ? name : $"{name}/{path[..split]}",
                    path[(split + 1)..], zip, item, item.Modified));
                count++;
            }
            details[name] = ExportPhotos.Files(count);
        }
        var photoCount = files.Count(file => file.Key.StartsWith("photos/", StringComparison.Ordinal));
        if (photoCount > 0) details[ExportPhotos.Folder] = ExportPhotos.Count(photoCount);
        var trashed = files.Count(file => file.Key.StartsWith("deleted/", StringComparison.Ordinal));
        if (trashed > 0) details[TrashFolder] = ExportPhotos.Count(trashed);
        if (albums.Count > 0) details[ExportSource.AlbumsFolder] = albums.Count == 1 ? "1 album" : $"{albums.Count:N0} albums";
        if (missing is not null)
            foreach (var folder in details.Keys.ToArray())
                details[folder] = $"{details[folder]} · {missing}";
        return new ExportReading(files, details, unrecognised, albums);
    }

    /// <summary>
    /// Whether a product's folder is Google Photos: by its name in the languages it's been seen in,
    /// or, in one that hasn't, by being photos with Takeout's details files beside them.
    /// </summary>
    private static bool IsPhotos(string product, IEnumerable<string> paths)
    {
        if (PhotosNames.Contains(product)) return true;
        if (product.Equals("Drive", StringComparison.OrdinalIgnoreCase)) return false;
        int media = 0, sidecars = 0, total = 0;
        foreach (var path in paths)
        {
            total++;
            if (ExportPhotos.IsMedia(path)) media++;
            else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) sidecars++;
        }
        return total > 0 && media > 0 && sidecars > 0 && (media + sidecars) * 10 >= total * 9;
    }

    private static (IReadOnlyList<ExportFile> Files, IReadOnlyList<ExportAlbum> Albums) ReadPhotos(
        List<(ExportZip Zip, ZipItem Item, string Path)> items, CancellationToken cancellationToken)
    {
        // By folder, wherever its files are among the zips: a year's, an album's, or one of Google's
        // own such as Archive.
        var folders = items.GroupBy(entry => entry.Path.LastIndexOf('/') is var split and >= 0 ? entry.Path[..split] : "",
            StringComparer.Ordinal);
        var copies = new List<ExportPhotos.Copy>();
        // Each photo by what its details say, for albums whose folders have only details in them.
        var described = new Dictionary<(string Title, DateTimeOffset Taken), string>();
        var albums = new List<(string Key, string Name, List<string> Keys, List<Details> Unmatched)>();
        foreach (var folder in folders.OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = folder.Key[(folder.Key.LastIndexOf('/') + 1)..];
            var sidecars = new Dictionary<string, Details>(StringComparer.OrdinalIgnoreCase);
            string? albumTitle = null;
            var isAlbum = false;
            foreach (var (zip, item, path) in folder)
            {
                var file = path[(path.LastIndexOf('/') + 1)..];
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                if (ZipIndex.ReadSmall(zip.File, item, 1 << 20) is not { } json) continue;
                var details = Describe(file, json);
                if (details.Taken is null && !details.Trashed && details.Title is not null
                    && !ExportPhotos.IsMedia(file[..^".json".Length]) && !file.Contains(".supplemental", StringComparison.OrdinalIgnoreCase)
                    && !file.Contains(".suppl", StringComparison.OrdinalIgnoreCase))
                {
                    // An album's own file, which only says what it's called: "metadata.json" in
                    // English, and other names in other languages.
                    isAlbum = true;
                    albumTitle = details.Title;
                    continue;
                }
                sidecars[file] = details;
            }

            var keys = new List<string>();
            var used = new HashSet<Details>();
            var year = YearFolder().Match(name);
            var preferred = year.Success && !isAlbum;
            foreach (var (zip, item, path) in folder)
            {
                var file = path[(path.LastIndexOf('/') + 1)..];
                if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                var details = Match(file, sidecars);
                if (details is not null) used.Add(details);
                copies.Add(new ExportPhotos.Copy(zip, item, file, details?.Taken, preferred, details?.Trashed ?? false,
                    year.Success ? year.Groups["year"].Value : null));
                keys.Add(ExportPhotos.Key(item));
                if (details is { Title: { } title, Taken: { } taken })
                    described.TryAdd((title.ToLowerInvariant(), taken), ExportPhotos.Key(item));
            }
            if (isAlbum)
                albums.Add(($"albums/{folder.Key}", albumTitle is { Length: > 0 } called ? called : name, keys,
                    sidecars.Values.Where(details => !used.Contains(details)).ToList()));
        }

        var found = albums.Select(album =>
        {
            var keys = album.Keys.ToList();
            foreach (var details in album.Unmatched)
                if (details is { Title: { } title, Taken: { } taken } && described.TryGetValue((title.ToLowerInvariant(), taken), out var key))
                    keys.Add(key);
            return new ExportAlbum(album.Key, album.Name, keys.Distinct().ToArray());
        }).Where(album => album.Files.Count > 0).ToArray();
        return (ExportPhotos.Once(copies, TrashFolder), found);
    }

    /// <summary>
    /// The details file for a photo, among the ones in its folder: by name as Takeout makes them,
    /// cut short or not, with any "(1)" wherever Takeout put it; failing that, the only one whose
    /// title is the photo's name.
    /// </summary>
    private static Details? Match(string file, IReadOnlyDictionary<string, Details> sidecars)
    {
        if (sidecars.Count == 0) return null;
        var counter = Counter().Match(file);
        // "IMG_1(1).jpg" has its details in "IMG_1.jpg(1).json", or "IMG_1.jpg.supplemental-metadata(1).json".
        var (plain, number) = counter.Success
            ? (counter.Groups["stem"].Value + counter.Groups["ending"].Value, counter.Groups["number"].Value)
            : (file, "");
        var original = Unedited(plain);
        var stem = Path.GetFileNameWithoutExtension(original);
        var names = new List<string> { plain };
        if (original != plain) names.Add(original);
        // A live photo's video, or a motion photo's, shares the photo's details.
        if (Path.GetExtension(original).ToLowerInvariant() is ".mp" or ".mp4" or ".mov")
        {
            names.Add(original + ".jpg");
            names.AddRange(new[] { ".jpg", ".JPG", ".jpeg", ".heic", ".HEIC" }.Select(ending => stem + ending));
        }
        // Details named for a photo without its ending.
        names.Add(stem);

        var suffix = number.Length > 0 ? $"({number})" : "";
        foreach (var name in names)
            foreach (var candidate in new[]
                     {
                         $"{name}{suffix}.json", $"{name}.supplemental-metadata{suffix}.json",
                         $"{name}{suffix}.supplemental-metadata.json",
                     })
                if (sidecars.TryGetValue(candidate, out var exact)) return exact;

        // Cut short: a details file whose name, without its ending and counter, starts what the full
        // one would have been. The longest such is the one meant.
        Details? best = null;
        var bestLength = 0;
        foreach (var (name, details) in sidecars)
        {
            var bare = name[..^".json".Length];
            var itsCounter = TrailingCounter().Match(bare);
            if (itsCounter.Success)
            {
                if (itsCounter.Groups["number"].Value != number) continue;
                bare = bare[..itsCounter.Index];
            }
            else if (number.Length > 0) continue;
            if (bare.Length < 20 || bare.Length <= bestLength) continue;
            if (names.Any(wanted => $"{wanted}.supplemental-metadata".StartsWith(bare, StringComparison.OrdinalIgnoreCase)))
                (best, bestLength) = (details, bare.Length);
        }
        if (best is not null) return best;

        var titled = sidecars.Values.Where(details => details.Title is { } title
            && (title.Equals(original, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(title).Equals(stem, StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
        return number.Length == 0 && titled.Length == 1 ? titled[0] : null;
    }

    /// <summary>A photo's name without what Google adds to an edited copy, which shares the original's details.</summary>
    private static string Unedited(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        foreach (var edited in Edited)
            if (stem.EndsWith(edited, StringComparison.OrdinalIgnoreCase))
                return stem[..^edited.Length] + Path.GetExtension(name);
        return name;
    }

    /// <summary>
    /// What a details file says: the photo's name where it came from, when it was taken — or, not
    /// knowing that, when it was added — and whether it was in the bin.
    /// </summary>
    private static Details Describe(string file, byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new Details(file, null, null, false);
            var title = root.TryGetProperty("title", out var called) && called.ValueKind == JsonValueKind.String ? called.GetString() : null;
            var trashed = root.TryGetProperty("trashed", out var bin) && bin.ValueKind == JsonValueKind.True;
            DateTimeOffset? taken = null;
            foreach (var field in new[] { "photoTakenTime", "creationTime" })
            {
                if (!root.TryGetProperty(field, out var time) || time.ValueKind != JsonValueKind.Object
                    || !time.TryGetProperty("timestamp", out var stamp)) continue;
                var seconds = stamp.ValueKind switch
                {
                    JsonValueKind.String when long.TryParse(stamp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
                    JsonValueKind.Number when stamp.TryGetInt64(out var value) => value,
                    _ => (long?)null,
                };
                // Zero is what Google writes when it doesn't know. Before 1970 is below zero, and a real date.
                if (seconds is { } known and not 0 and >= -62135596800 and < 253402300800)
                {
                    taken = DateTimeOffset.FromUnixTimeSeconds(known);
                    break;
                }
            }
            return new Details(file, title, taken, trashed);
        }
        catch (JsonException)
        {
            return new Details(file, null, null, false);
        }
    }

    /// <summary>A folder Google made for a year's photos, such as "Photos from 2019" in whichever language.</summary>
    [GeneratedRegex(@"(?:^|\D)(?<year>(?:18|19|20|21)\d{2})$")]
    private static partial Regex YearFolder();

    /// <summary>A Takeout zip's name, such as "takeout-20260406T202814Z-001.zip": which export, and which part of it.</summary>
    [GeneratedRegex(@"^(?<export>takeout-\d{8}T\d{6}Z(?:-\d+)?)-(?<number>\d{3})(?:\s*\(\d+\))?\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex Numbered();

    /// <summary>A "(1)" Takeout added before a name's ending to tell two photos apart.</summary>
    [GeneratedRegex(@"^(?<stem>.+)\((?<number>\d+)\)(?<ending>\.[^.]+)?$")]
    private static partial Regex Counter();

    /// <summary>The same "(1)", at the end of a details file's name, where Takeout moves it.</summary>
    [GeneratedRegex(@"\((?<number>\d+)\)$")]
    private static partial Regex TrailingCounter();
}
