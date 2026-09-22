using Homebase.Core.Accounts;

namespace Homebase.Core.Providers;

/// <summary>
/// The host's Dropbox app key: which Dropbox app the whole household's connections are made
/// through. An administrator sets it in Uncloud, so nobody has to restart the app from a terminal
/// to change it, and it is kept beside the host's other settings rather than in the environment.
///
/// It is not a secret. Uncloud signs in with PKCE precisely because a program on somebody's own
/// computer cannot keep one, and there is no client secret anywhere in Uncloud to leak.
/// <c>Homebase__Dropbox__AppKey</c> still works and is used when nothing has been set here, so a
/// host that was started with the environment variable keeps working untouched.
/// </summary>
public sealed class DropboxAppKey(ControlDatabase database, string? fromEnvironment)
{
    private const string SettingKey = "dropbox_app_key";
    private readonly string? _environment = Blank(fromEnvironment) ? null : fromEnvironment!.Trim();
    private readonly Lock _lock = new();
    private string? _stored;
    private bool _read;

    /// <summary>The key in force, whichever way it arrived.</summary>
    public string? Current => Stored ?? _environment;

    /// <summary>What an administrator set here, as opposed to what the environment supplied.</summary>
    public string? Stored
    {
        get
        {
            lock (_lock)
            {
                if (_read) return _stored;
                _stored = Read();
                _read = true;
                return _stored;
            }
        }
    }

    /// <summary>
    /// Whether the key in force came from the environment. Worth saying on screen: it explains why
    /// there is a key with nothing typed in the box, and where to change it.
    /// </summary>
    public bool FromEnvironment => Stored is null && _environment is not null;

    /// <summary>
    /// Records a new key, or clears it when given nothing. Returns whether the key in force actually
    /// changed, because every existing Dropbox connection was authorised through the old app and
    /// stops working the moment it does — the caller has to clear those rather than leave people
    /// with a connection that fails on its next refresh for no visible reason.
    /// </summary>
    public bool Set(string? key)
    {
        var trimmed = Blank(key) ? null : key!.Trim();
        lock (_lock)
        {
            var before = Current;
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            if (trimmed is null)
            {
                command.CommandText = "DELETE FROM host_settings WHERE key = $key";
                command.Parameters.AddWithValue("$key", SettingKey);
            }
            else
            {
                command.CommandText = "INSERT OR REPLACE INTO host_settings(key, value) VALUES ($key, $value)";
                command.Parameters.AddWithValue("$key", SettingKey);
                command.Parameters.AddWithValue("$value", trimmed);
            }
            command.ExecuteNonQuery();
            _stored = trimmed;
            _read = true;
            return (trimmed ?? _environment) != before;
        }
    }

    private string? Read()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM host_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", SettingKey);
        return command.ExecuteScalar() as string;
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
