using System.Text.Json;
using System.Text.RegularExpressions;
using Homebase.Core.Accounts;

namespace Homebase.Core;

/// <summary>
/// The bin. Deleting moves things here rather than removing them, so a mistake — or a deletion that
/// reached a synced laptop too — can be put back for <see cref="KeepDeletedFor"/>.
///
/// It lives in the account's own <c>.homebase</c> folder: on the same drive, so going in and coming
/// out are renames however large the folder; never shown, copied or synced, since everything under
/// <c>.homebase</c> is kept out of all three; and counted as this account's, because until it goes
/// for good it still takes up room. Each thing deleted gets a folder of its own named by an id,
/// holding it under its own name, and a record beside it saying where it came from.
/// </summary>
public sealed partial class LibraryService
{
    /// <summary>How long something deleted can be put back before it goes for good.</summary>
    public static readonly TimeSpan KeepDeletedFor = TimeSpan.FromDays(30);

    /// <summary>
    /// What the bin knows about one thing in it. Kept in a file beside it rather than in the index,
    /// which is a cache and may be thrown away.
    /// </summary>
    private sealed record Deleted(string Path, bool IsDirectory, long Size, DateTimeOffset DeletedAt);

    private static readonly JsonSerializerOptions RecordFormat = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex BinId();

    public async Task<BinListing> BinAsync(CancellationToken cancellationToken)
    {
        await _edits.WaitAsync(cancellationToken);
        try
        {
            var bin = BinFolder();
            Purge(bin);
            var entries = Records(bin)
                .Where(entry => Held(bin, entry.Id, entry.Record) is not null)
                .Select(entry => new BinEntry(entry.Id, Path.GetFileName(entry.Record.Path), entry.Record.Path,
                    entry.Record.IsDirectory, entry.Record.Size, entry.Record.DeletedAt, entry.Record.DeletedAt + KeepDeletedFor))
                .OrderByDescending(entry => entry.DeletedAt)
                .ToArray();
            return new BinListing(entries, entries.Sum(entry => entry.Size));
        }
        finally { _edits.Release(); }
    }

    /// <summary>
    /// Puts things back where they were deleted from, making the folders above them again if those
    /// have gone too. A name taken there since is kept, and the one coming back becomes “name 2”.
    /// </summary>
    public async Task<IReadOnlyList<EditedEntry>> RestoreAsync(IReadOnlyList<string>? ids, CancellationToken cancellationToken)
    {
        await _edits.WaitAsync(cancellationToken);
        try
        {
            var bin = BinFolder();
            var chosen = Chosen(bin, ids)
                .Select(entry => (entry.Id, entry.Record, entry.Item, Folder: Path.GetDirectoryName(PathPolicy.Resolve(Root, entry.Record.Path))!))
                .ToArray();
            foreach (var entry in chosen) AwayFromImport([], entry.Folder, "put back");

            var restored = new List<EditedEntry>();
            foreach (var (id, record, item, folder) in chosen)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var above = folder; above.Length > Root.Length; above = Path.GetDirectoryName(above)!)
                    if (File.Exists(above))
                        throw new LibraryException(
                            $"“{Path.GetFileName(item)}” was in a folder called “{Path.GetFileName(above)}”, and there’s a file "
                            + "by that name there now. Move or rename the file, then try again.", "conflict");
                Directory.CreateDirectory(folder);
                var name = Place(item, folder, Path.GetFileName(item), record.IsDirectory, copy: false);
                Discard(bin, id);
                restored.Add(new EditedEntry(record.Path, Join(folder, name)));
            }
            return restored;
        }
        finally { _edits.Release(); }
    }

    /// <summary>Deletes things in the bin for good, ahead of their time.</summary>
    public async Task<int> DeleteFromBinAsync(IReadOnlyList<string>? ids, CancellationToken cancellationToken)
    {
        await _edits.WaitAsync(cancellationToken);
        try
        {
            var bin = BinFolder();
            var chosen = Chosen(bin, ids);
            foreach (var entry in chosen) Discard(bin, entry.Id);
            return chosen.Count;
        }
        finally { _edits.Release(); }
    }

    /// <summary>Deletes everything in the bin for good, including anything half-deleted it can't list.</summary>
    public async Task<int> EmptyBinAsync(CancellationToken cancellationToken)
    {
        await _edits.WaitAsync(cancellationToken);
        try
        {
            var bin = BinFolder();
            var count = Records(bin).Count(entry => Held(bin, entry.Id, entry.Record) is not null);
            // A recursive delete removes a link it meets rather than following it.
            Directory.Delete(bin, recursive: true);
            Directory.CreateDirectory(bin);
            return count;
        }
        finally { _edits.Release(); }
    }

    private string BinFolder()
    {
        var bin = Path.Combine(Path.GetDirectoryName(PathPolicy.PrepareMetadata(Root))!, "bin");
        PathPolicy.RejectLink(bin);
        Directory.CreateDirectory(bin);
        return bin;
    }

    private static void ToBin(string bin, Item item)
    {
        var id = Guid.NewGuid().ToString("N");
        var size = item.IsDirectory ? UsageService.Walk(item.FullPath) : new FileInfo(item.FullPath).Length;
        // Written first, so the bin never holds something it can't say where to put back.
        var recordFile = Path.Combine(bin, $"{id}.json");
        PathPolicy.RejectLink(recordFile);
        File.WriteAllText(recordFile, JsonSerializer.Serialize(
            new Deleted(item.Relative, item.IsDirectory, size, DateTimeOffset.UtcNow), RecordFormat));
        try
        {
            var holder = Directory.CreateDirectory(Path.Combine(bin, id)).FullName;
            var to = Path.Combine(holder, item.Name);
            if (item.IsDirectory) Directory.Move(item.FullPath, to);
            else File.Move(item.FullPath, to);
        }
        catch
        {
            Discard(bin, id);
            throw;
        }
    }

    /// <summary>Everything in the bin with a record that reads. Anything else is left for emptying.</summary>
    private static IEnumerable<(string Id, Deleted Record)> Records(string bin)
    {
        foreach (var file in Directory.EnumerateFiles(bin, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!BinId().IsMatch(id) || new FileInfo(file).LinkTarget is not null) continue;
            Deleted? record;
            try { record = JsonSerializer.Deserialize<Deleted>(File.ReadAllText(file), RecordFormat); }
            catch (JsonException) { continue; }
            if (record is { Path.Length: > 0 }) yield return (id, record);
        }
    }

    /// <summary>Where the thing a record describes is kept, or null if it isn't there after all.</summary>
    private static string? Held(string bin, string id, Deleted record)
    {
        var item = Path.Combine(bin, id, Path.GetFileName(record.Path));
        if (new FileInfo(item).LinkTarget is not null) return null;
        return (record.IsDirectory ? Directory.Exists(item) : File.Exists(item)) ? item : null;
    }

    /// <summary>What a request names in the bin. An id is only ever matched, never used as a path.</summary>
    private static IReadOnlyList<(string Id, Deleted Record, string Item)> Chosen(string bin, IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count == 0) throw new LibraryException("Choose at least one thing in the bin.");
        var records = Records(bin).ToDictionary(entry => entry.Id, entry => entry.Record);
        return ids.Distinct().Select(id =>
            BinId().IsMatch(id) && records.TryGetValue(id, out var record) && Held(bin, id, record) is { } item
                ? (id, record, item)
                : throw new LibraryException("That’s no longer in the bin. Refresh and try again.", "not_found"))
            .ToArray();
    }

    private static void Purge(string bin)
    {
        var cutoff = DateTimeOffset.UtcNow - KeepDeletedFor;
        foreach (var (id, record) in Records(bin).ToArray())
            if (record.DeletedAt < cutoff) Discard(bin, id);
    }

    private static void Discard(string bin, string id)
    {
        var holder = Path.Combine(bin, id);
        PathPolicy.RejectLink(holder);
        // A recursive delete removes a link it meets rather than following it.
        if (Directory.Exists(holder)) Directory.Delete(holder, recursive: true);
        File.Delete(Path.Combine(bin, $"{id}.json"));
    }
}
