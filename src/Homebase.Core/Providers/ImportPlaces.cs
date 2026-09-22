using Homebase.Core.Accounts;

namespace Homebase.Core.Providers;

/// <summary>Somewhere an administrator could add, offered rather than typed out.</summary>
public sealed record SuggestedPlace(string Name, string Path);

/// <summary>
/// The folders on this computer that Uncloud may bring files in from, and the rules about which
/// folders those are allowed to be.
///
/// The rules are the isolation boundary, not a convenience. Uncloud runs as one operating-system
/// user, so a place that contains the host's storage root would hand every member a way to read
/// every other account's files, and a place that contains the preference directory would hand them
/// the password hashes and the key that seals everybody's Dropbox tokens. Both are refused when a
/// place is added and again every time one is used, because the storage root can move afterwards.
/// </summary>
public sealed class ImportPlaces(ControlDatabase database, HostService host, string configDirectory)
{
    public IReadOnlyList<ImportPlace> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, path, added_at FROM import_places ORDER BY name COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var places = new List<ImportPlace>();
        while (reader.Read())
            places.Add(new ImportPlace(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3))));
        return places;
    }

    private ImportPlace Find(string id) =>
        List().FirstOrDefault(place => place.Id == id)
        ?? throw new LibraryException("That place isn’t on this Uncloud any more.", "not_found");

    /// <summary>
    /// The place, checked against today's storage root and confirmed to still be there. Everything
    /// that reads from a place goes through here rather than through <see cref="Find"/>.
    /// </summary>
    public ImportPlace Require(string id)
    {
        var place = Find(id);
        if (!Directory.Exists(place.Path))
            throw new LibraryException(
                $"“{place.Name}” isn’t on this computer right now. Plug the drive back in, or ask an administrator to remove it.",
                "unavailable");
        // Re-checked rather than trusted: the host's folder may have moved since this was added,
        // and a place that now holds it would read straight across everybody's accounts.
        if (Refusal(place.Path) is { } refusal) throw new LibraryException(refusal, "forbidden");
        return place;
    }

    /// <summary>
    /// The place that would hold, or sit inside, this folder — or null when none would. Asked before
    /// the host's folder moves: a folder everybody may read from is not somewhere everybody's files
    /// can live, and refusing the move is kinder than silently disabling the place afterwards.
    /// </summary>
    public ImportPlace? Conflicting(string root)
    {
        var resolved = Safe(root);
        return List().FirstOrDefault(place => Nested(Safe(place.Path), resolved));
    }

    public ImportPlace Add(string? path, string? name)
    {
        var resolved = PathPolicy.NormalizeRoot(path ?? "");
        PathPolicy.RejectLink(resolved);
        if (Refusal(resolved) is { } refusal) throw new LibraryException(refusal, "forbidden");
        var existing = List();
        if (existing.FirstOrDefault(place => Same(place.Path, resolved)) is { } already)
            throw new LibraryException($"That folder is already here, as “{already.Name}”.", "conflict");

        var label = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(resolved) : name.Trim();
        if (label.Length == 0) label = "Imported";
        if (label.Length > 60) label = label[..60].TrimEnd();
        // Two places writing into one folder under Files/ would make "already imported" ambiguous
        // between them, so the names that become folders have to stay distinct.
        var folder = ImportPlace.FolderName(label);
        if (existing.Any(place => ImportPlace.FolderName(place.Name).Equals(folder, StringComparison.OrdinalIgnoreCase)))
            throw new LibraryException($"Something here is already called “{folder}”. Give this one another name.", "conflict");

        var place = new ImportPlace(ImportPlace.NewId(), label, resolved, DateTimeOffset.UtcNow);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO import_places(id, name, path, added_at) VALUES ($id, $name, $path, $added)";
        command.Parameters.AddWithValue("$id", place.Id);
        command.Parameters.AddWithValue("$name", place.Name);
        command.Parameters.AddWithValue("$path", place.Path);
        command.Parameters.AddWithValue("$added", place.AddedAt.ToString("O"));
        command.ExecuteNonQuery();
        return place;
    }

    /// <summary>
    /// Takes a place off the list. Nothing already brought home is touched or forgotten: those are
    /// ordinary files in somebody's folder now, and the record of where they came from is theirs.
    /// </summary>
    public void Remove(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM import_places WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0)
            throw new LibraryException("That place isn’t on this Uncloud any more.", "not_found");
    }

    /// <summary>
    /// Folders worth offering: the ones a desktop sync app puts where everybody expects, and the
    /// home folders people keep things in. Only what exists, is allowed, and isn't already here.
    /// </summary>
    public IReadOnlyList<SuggestedPlace> Suggestions()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0) return [];
        var candidates = new List<SuggestedPlace>();

        void Offer(string name, string path)
        {
            if (Directory.Exists(path)) candidates.Add(new SuggestedPlace(name, path));
        }

        Offer("Dropbox", Path.Combine(home, "Dropbox"));
        Offer("Google Drive", Path.Combine(home, "Google Drive"));
        Offer("OneDrive", Path.Combine(home, "OneDrive"));
        // Where macOS mounts the file providers of Google Drive, OneDrive, Box and the rest, under
        // names that carry the signed-in address. Nobody wants "GoogleDrive-me@example.com" as a
        // folder in their library, so the service's own name is what gets offered.
        var cloudStorage = Path.Combine(home, "Library", "CloudStorage");
        try
        {
            if (Directory.Exists(cloudStorage))
                foreach (var directory in Directory.EnumerateDirectories(cloudStorage).Order(StringComparer.OrdinalIgnoreCase))
                    candidates.Add(new SuggestedPlace(ServiceName(Path.GetFileName(directory)), directory));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A file provider Uncloud isn't allowed to look at is one fewer thing to offer, not a
            // reason to offer nothing: the folders below are still worth suggesting.
        }
        foreach (var name in new[] { "Documents", "Desktop", "Pictures", "Movies", "Music" })
            Offer(name, Path.Combine(home, name));

        var taken = List();
        return candidates
            .Where(candidate => Refusal(Safe(candidate.Path)) is null)
            .Where(candidate => !taken.Any(place => Same(place.Path, Safe(candidate.Path))))
            // Two entries can resolve to the same folder — ~/Dropbox is often a link into
            // CloudStorage — and offering it twice would just be a way to fail the second time.
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

    /// <summary>Resolved the way a real add would resolve it, or left alone if it can't be.</summary>
    private static string Safe(string path)
    {
        try { return PathPolicy.NormalizeRoot(path); }
        catch (LibraryException) { return path; }
    }

    /// <summary>Why this folder can't be a place, or null when it can.</summary>
    private string? Refusal(string path)
    {
        if (Nested(path, configDirectory))
            return "That folder holds Uncloud’s own settings, which includes everybody’s passwords. Choose another one.";
        if (host.RootPath is { } root && Nested(path, Safe(root)))
            return "That folder holds everybody’s Uncloud files. Bringing files in from it would let anyone here read everybody else’s, so choose a folder outside it.";
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
