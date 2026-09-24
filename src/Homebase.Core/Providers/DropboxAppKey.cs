using System.Collections.Concurrent;
using Homebase.Core.Accounts;

namespace Homebase.Core.Providers;

/// <summary>Where the app key an account is connecting through came from.</summary>
public enum DropboxKeySource
{
    /// <summary>Nobody has supplied one; this account can't connect Dropbox yet.</summary>
    None,
    /// <summary>The account's own, set by whoever signs in as it.</summary>
    Own,
    /// <summary>The host's, offered to everybody here by an administrator.</summary>
    Host,
    /// <summary>The host's, from <c>Homebase__Dropbox__AppKey</c> in the environment.</summary>
    Environment,
    /// <summary>Uncloud's own, which is there without anybody setting anything up.</summary>
    Relay
}

/// <summary>
/// Which Dropbox app each account connects through.
///
/// An account's own key comes first, then the host's, then the environment, and under all of them
/// Uncloud's own. That order is the whole point: connecting Dropbox works out of the box because
/// of the last one, an administrator who sets a key makes everybody here use theirs instead, and
/// anybody who would rather use their own Dropbox app sets it themselves and needs nothing from
/// anyone. Nobody's Dropbox waits on somebody else.
///
/// Uncloud's own comes last rather than first because every other entry in that list is somebody
/// having deliberately chosen a Dropbox app, and a default that quietly won over a choice would
/// be a bug. It is the floor, not the preference.
///
/// A key is not a secret. Uncloud signs in with PKCE precisely because a program on somebody's own
/// computer cannot keep one, and there is no client secret anywhere in Uncloud to leak. That is
/// what makes it safe to let a member set their own rather than reserving it to an administrator.
/// </summary>
public sealed class DropboxAppKey(ControlDatabase database, string? fromEnvironment, DropboxRelay relay)
{
    private const string SettingKey = "dropbox_app_key";
    private readonly string? _environment = Blank(fromEnvironment) ? null : fromEnvironment!.Trim();
    private readonly Lock _lock = new();
    // Read on every status check, so held rather than fetched each time. A key set anywhere else
    // than through this class would not be seen, and there is nowhere else to set one.
    private readonly ConcurrentDictionary<string, string?> _mine = new(StringComparer.Ordinal);
    private string? _stored;
    private bool _read;

    /// <summary>What an administrator set for the whole host, as opposed to the environment's.</summary>
    public string? HostStored
    {
        get
        {
            lock (_lock)
            {
                if (_read) return _stored;
                _stored = ReadHost();
                _read = true;
                return _stored;
            }
        }
    }

    /// <summary>The host's key in force, whichever way it arrived, or null when there is none.</summary>
    public string? Host => HostStored ?? _environment;

    /// <summary>
    /// Whether the host's key came from the environment. Worth saying on screen: it explains why
    /// there is a key in force with nothing typed in the box, and where to change it.
    /// </summary>
    public bool HostFromEnvironment => HostStored is null && _environment is not null;

    /// <summary>This account's own key, which is theirs alone and overrides the host's.</summary>
    public string? OwnedBy(string userId) =>
        _mine.GetOrAdd(userId, id => ReadOwn(id));

    /// <summary>The key this account actually connects through.</summary>
    public string? For(string userId) => OwnedBy(userId) ?? Host ?? relay.AppKey;

    /// <summary>Where that key came from, so the screen can say whose it is.</summary>
    public DropboxKeySource SourceFor(string userId) =>
        OwnedBy(userId) is not null ? DropboxKeySource.Own
        : HostStored is not null ? DropboxKeySource.Host
        : _environment is not null ? DropboxKeySource.Environment
        : relay.Available ? DropboxKeySource.Relay
        : DropboxKeySource.None;

    /// <summary>
    /// Whether this account is connecting through Uncloud's own app rather than one somebody here
    /// chose — which decides where Dropbox sends the sign-in back to, and so has to be asked before
    /// a sign-in starts as well as when it comes back.
    /// </summary>
    public bool UsesRelay(string userId) => SourceFor(userId) == DropboxKeySource.Relay;

    /// <summary>
    /// Records the host's key, or clears it when given nothing. Returns whether the key in force
    /// changed, because every connection made through the old app stops working the moment it does
    /// — the caller has to clear those rather than leave people with a connection that fails on its
    /// next refresh for no visible reason.
    /// </summary>
    public bool SetHost(string? key)
    {
        var trimmed = Trimmed(key);
        lock (_lock)
        {
            var before = Host;
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

    /// <summary>
    /// Records one account's own key, or clears it when given nothing. Returns whether the key that
    /// account connects through changed — clearing an own key falls back to the host's, which may
    /// well be the same one, and signing somebody out for no change is its own small unkindness.
    /// </summary>
    public bool SetOwn(string userId, string? key)
    {
        var trimmed = Trimmed(key);
        var before = For(userId);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        if (trimmed is null)
        {
            command.CommandText = "DELETE FROM user_settings WHERE user_id = $user AND key = $key";
            command.Parameters.AddWithValue("$user", userId);
            command.Parameters.AddWithValue("$key", SettingKey);
        }
        else
        {
            command.CommandText =
                "INSERT OR REPLACE INTO user_settings(user_id, key, value) VALUES ($user, $key, $value)";
            command.Parameters.AddWithValue("$user", userId);
            command.Parameters.AddWithValue("$key", SettingKey);
            command.Parameters.AddWithValue("$value", trimmed);
        }
        command.ExecuteNonQuery();
        _mine[userId] = trimmed;
        return For(userId) != before;
    }

    /// <summary>
    /// Whether this account connects through the host's key rather than one of its own — which is
    /// to say, whether a change to the host's key is any of its business.
    /// </summary>
    public bool UsesHostKey(string userId) => OwnedBy(userId) is null;

    /// <summary>Drops what is held for an account, after it is deleted.</summary>
    public void Forget(string userId) => _mine.TryRemove(userId, out _);

    private string? ReadHost()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM host_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", SettingKey);
        return command.ExecuteScalar() as string;
    }

    private string? ReadOwn(string userId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM user_settings WHERE user_id = $user AND key = $key";
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$key", SettingKey);
        return command.ExecuteScalar() as string;
    }

    private static string? Trimmed(string? key) => Blank(key) ? null : key!.Trim();
    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
