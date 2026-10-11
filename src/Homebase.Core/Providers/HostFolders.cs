namespace Homebase.Core.Providers;

/// <summary>
/// A folder on the host worth offering, with the real folder behind its path. <paramref name="Real"/>
/// is <paramref name="Path"/> with every link resolved away; <paramref name="Settled"/> is false only
/// when it was still moving after the passes ran out, which makes it unfit to compare against.
/// </summary>
public sealed record HostFolder(string Name, string Path, string Real, bool Settled, string Kind);

/// <summary>
/// What there is to offer on the host: its usual folders and drives, and the folders drives are
/// mounted in, resolved, so any folder beneath one of them can be called a drive.
/// </summary>
public sealed record HostFolderCandidates(IReadOnlyList<string> Drives, IReadOnlyList<HostFolder> Folders);

/// <summary>
/// The folders on the computer Uncloud runs on, as the person who looks after it sees them: their
/// Documents, the folder their Google Drive app keeps, a drive they plugged in.
///
/// Read directly when Uncloud runs as that person. On a Mac where it runs in an account of its own
/// — which is what keeps everybody's files out of reach of whoever is signed in at the Mac — that
/// account can't read anybody's folders, so they are read by the Uncloud app running as the person
/// signed in there, and handed across. <see cref="ImportPlaces"/> decides which folders may be read
/// at all; this only reads them.
/// </summary>
public interface IHostFolders
{
    /// <summary>The folders worth offering, before any are refused.</summary>
    HostFolderCandidates Candidates(int rewrites);

    /// <summary>
    /// The real folder behind a path, resolved until it stops moving, for comparing against.
    /// <paramref name="settled"/> is false when it was still moving after <paramref name="rewrites"/>
    /// passes. A path that cannot be resolved at all comes back as it went in.
    /// </summary>
    string Settle(string path, int rewrites, out bool settled);

    /// <summary>A folder somebody named, as an absolute path to a folder that is there and isn't a link.</summary>
    string Normalize(string path);

    /// <summary>Whether the folder is there now. False for a drive that isn't plugged in.</summary>
    bool Exists(string path);

    /// <summary>The place, as something the import engine can read.</summary>
    IImportSource Open(ImportPlace place);
}

/// <summary>The host's folders, read by this process, as the operating-system user it runs as.</summary>
public sealed class LocalHostFolders : IHostFolders
{
    /// <summary>This computer's home folder, where suggestions come from; replaced in tests.</summary>
    public string Home { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Where this computer mounts drives plugged into it; replaced in tests.</summary>
    public IReadOnlyList<string> DriveFolders { get; set; } = OperatingSystem.IsMacOS()
        ? ["/Volumes"]
        : OperatingSystem.IsLinux() ? [$"/media/{Environment.UserName}", $"/run/media/{Environment.UserName}"] : [];

    public HostFolderCandidates Candidates(int rewrites)
    {
        HostFolder Found(SuggestedPlace place) =>
            new(place.Name, place.Path, Resolve(place.Path, rewrites, out var settled), settled, place.Kind);
        return new HostFolderCandidates(
            DriveFolders.Select(folder => Resolve(folder, rewrites, out _)).ToArray(),
            Offered().Select(Found).ToArray());
    }

    public string Settle(string path, int rewrites, out bool settled) => Resolve(path, rewrites, out settled);

    public string Normalize(string path)
    {
        var resolved = PathPolicy.NormalizeRoot(path);
        PathPolicy.RejectLink(resolved);
        return resolved;
    }

    public bool Exists(string path) => Directory.Exists(path);

    public IImportSource Open(ImportPlace place) => new LocalFolderSource(place);

    /// <summary>
    /// The real folder behind a path, whatever it is spelled as, so two names for one folder
    /// compare equal. Resolving once is not enough: a link is resolved to the target it stores,
    /// and that target can itself run through a link — /var/… on macOS, where every temporary
    /// folder and many a home directory lives. So it is resolved until it stops moving.
    ///
    /// <paramref name="settled"/> is false only when the path was still moving after
    /// <paramref name="rewrites"/> passes, which is the one case where the answer is not the real
    /// folder and must not be compared against anything: a chain of links long enough to outlast
    /// the bound would otherwise let a folder be matched under a name that hides where it really
    /// is. A path that cannot be resolved at all is a different matter and comes back as it went
    /// in — it has no real folder to hide.
    /// </summary>
    public static string Resolve(string path, int rewrites, out bool settled)
    {
        var current = path;
        // Bounded: a cycle of links would otherwise be an infinite loop rather than a refusal.
        for (var attempt = 0; attempt < rewrites; attempt++)
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

    private IReadOnlyList<SuggestedPlace> Offered()
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
        // So is an app's installer left open after installing it — Uncloud's own among them.
        foreach (var drives in DriveFolders)
        {
            try
            {
                if (!Directory.Exists(drives)) continue;
                foreach (var drive in new DirectoryInfo(drives).EnumerateDirectories()
                             .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase))
                    if (drive.LinkTarget is null && !drive.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        && !drive.Name.StartsWith('.') && !Installer(drive))
                        candidates.Add(new SuggestedPlace(drive.Name, drive.FullName, "drive"));
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Drives that can't be listed are simply not offered.
            }
        }
        return candidates;
    }

    /// <summary>
    /// Whether this drive is an app's installer: a disk image holding the app and a shortcut to
    /// Applications to drag it onto, and nothing else anybody would see. macOS mounts one every
    /// time it is opened and numbers them when they pile up, so six downloads left open are six
    /// "drives" called Uncloud, Uncloud 1, Uncloud 2… none of which has anybody's files on it.
    ///
    /// Read only until the first thing that couldn't be in an installer, which on a drive with
    /// files on it is almost always the first thing there. A drive that can't be read is still
    /// offered: opening it is what explains why it can't be.
    /// </summary>
    private static bool Installer(DirectoryInfo drive)
    {
        // Both are needed: a drive of nothing but apps, with no shortcut to drag them onto, is as
        // likely to be somebody's archive of old software as an installer.
        var apps = false;
        var shortcut = false;
        try
        {
            foreach (var entry in drive.EnumerateFileSystemInfos())
            {
                // What Finder hides — .DS_Store, the background picture, the volume's icon — is
                // how an installer is dressed, not what it holds.
                if (entry.Name.StartsWith('.') || entry.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                if (entry.LinkTarget is { } target)
                {
                    if (!Path.TrimEndingDirectorySeparator(target)
                            .Equals("/Applications", StringComparison.OrdinalIgnoreCase))
                        return false;
                    shortcut = true;
                }
                else if (entry is DirectoryInfo && entry.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    apps = true;
                else
                    return false;
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return apps && shortcut;
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
}
