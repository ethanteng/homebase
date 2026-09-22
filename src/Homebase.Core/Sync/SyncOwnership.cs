using Homebase.Core.Accounts;

namespace Homebase.Core.Sync;

/// <summary>
/// Which account each paired computer and each synced folder belongs to. Syncthing holds one
/// configuration for the whole host, so without this every account would see every other
/// account's computers and folders. A computer belongs to exactly one account.
/// </summary>
public sealed class SyncOwnership(ControlDatabase database)
{
    public sealed record Device(string DeviceId, string UserId, string Name);
    public sealed record Folder(string FolderId, string UserId, string Path);

    public IReadOnlyList<Device> Devices(string? userId = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = userId is null
            ? "SELECT device_id, user_id, name FROM sync_devices ORDER BY added_at"
            : "SELECT device_id, user_id, name FROM sync_devices WHERE user_id = $user ORDER BY added_at";
        if (userId is not null) command.Parameters.AddWithValue("$user", userId);
        using var reader = command.ExecuteReader();
        var devices = new List<Device>();
        while (reader.Read()) devices.Add(new Device(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return devices;
    }

    public IReadOnlyList<Folder> Folders(string? userId = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = userId is null
            ? "SELECT folder_id, user_id, path FROM sync_folders ORDER BY path"
            : "SELECT folder_id, user_id, path FROM sync_folders WHERE user_id = $user ORDER BY path";
        if (userId is not null) command.Parameters.AddWithValue("$user", userId);
        using var reader = command.ExecuteReader();
        var folders = new List<Folder>();
        while (reader.Read()) folders.Add(new Folder(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return folders;
    }

    public Device? FindDevice(string deviceId) => Devices().FirstOrDefault(device => device.DeviceId == deviceId);

    public Folder? FindFolder(string folderId) => Folders().FirstOrDefault(folder => folder.FolderId == folderId);

    /// <summary>Claims a computer for an account. False if another account already has it.</summary>
    public bool ClaimDevice(string userId, string deviceId, string name) =>
        Execute("INSERT INTO sync_devices(device_id, user_id, name, added_at) VALUES ($id, $user, $value, $at) ON CONFLICT DO NOTHING",
            deviceId, userId, name) > 0;

    public bool ClaimFolder(string userId, string folderId, string path) =>
        Execute("INSERT INTO sync_folders(folder_id, user_id, path, added_at) VALUES ($id, $user, $value, $at) ON CONFLICT DO NOTHING",
            folderId, userId, path) > 0;

    public void ReleaseDevice(string deviceId) =>
        Execute("DELETE FROM sync_devices WHERE device_id = $id", deviceId, null, null);

    public void ReleaseFolder(string folderId) =>
        Execute("DELETE FROM sync_folders WHERE folder_id = $id", folderId, null, null);

    private int Execute(string sql, string id, string? userId, string? value)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$user", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery();
    }
}
