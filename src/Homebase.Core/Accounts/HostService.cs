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

    public HostService(ControlDatabase database, SettingsStore? legacy = null)
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
    }

    public string? RootPath
    {
        get { lock (_lock) return _root; }
    }

    public bool IsConfigured => RootPath is not null;

    public string RequireRoot() => RootPath ?? throw new LibraryException(
        "This Uncloud doesn't have a folder yet. An administrator needs to choose one.", "not_configured");

    public string SelectRoot(string path)
    {
        var root = PathPolicy.NormalizeRoot(path);
        if (root.Split(Path.DirectorySeparatorChar).Any(part => part.Equals(".homebase", StringComparison.OrdinalIgnoreCase)))
            throw new LibraryException("Choose your files folder, not a .homebase metadata folder.");
        // Made now rather than on the first sign-in, so a folder that can't hold accounts is
        // refused while somebody is still looking at the folder chooser.
        Directory.CreateDirectory(Path.Combine(root, UserPaths.UsersDirectory));
        lock (_lock)
        {
            Write(root);
            _root = root;
        }
        return root;
    }

    private string? Read()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM host_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", RootKey);
        return command.ExecuteScalar() as string;
    }

    private void Write(string root)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO host_settings(key, value) VALUES ($key, $value)";
        command.Parameters.AddWithValue("$key", RootKey);
        command.Parameters.AddWithValue("$value", root);
        command.ExecuteNonQuery();
    }
}
