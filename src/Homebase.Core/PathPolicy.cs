namespace Homebase.Core;

public static class PathPolicy
{
    public static string NormalizeRoot(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new LibraryException("Choose an existing folder for Uncloud.");
        if (input == "~" || input.StartsWith("~/", StringComparison.Ordinal))
            input = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), input.Length > 2 ? input[2..] : "");
        if (!System.IO.Path.IsPathFullyQualified(input))
            throw new LibraryException("Enter an absolute folder path, such as /Users/you/Uncloud.");

        var fullPath = System.IO.Path.GetFullPath(input);
        if (!Directory.Exists(fullPath))
            throw new LibraryException("This folder doesn’t exist or isn’t available. Create it in Finder, then choose it here.", "not_found");

        // Resolve selected ancestors once (macOS /var and /tmp are links). Descendant links are never followed.
        var current = System.IO.Path.GetPathRoot(fullPath)!;
        foreach (var part in fullPath[current.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(System.IO.Path.Combine(current, part));
            current = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
        }
        return System.IO.Path.TrimEndingDirectorySeparator(current);
    }

    public static string Resolve(string root, string? relativePath) =>
        Resolve(root, relativePath,
            "Your Uncloud folder is unavailable. Reconnect the drive or choose another folder.",
            "Use a path inside your Uncloud folder.",
            "Hidden folders and paths outside Uncloud aren’t accessible.");

    /// <summary>
    /// The same containment rules, worded for somewhere that isn’t the library: a folder files are
    /// being brought in from is not the reader’s Uncloud folder, and saying so confuses people.
    /// Only the wording differs — what is refused does not.
    /// </summary>
    public static string Resolve(string root, string? relativePath, string unavailable, string outside, string hidden)
    {
        if (!Directory.Exists(root)) throw new LibraryException(unavailable, "unavailable");
        RejectLink(root);
        relativePath ??= "";
        if (System.IO.Path.IsPathRooted(relativePath) || relativePath.Contains('\\') || relativePath.Contains('\0'))
            throw new LibraryException(outside);

        var current = root;
        foreach (var part in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or ".." || part.StartsWith('.')) throw new LibraryException(hidden);
            current = System.IO.Path.Combine(current, part);
            RejectLink(current);
        }
        return current;
    }

    public static void RejectLink(string path)
    {
        var entry = new FileInfo(path);
        if (entry.LinkTarget is not null || (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new LibraryException("Symbolic links aren’t followed in Uncloud v0.");
    }

    public static string PrepareMetadata(string root)
    {
        Resolve(root, "");
        var directory = System.IO.Path.Combine(root, ".homebase");
        RejectLink(directory);
        Directory.CreateDirectory(directory);
        foreach (var file in new[] { "index.db", "index.db-wal", "index.db-shm", "index.db-journal" })
            RejectLink(System.IO.Path.Combine(directory, file));
        return System.IO.Path.Combine(directory, "index.db");
    }
}
