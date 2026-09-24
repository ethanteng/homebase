using Homebase.Core.Accounts;
using Microsoft.Data.Sqlite;

namespace Homebase.Core.Providers;

/// <summary>
/// Somewhere on this computer worth offering, rather than making somebody type it out.
/// <paramref name="Kind"/> is what it looks like to a person: an ordinary folder, the folder a
/// cloud app keeps, or a drive plugged into this computer.
/// </summary>
public sealed record SuggestedPlace(string Name, string Path, string Kind);

/// <summary>
/// The folders on this computer that people bring files in from, whose each one is, who else may
/// use it, and the rules about which folders those are allowed to be.
///
/// A folder is its owner's alone until they share it. Sharing is a separate, deliberate step,
/// because most of what is on this computer — somebody's Documents, their Desktop — is nobody
/// else's business.
///
/// The rules are the isolation boundary, not a convenience. Uncloud runs as one operating-system
/// user, so a place that contains the host's storage root would hand whoever can read it every
/// other account's files, and a place that contains the preference directory would hand them the
/// password hashes and the key that seals everybody's Dropbox tokens. Both are refused when a
/// place is added and again every time one is used, because the storage root can move afterwards.
/// </summary>
public sealed class ImportPlaces(ControlDatabase database, HostService host, string configDirectory)
{
    /// <summary>
    /// How many times resolving a path may rewrite it before Uncloud decides it doesn't know where
    /// the folder really is. A pass follows every link in the path to its final target, so another
    /// pass is only wanted when doing that uncovered a link in an *ancestor* — /var on macOS, say.
    /// Real arrangements settle in two; the margin is for ones nobody has thought of. Replaced in
    /// tests, because contriving a path that outlasts the real bound proves less than choosing it.
    /// </summary>
    public int Rewrites { get; init; } = 8;

    /// <summary>This computer's home folder, where suggestions come from; replaced in tests.</summary>
    public string Home { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Where this computer mounts drives plugged into it; replaced in tests.</summary>
    public IReadOnlyList<string> DriveFolders { get; init; } = OperatingSystem.IsMacOS()
        ? ["/Volumes"]
        : OperatingSystem.IsLinux() ? [$"/media/{Environment.UserName}", $"/run/media/{Environment.UserName}"] : [];

    private const string Columns = """
        SELECT p.id, p.name, p.path, p.added_at, p.owner_id, p.shared, u.display_name
        FROM import_places p LEFT JOIN users u ON u.id = p.owner_id
        """;

    // A place is only read through somebody who still looks after this host. An administrator who
    // is demoted or disabled stops being able to read this computer's folders, and so does anyone
    // they had shared one with: the sharing was only ever theirs to give.
    private const string Usable = "u.is_admin = 1 AND u.disabled_at IS NULL";

    /// <summary>
    /// Every place on this host, whoever it belongs to. Only for checks about the host itself,
    /// such as whether its folder may move somewhere; what a person may use is
    /// <see cref="VisibleTo"/>.
    /// </summary>
    public IReadOnlyList<ImportPlace> List() => Query("", _ => { });

    /// <summary>The places this account may bring files in from: its own, and those shared with everyone.</summary>
    public IReadOnlyList<ImportPlace> VisibleTo(string userId) =>
        Query($"WHERE {Usable} AND (p.owner_id = $user OR p.shared = 1)",
            parameters => parameters.AddWithValue("$user", userId));

    /// <summary>
    /// One place this account may use. A place that exists but isn't theirs to use is reported as
    /// missing, not as forbidden: somebody else's private folder is not a thing to confirm exists.
    /// </summary>
    public ImportPlace Find(string id, string userId) =>
        VisibleTo(userId).FirstOrDefault(place => place.Id == id)
        ?? throw new LibraryException("That folder isn’t available any more.", "not_found");

    /// <summary>
    /// The place, checked against today's storage root and confirmed to still be there. Everything
    /// that reads from a place goes through here rather than through <see cref="Find"/>.
    /// </summary>
    public ImportPlace Require(string id, string userId)
    {
        var place = Find(id, userId);
        if (!Directory.Exists(place.Path))
            throw new LibraryException(
                $"“{place.Name}” isn’t on this computer right now. If it’s on a drive, plug the drive back in.",
                "unavailable");
        // Re-checked rather than trusted: the host's folder may have moved since this was added,
        // and a place that now holds it would read straight across everybody's accounts.
        if (Refusal(place.Path) is { } refusal) throw new LibraryException(refusal, "forbidden");
        return place;
    }

    /// <summary>
    /// The place that would hold, or sit inside, this folder — or null when none would. Asked before
    /// the host's folder moves: a folder people bring files in from is not somewhere everybody's
    /// files can live, and refusing the move is kinder than silently disabling the place afterwards.
    /// </summary>
    public ImportPlace? Conflicting(string root)
    {
        var resolved = Safe(root);
        return List().FirstOrDefault(place => Nested(Safe(place.Path), resolved));
    }

    /// <summary>
    /// Adds a folder for <paramref name="ownerId"/> alone. Adding the same folder again hands back
    /// the one already there, so choosing a folder twice is never an error to explain.
    /// </summary>
    public ImportPlace Add(string ownerId, string? path, string? name = null)
    {
        var resolved = PathPolicy.NormalizeRoot(path ?? "");
        PathPolicy.RejectLink(resolved);
        if (Refusal(resolved) is { } refusal) throw new LibraryException(refusal, "forbidden");
        var own = Owned(ownerId);
        if (own.FirstOrDefault(place => Same(Safe(place.Path), Safe(resolved))) is { } already) return already;

        var label = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(resolved) : name.Trim();
        if (label.Length == 0) label = "Imported";
        if (label.Length > 60) label = label[..60].TrimEnd();
        // Each place writes into a folder of its own at the top of My files, so the names that
        // become folders stay distinct among everything this person can bring files in from. Two
        // folders both called "Photos" is an ordinary thing to have, so the second is numbered.
        var taken = own.Concat(VisibleTo(ownerId))
            .Select(place => ImportPlace.FolderName(place.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = label;
        for (var number = 2; taken.Contains(ImportPlace.FolderName(unique)); number++)
            unique = $"{label} {number}";

        var place = new ImportPlace(ImportPlace.NewId(), unique, resolved, DateTimeOffset.UtcNow, ownerId, Shared: false);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO import_places(id, name, path, added_at, owner_id, shared)
            VALUES ($id, $name, $path, $added, $owner, 0)
            """;
        command.Parameters.AddWithValue("$id", place.Id);
        command.Parameters.AddWithValue("$name", place.Name);
        command.Parameters.AddWithValue("$path", place.Path);
        command.Parameters.AddWithValue("$added", place.AddedAt.ToString("O"));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.ExecuteNonQuery();
        return place;
    }

    /// <summary>
    /// Shares one of <paramref name="ownerId"/>'s folders with everyone here, or stops sharing it.
    /// Only its owner can do either; to anybody else it isn't theirs, and so isn't there.
    /// </summary>
    public ImportPlace SetShared(string id, string ownerId, bool shared)
    {
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE import_places SET shared = $shared WHERE id = $id AND owner_id = $owner";
            command.Parameters.AddWithValue("$shared", shared ? 1 : 0);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$owner", ownerId);
            if (command.ExecuteNonQuery() == 0)
                throw new LibraryException("That folder isn’t available any more.", "not_found");
        }
        return Owned(ownerId).First(place => place.Id == id);
    }

    /// <summary>
    /// Takes one of <paramref name="ownerId"/>'s folders off their list. Nothing already brought
    /// home is touched or forgotten: those are ordinary files in somebody's folder now, and the
    /// record of where they came from is theirs.
    /// </summary>
    public ImportPlace Remove(string id, string ownerId)
    {
        var place = Owned(ownerId).FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new LibraryException("That folder isn’t available any more.", "not_found");
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM import_places WHERE id = $id AND owner_id = $owner";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$owner", ownerId);
        command.ExecuteNonQuery();
        return place;
    }

    private IReadOnlyList<ImportPlace> Owned(string ownerId) =>
        Query("WHERE p.owner_id = $owner", parameters => parameters.AddWithValue("$owner", ownerId));

    private IReadOnlyList<ImportPlace> Query(string where, Action<SqliteParameterCollection> bind)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"{Columns} {where} ORDER BY p.name COLLATE NOCASE";
        bind(command.Parameters);
        using var reader = command.ExecuteReader();
        var places = new List<ImportPlace>();
        while (reader.Read())
            places.Add(new ImportPlace(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? "" : reader.GetString(4),
                reader.GetInt64(5) != 0,
                reader.IsDBNull(6) ? "" : reader.GetString(6)));
        return places;
    }

    /// <summary>
    /// Folders worth offering <paramref name="ownerId"/>: the ones people keep things in, the ones a
    /// desktop sync app puts where everybody expects, and drives plugged into this computer. Only
    /// what exists, is allowed, and isn't already on their list.
    /// </summary>
    public IReadOnlyList<SuggestedPlace> Suggestions(string ownerId)
    {
        var taken = Owned(ownerId);
        return Candidates()
            .Where(candidate => !taken.Any(place => Same(Safe(place.Path), Safe(candidate.Path))))
            .ToArray();
    }

    /// <summary>
    /// What each folder looks like to a person, whether or not it was ever suggested: a drive, a
    /// cloud app's folder, or an ordinary one. Looked around for once, then asked per folder.
    /// </summary>
    public Func<string, string> Kinds()
    {
        var candidates = Candidates().Select(candidate => (Real: Safe(candidate.Path), candidate.Kind)).ToArray();
        var drives = DriveFolders.Select(Safe).ToArray();
        return path =>
        {
            var real = Safe(path);
            if (drives.Any(folder => Inside(folder, real))) return "drive";
            return candidates.FirstOrDefault(candidate => Same(candidate.Real, real)).Kind ?? "folder";
        };
    }

    private IReadOnlyList<SuggestedPlace> Candidates()
    {
        var home = Home;
        if (home.Length == 0) return [];
        var candidates = new List<SuggestedPlace>();

        void Offer(string name, string path, string kind)
        {
            if (Directory.Exists(path)) candidates.Add(new SuggestedPlace(name, path, kind));
        }

        // The folders people keep their own things in come first: they are what most people are
        // looking for, and they need nothing else installed.
        foreach (var name in new[] { "Desktop", "Documents", "Downloads", "Pictures", "Movies", "Music" })
            Offer(name, Path.Combine(home, name), "folder");

        Offer("Dropbox", Path.Combine(home, "Dropbox"), "cloud");
        Offer("Google Drive", Path.Combine(home, "Google Drive"), "cloud");
        Offer("OneDrive", Path.Combine(home, "OneDrive"), "cloud");
        // Where macOS mounts the file providers of Google Drive, OneDrive, Box and the rest, under
        // names that carry the signed-in address. Nobody wants "GoogleDrive-me@example.com" as a
        // folder in their library, so the service's own name is what gets offered.
        var cloudStorage = Path.Combine(home, "Library", "CloudStorage");
        try
        {
            if (Directory.Exists(cloudStorage))
                foreach (var directory in Directory.EnumerateDirectories(cloudStorage).Order(StringComparer.OrdinalIgnoreCase))
                    candidates.Add(new SuggestedPlace(ServiceName(Path.GetFileName(directory)), directory, "cloud"));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A file provider Uncloud isn't allowed to look at is one fewer thing to offer, not a
            // reason to offer nothing: the folders around it are still worth suggesting.
        }

        // Drives plugged into this computer: an old backup drive is one of the commonest places a
        // household's files are waiting. The startup disk appears among them on a Mac as a link to
        // the root of everything, which is not a drive anybody means, so links are passed over.
        foreach (var drives in DriveFolders)
        {
            try
            {
                if (!Directory.Exists(drives)) continue;
                foreach (var drive in new DirectoryInfo(drives).EnumerateDirectories()
                             .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase))
                    if (drive.LinkTarget is null && !drive.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        && !drive.Name.StartsWith('.'))
                        candidates.Add(new SuggestedPlace(drive.Name, drive.FullName, "drive"));
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Drives that can't be listed are simply not offered.
            }
        }

        return candidates
            // A drive holding everybody's Uncloud files, or a home folder that does, is left out
            // exactly as it would be refused if somebody chose it by hand.
            .Where(candidate => Refusal(Safe(candidate.Path)) is null)
            // Two entries can resolve to the same folder — ~/Dropbox is often a link into
            // CloudStorage — and offering it twice would just be a way to add it twice.
            .DistinctBy(candidate => Safe(candidate.Path), StringComparer.OrdinalIgnoreCase)
            .DistinctBy(candidate => ImportPlace.FolderName(candidate.Name), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>"GoogleDrive-me@example.com" as a person would say it.</summary>
    private static string ServiceName(string directory)
    {
        var service = directory.Split('-', 2)[0];
        return service switch
        {
            "GoogleDrive" => "Google Drive",
            "OneDrive" => "OneDrive",
            "Dropbox" => "Dropbox",
            "Box" => "Box",
            "ProtonDrive" => "Proton Drive",
            _ => service.Length == 0 ? directory : service
        };
    }

    /// <summary>
    /// The real folder behind a path, whatever it is spelled as, so two names for one folder
    /// compare equal. Resolving once is not enough: a link is resolved to the target it stores,
    /// and that target can itself run through a link — /var/… on macOS, where every temporary
    /// folder and many a home directory lives. So it is resolved until it stops moving.
    ///
    /// Used where an unsettled answer is no worse than a settled one, such as offering
    /// suggestions. Anything that refuses on a comparison uses <see cref="Settle"/> instead.
    /// </summary>
    private string Safe(string path) => Settle(path, out _);

    /// <summary>
    /// As above, saying whether it got there. <paramref name="settled"/> is false only when the
    /// path was still moving after <see cref="Rewrites"/> passes, which is the one case where the
    /// answer is not the real folder and must not be compared against anything: a chain of links
    /// long enough to outlast the bound would otherwise let a folder be matched under a name that
    /// hides where it really is. A path that cannot be resolved at all is a different matter and
    /// comes back as it went in — it has no real folder to hide.
    /// </summary>
    private string Settle(string path, out bool settled)
    {
        var current = path;
        // Bounded: a cycle of links would otherwise be an infinite loop rather than a refusal.
        for (var attempt = 0; attempt < Rewrites; attempt++)
        {
            string resolved;
            try { resolved = PathPolicy.NormalizeRoot(current); }
            catch (Exception failure) when (failure is LibraryException or IOException)
            {
                settled = true;
                return current;
            }
            if (resolved == current)
            {
                settled = true;
                return current;
            }
            current = resolved;
        }
        settled = false;
        return current;
    }

    /// <summary>Why this folder can't be a place, or null when it can.</summary>
    private string? Refusal(string path)
    {
        // Every side resolved before anything is compared. These refusals work by matching two
        // paths, so a folder reached one way and named another — /var against /private/var on
        // macOS, or any symbolic link above it — would be two strings that don't match and a
        // check that quietly passes on a spelling.
        var folder = Settle(path, out var folderSettled);
        var settings = Settle(configDirectory, out var settingsSettled);
        // A path that never stops moving is one Uncloud cannot say the real folder of, and every
        // check below is a comparison against that folder. Refusing is the only safe answer:
        // letting it through would be deciding it is not the host's folder on the strength of a
        // name that doesn't say where it goes.
        if (!folderSettled || !settingsSettled)
            return "Uncloud can’t work out which folder that really is — it’s reached through too many linked folders. Choose it by its own path instead.";
        if (Nested(folder, settings))
            return "That folder holds Uncloud’s own settings, which includes everybody’s passwords. Choose another one.";
        if (host.RootPath is { } root)
        {
            var library = Settle(root, out var librarySettled);
            if (!librarySettled || Nested(folder, library))
                return "That folder holds everybody’s Uncloud files. Bringing files in from it would let anyone here read everybody else’s, so choose a folder outside it.";
        }
        return null;
    }

    /// <summary>Whether either folder is the other, or sits inside it. Both directions matter.</summary>
    private static bool Nested(string first, string second) =>
        Same(first, second) || Inside(first, second) || Inside(second, first);

    private static bool Inside(string outer, string inner) =>
        inner.StartsWith(
            Path.TrimEndingDirectorySeparator(outer) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    // Case-insensitively, which is how macOS compares paths by default. Treating two spellings of
    // one folder as the same folder is the safe way to be wrong about a filesystem that doesn't.
    private static bool Same(string first, string second) =>
        Path.TrimEndingDirectorySeparator(first)
            .Equals(Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);
}
