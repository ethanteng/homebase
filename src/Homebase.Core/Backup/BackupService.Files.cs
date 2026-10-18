using System.Text.Json;
using Homebase.Core.Accounts;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Backup;

/// <summary>Something in one account's folder in a backup: its name, where it was, and what it was.</summary>
public sealed record BackupEntry(string Name, string Path, bool IsDirectory, long? Size, DateTimeOffset ModifiedAt);

/// <summary>One folder of one account's files, as a backup holds it.</summary>
public sealed record BackupFolder(string Snapshot, DateTimeOffset Time, string Path, IReadOnlyList<BackupEntry> Entries);

/// <summary>
/// Something restored from a backup, waiting to be put in place: where it was in the account's
/// folder, where it came back to, and what it is.
/// </summary>
public sealed record RestoredItem(string Path, string Restored, bool IsDirectory, long Size);

public sealed partial class BackupService
{
    /// <summary>How many things one restore puts back at most.</summary>
    private const int RestoreAtOnce = 100;

    /// <summary>One entry in a snapshot, as restic lists it.</summary>
    private sealed record SnapshotNode(string Path, bool IsDirectory, long Size, DateTimeOffset Modified);

    /// <summary>
    /// What's in one folder of one account's files in one of this Uncloud's backups. Only that
    /// account's: the folder is found under the account's own, whose id comes from the session,
    /// and nothing above it can be named. Hidden entries are left out, as they are in My files.
    /// </summary>
    public async Task<BackupFolder> BrowseAsync(string snapshot, string userId, string? path, CancellationToken cancellationToken)
    {
        var relative = Relative(path);
        var restic = Open();
        var (chosen, account) = await AccountInAsync(restic, snapshot, userId, cancellationToken);
        var folder = relative.Length == 0 ? account : $"{account}/{relative}";
        var listed = await restic.RunAsync(["--no-lock", "ls", "--json", chosen.Id, folder], _policy.OpenLimit, cancellationToken);
        if (!listed.Succeeded) throw Unreadable(listed, "read what's in the backup", _policy.OpenLimit);

        var nodes = Nodes(listed.Output);
        if (!nodes.Any(node => node.Path == folder && node.IsDirectory))
            throw new LibraryException("That folder isn’t in this backup.", "not_found");
        var entries = nodes
            .Where(node => System.IO.Path.GetDirectoryName(node.Path) == folder)
            .Select(node => (Node: node, Name: System.IO.Path.GetFileName(node.Path)))
            .Where(entry => !entry.Name.StartsWith('.'))
            .Select(entry => new BackupEntry(
                entry.Name, relative.Length == 0 ? entry.Name : $"{relative}/{entry.Name}", entry.Node.IsDirectory,
                entry.Node.IsDirectory ? null : entry.Node.Size, entry.Node.Modified))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new BackupFolder(chosen.Id, chosen.Time, relative, entries);
    }

    /// <summary>
    /// Restores files and folders of one account's from a backup into <paramref name="target"/>,
    /// and says where each came back to there. Each is measured first, and
    /// <paramref name="room"/> asked whether that much fits, so nothing is fetched that can't be
    /// kept. Waits for any tidying of the backup, as every restore does.
    /// </summary>
    public async Task<IReadOnlyList<RestoredItem>> RestoreForAsync(
        string snapshot, string userId, IReadOnlyList<string>? paths, string target, Action<long> room,
        CancellationToken cancellationToken)
    {
        var relatives = (paths ?? []).Select(Relative).Distinct().ToArray();
        if (relatives.Length == 0 || relatives.Any(path => path.Length == 0))
            throw new LibraryException("Choose something in the backup to restore.");
        if (relatives.Length > RestoreAtOnce)
            throw new LibraryException($"Restore up to {RestoreAtOnce} things at once.");
        // Something inside another thing being restored comes back with it.
        relatives = relatives.Where(path => !relatives.Any(other => other != path && path.StartsWith(other + "/", StringComparison.Ordinal))).ToArray();

        var restic = Open();
        await _maintenance.WaitAsync(cancellationToken);
        try
        {
            var (chosen, account) = await AccountInAsync(restic, snapshot, userId, cancellationToken);
            var items = new List<RestoredItem>();
            foreach (var relative in relatives)
            {
                var full = $"{account}/{relative}";
                var listed = await restic.RunAsync(
                    ["--no-lock", "ls", "--json", chosen.Id, System.IO.Path.GetDirectoryName(full)!], _policy.OpenLimit, cancellationToken);
                if (!listed.Succeeded) throw Unreadable(listed, "read what's in the backup", _policy.OpenLimit);
                var node = Nodes(listed.Output).FirstOrDefault(candidate => candidate.Path == full)
                    ?? throw new LibraryException($"“{System.IO.Path.GetFileName(relative)}” isn’t in this backup.", "not_found");
                var size = node.Size;
                if (node.IsDirectory)
                {
                    var inside = await restic.RunAsync(
                        ["--no-lock", "ls", "--json", "--recursive", chosen.Id, full], _policy.ForgetLimit, cancellationToken);
                    if (!inside.Succeeded) throw Unreadable(inside, "read what's in the backup", _policy.ForgetLimit);
                    size = Nodes(inside.Output).Where(candidate => !candidate.IsDirectory).Sum(candidate => candidate.Size);
                }
                items.Add(new RestoredItem(relative, Restored(target, full), node.IsDirectory, size));
            }
            room(items.Sum(item => item.Size));

            List<string> arguments = ["--no-lock", "restore", chosen.Id, "--target", target];
            // Named on the command line, where restic reads a $ as itself, and escaped, since
            // each is a pattern there.
            foreach (var relative in relatives) arguments.AddRange(["--include", Pattern($"{account}/{relative}")]);
            var restored = await restic.RunAsync(arguments, _policy.RestoreLimit, cancellationToken);
            if (!restored.Succeeded) throw Unreadable(restored, "restore from the backup", _policy.RestoreLimit);
            return items;
        }
        finally
        {
            _maintenance.Release();
        }
    }

    /// <summary>
    /// One of this Uncloud's snapshots, named in full or by an unambiguous prefix of at least eight
    /// characters, and where one account's files are in it: under the users folder it was made of.
    /// </summary>
    private async Task<(BackupSnapshot Snapshot, string Account)> AccountInAsync(
        Restic restic, string snapshot, string userId, CancellationToken cancellationToken)
    {
        var ours = (await SnapshotsAsync(restic, cancellationToken))
            .Where(known => known.Id == snapshot || (snapshot.Length >= 8 && known.Id.StartsWith(snapshot, StringComparison.Ordinal)))
            .ToList();
        if (ours.Count != 1) throw new LibraryException("That isn’t one of this Uncloud’s backups.", "not_found");
        var users = ours[0].Paths.FirstOrDefault(path => System.IO.Path.GetFileName(path.TrimEnd('/')) == UserPaths.UsersDirectory)
            ?? throw new LibraryException("This backup has nobody’s files in it.", "not_found");
        return (ours[0], $"{users.TrimEnd('/')}/{userId}");
    }

    /// <summary>
    /// A path in an account's folder as asked for, checked and tidied: inside the folder, and never
    /// through anything hidden. It needn't be there now: what's restored is often what's gone.
    /// </summary>
    private static string Relative(string? path)
    {
        path ??= "";
        if (System.IO.Path.IsPathRooted(path) || path.Contains('\\') || path.Contains('\0'))
            throw new LibraryException("Use a path inside your Uncloud folder.");
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".." || part.StartsWith('.')))
            throw new LibraryException("Hidden folders and paths outside Uncloud aren’t accessible.");
        return string.Join('/', parts);
    }

    /// <summary>
    /// restic couldn't do something asked of it for somebody's files. What it said goes to the log,
    /// since it can name the repository; the answer says what wasn't done.
    /// </summary>
    private LibraryException Unreadable(ResticResult result, string doing, TimeSpan limit)
    {
        _logger.LogWarning("restic couldn't {Doing} (exit {Code}{TimedOut}): {Said}",
            doing, result.ExitCode, result.TimedOut ? ", timed out" : "", result.Said(5));
        return new LibraryException(
            result.TimedOut || result.Refused ? Explain(result, doing, limit) : $"Uncloud couldn’t {doing}. Try again in a minute.",
            "unavailable");
    }

    /// <summary>The entries restic lists, files and folders alike.</summary>
    private static List<SnapshotNode> Nodes(IReadOnlyList<string> lines)
    {
        var nodes = new List<SnapshotNode>();
        foreach (var line in lines)
        {
            if (!line.Contains("\"struct_type\":\"node\"", StringComparison.Ordinal)) continue;
            using var node = JsonDocument.Parse(line);
            var root = node.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type is not ("file" or "dir")) continue;
            nodes.Add(new SnapshotNode(
                root.GetProperty("path").GetString()!,
                type == "dir",
                root.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                Time(root.GetProperty("mtime").GetString()!)));
        }
        return nodes;
    }
}
