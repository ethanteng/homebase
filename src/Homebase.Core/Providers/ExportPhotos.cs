using System.Globalization;
using System.Text.RegularExpressions;

namespace Homebase.Core.Providers;

/// <summary>
/// What iCloud's and Google's exports have in common once their photos are found: each photo or
/// video arrives once, in a folder for the year it was taken, however many albums and folders it
/// was in and however many times the export repeats it.
/// </summary>
public static partial class ExportPhotos
{
    /// <summary>Where photos land, relative to the export's folder.</summary>
    public const string Folder = "Photos";

    /// <summary>The year folder for a photo nobody can say the date of.</summary>
    public const string Undated = "Undated";

    private static readonly HashSet<string> Media = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".bmp", ".tif", ".tiff",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".raf", ".rw2", ".srw", ".pef",
        ".mp4", ".m4v", ".mov", ".qt", ".3gp", ".3g2", ".avi", ".mkv", ".webm", ".mts", ".m2ts", ".mpg", ".mpeg", ".mp", ".wmv",
    };

    /// <summary>Whether a file is a photo or video by its ending.</summary>
    public static bool IsMedia(string name) => Media.Contains(Path.GetExtension(name));

    /// <summary>One photo or video as it appears in the export, perhaps one of several copies of it.</summary>
    /// <param name="Date">When it was taken, if the export says.</param>
    /// <param name="Preferred">Whether this copy is the one to name the photo after, such as the one in a year folder rather than an album.</param>
    /// <param name="Deleted">Whether it was in the service's bin when the export was made.</param>
    /// <param name="Year">The year the export filed it under, for when nothing says when it was taken.</param>
    public sealed record Copy(
        ExportZip Zip, ZipItem Item, string Name, DateTimeOffset? Date, bool Preferred, bool Deleted = false, string? Year = null);

    /// <summary>
    /// The key a photo is known by: what's in it, which is the same in every copy of it and every
    /// export it's in, whatever it's called.
    /// </summary>
    public static string Key(ZipItem item) => "photos/" + Content(item);

    private static string Content(ZipItem item) =>
        $"{item.Size.ToString("x", CultureInfo.InvariantCulture)}-{item.Crc.ToString("x8", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Each photo once, from all its copies: named after a preferred copy, dated from whichever copy
    /// knows when it was taken, and filed by that year. One that was only ever in the bin goes in
    /// <paramref name="deletedFolder"/> instead, so nothing somebody deleted turns up among their
    /// photos, and nothing is lost either.
    /// </summary>
    public static IReadOnlyList<ExportFile> Once(IEnumerable<Copy> copies, string deletedFolder)
    {
        var files = new List<ExportFile>();
        foreach (var same in copies.GroupBy(copy => Content(copy.Item), StringComparer.Ordinal))
        {
            var kept = same.Where(copy => !copy.Deleted).ToArray();
            var deleted = kept.Length == 0;
            var ordered = (deleted ? same.ToArray() : kept).OrderByDescending(copy => copy.Preferred)
                .ThenBy(copy => copy.Item.Path, StringComparer.Ordinal).ToArray();
            var named = ordered[0];
            var date = ordered.Select(copy => copy.Date).FirstOrDefault(date => date is not null);
            var year = date is null ? ordered.Select(copy => copy.Year).FirstOrDefault(year => year is not null) ?? Undated : Year(date);
            files.Add(deleted
                ? new ExportFile("deleted/" + same.Key, deletedFolder, named.Name, named.Zip, named.Item, date)
                : new ExportFile("photos/" + same.Key, $"{Folder}/{year}", named.Name, named.Zip, named.Item, date));
        }
        return files;
    }

    /// <summary>The year folder for a date, in UTC, which is all an export says.</summary>
    public static string Year(DateTimeOffset? date) =>
        date is { } known && known.Year is >= 1826 and <= 9999
            ? known.UtcDateTime.Year.ToString(CultureInfo.InvariantCulture)
            : Undated;

    /// <summary>How many photos and videos, said as a person would.</summary>
    public static string Count(int count) => count == 1 ? "1 photo or video" : $"{count:N0} photos and videos";

    /// <summary>How many files, said as a person would.</summary>
    public static string Files(int count) => count == 1 ? "1 file" : $"{count:N0} files";

    /// <summary>
    /// A part number and total from a name like "iCloud Photos Part 2 of 5", with what comes before
    /// it, or null when the name doesn't say.
    /// </summary>
    public static (string Name, int Part, int Parts)? Part(string name)
    {
        var match = PartPattern().Match(name);
        if (!match.Success) return null;
        return int.TryParse(match.Groups["part"].Value, CultureInfo.InvariantCulture, out var part)
               && int.TryParse(match.Groups["parts"].Value, CultureInfo.InvariantCulture, out var parts)
               && part >= 1 && part <= parts && parts <= 10_000
            ? (match.Groups["name"].Value.Replace('_', ' ').Trim(), part, parts)
            : null;
    }

    /// <summary>Which parts are missing, said as a person would, or null when none are.</summary>
    public static string? Missing(IReadOnlySet<int> seen, int parts)
    {
        var missing = Enumerable.Range(1, parts).Where(part => !seen.Contains(part)).ToArray();
        if (missing.Length == 0) return null;
        return missing.Length == 1
            ? $"part {missing[0]} of {parts} not added yet"
            : $"{missing.Length} of {parts} parts not added yet";
    }

    // "iCloud Photos Part 2 of 5", and the same in the other languages Apple writes it in, such as
    // "iCloud-bilder Del 2 av 5"; a browser's " (1)" for a second download of the same part is ignored.
    [GeneratedRegex(@"^(?<name>.+?)[\s_-]+\p{L}{1,12}[\s_-]*(?<part>\d+)[\s_-]+\p{L}{1,8}[\s_-]+(?<parts>\d+)\s*(?:\(\d+\))?(?:\.zip)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PartPattern();
}
