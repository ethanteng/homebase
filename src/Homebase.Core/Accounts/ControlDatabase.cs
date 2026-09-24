using Microsoft.Data.Sqlite;

namespace Homebase.Core.Accounts;

/// <summary>
/// The host's own database: who has an account, which sessions are live, which provider
/// connections and synced computers belong to whom, what each account has set for itself,
/// where the host keeps everyone's files, and which folders on this computer files may be
/// brought in from. It lives beside the host's preferences rather than under the storage root,
/// so it is outside the reach of the per-user boundary it helps define, and nothing in it
/// travels with anybody's files.
/// </summary>
public sealed class ControlDatabase(string directory)
{
    private readonly string _path = Path.Combine(directory, "homebase.db");

    /// <summary>
    /// One of the host's own settings — not an account's, and not a file's. The folder everybody's
    /// files live under is kept this way, and so is whether this host can be reached from outside.
    /// </summary>
    public string? Setting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM host_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO host_settings(key, value) VALUES ($key, $value)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public SqliteConnection Open()
    {
        System.IO.Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // Foreign keys are per-connection in SQLite, so this has to be said every time: it is
            // what makes deleting an account take its sessions and connector tokens with it.
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS host_settings (
                    key TEXT PRIMARY KEY, value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS users (
                    id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE, display_name TEXT NOT NULL,
                    password_hash TEXT NOT NULL, is_admin INTEGER NOT NULL,
                    created_at TEXT NOT NULL, disabled_at TEXT
                );
                CREATE TABLE IF NOT EXISTS sessions (
                    token_hash TEXT PRIMARY KEY,
                    user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    created_at TEXT NOT NULL, expires_at TEXT NOT NULL, seen_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_sessions_user ON sessions(user_id);
                CREATE TABLE IF NOT EXISTS connectors (
                    user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    provider TEXT NOT NULL, account_name TEXT, secret BLOB NOT NULL,
                    connected_at TEXT NOT NULL,
                    PRIMARY KEY (user_id, provider)
                );
                CREATE TABLE IF NOT EXISTS user_settings (
                    user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    key TEXT NOT NULL, value TEXT NOT NULL,
                    PRIMARY KEY (user_id, key)
                );
                CREATE TABLE IF NOT EXISTS import_places (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL, added_at TEXT NOT NULL
                );
                -- Syncthing's configuration is the whole host's. These say which account each
                -- paired computer and each synced folder belongs to, which Syncthing can't.
                CREATE TABLE IF NOT EXISTS sync_devices (
                    device_id TEXT PRIMARY KEY,
                    user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    name TEXT NOT NULL, added_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS sync_folders (
                    folder_id TEXT PRIMARY KEY,
                    user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    path TEXT NOT NULL, added_at TEXT NOT NULL
                );
                -- Short-lived codes that pair a computer without a session. Only hashes are kept.
                CREATE TABLE IF NOT EXISTS pairing_codes (
                    code_hash TEXT PRIMARY KEY,
                    user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    expires_at TEXT NOT NULL
                );
                PRAGMA user_version = 4;
                """;
            command.ExecuteNonQuery();
            // Password hashes and sealed tokens live here; nobody else on the host needs to read it.
            if (!OperatingSystem.IsWindows() && File.Exists(_path))
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
