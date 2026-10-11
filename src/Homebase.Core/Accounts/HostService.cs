namespace Homebase.Core.Accounts;

/// <summary>
/// The one directory everything on this host is kept in. An administrator chooses it; every
/// account's folder is then made underneath it, and everyone draws from the same volume.
/// </summary>
public sealed class HostService
{
    private const string RootKey = "root_path";
    private readonly ControlDatabase _database;
    private readonly Lock _lock = new();
    private string? _root;

    /// <param name="initial">
    /// The folder to start with when nobody has chosen one yet. The Mac app chooses it when it sets
    /// Uncloud up in an account of its own, because it is the one that can open a folder to that
    /// account. Once anybody chooses a folder here, that choice is the one kept.
    /// </param>
    public HostService(ControlDatabase database, SettingsStore? legacy = null, string? initial = null)
    {
        _database = database;
        _root = Read();
        // A v0 library was one person's folder recorded in settings.json. Adopt it as the host's
        // root so an upgrade doesn't land on the setup screen; nothing inside it is moved.
        if (_root is null && legacy?.Load() is { } adopted && Directory.Exists(adopted))
        {
            Write(adopted);
            _root = adopted;
        }
        if (_root is null && !string.IsNullOrWhiteSpace(initial))
        {
            // Only a folder that is there and can hold accounts. Otherwise this host starts with no
            // folder, as any other does, and an administrator is asked for one.
            try { SelectRoot(initial); }
            catch (Exception failure) when (failure is LibraryException or IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Whether this host runs in an operating-system account of its own, which nobody signs in to.
    /// It can then only use a folder that has been opened to that account, and saying so is more
    /// use to somebody than "that folder doesn't exist".
    /// </summary>
    public bool OwnAccount { get; init; }

    public string? RootPath
    {
        get { lock (_lock) return _root; }
    }

    public bool IsConfigured => RootPath is not null;

    public string RequireRoot() => RootPath ?? throw new LibraryException(
        "This Uncloud doesn't have a folder yet. An administrator needs to choose one.", "not_configured");

    public string SelectRoot(string path)
    {
        string root;
        // A folder this account can't reach looks to it exactly like one that isn't there.
        try { root = PathPolicy.NormalizeRoot(path); }
        catch (LibraryException missing) when (OwnAccount && missing.Code == "not_found") { throw NotOpenedToUs(); }
        if (root.Split(Path.DirectorySeparatorChar).Any(part => part.Equals(".homebase", StringComparison.OrdinalIgnoreCase)))
            throw new LibraryException("Choose your files folder, not a .homebase metadata folder.");
        // Made now rather than on the first sign-in, so a folder that can't hold accounts is
        // refused while somebody is still looking at the folder chooser.
        try { Directory.CreateDirectory(Path.Combine(root, UserPaths.UsersDirectory)); }
        catch (UnauthorizedAccessException) when (OwnAccount) { throw NotOpenedToUs(); }
        lock (_lock)
        {
            Write(root);
            _root = root;
        }
        return root;
    }

    private static LibraryException NotOpenedToUs() => new(
        "Uncloud can’t use that folder. On this Mac it keeps everyone’s files in an account of its "
        + "own, so it can only use a folder that’s been opened to it. On the host, choose Use Another "
        + "Folder… in the Uncloud menu, then enter the folder it gives you here.",
        "forbidden");

    private string? Read() => _database.Setting(RootKey);

    private void Write(string root) => _database.SetSetting(RootKey, root);
}
