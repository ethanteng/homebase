using Microsoft.Data.Sqlite;

namespace Homebase.Core.Providers;

/// <summary>Records which provider files Homebase mirrors, in the library's own database.</summary>
public sealed class SyncedFileStore
{
    public IReadOnlyList<SyncedFile> List(string root)
    {
        using var connection = MetadataIndex.Open(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider, remote_path, remote_rev, local_path, size, local_size, local_modified_at, synced_at
            FROM synced_files ORDER BY local_path
            """;
        using var reader = command.ExecuteReader();
        var files = new List<SyncedFile>();
        while (reader.Read())
            files.Add(new SyncedFile(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetInt64(5),
                DateTimeOffset.Parse(reader.GetString(6)), DateTimeOffset.Parse(reader.GetString(7))));
        return files;
    }

    public SyncedFile? Find(string root, string provider, string remotePath) =>
        List(root).FirstOrDefault(file =>
            file.Provider == provider && file.RemotePath.Equals(remotePath, StringComparison.OrdinalIgnoreCase));

    public void Save(string root, SyncedFile file)
    {
        using var connection = MetadataIndex.Open(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO synced_files(provider, remote_path, remote_rev, local_path, size, local_size, local_modified_at, synced_at)
            VALUES ($provider, $remote, $rev, $local, $size, $localSize, $localModified, $synced)
            ON CONFLICT(provider, remote_path) DO UPDATE SET
                remote_rev = excluded.remote_rev, local_path = excluded.local_path, size = excluded.size,
                local_size = excluded.local_size, local_modified_at = excluded.local_modified_at, synced_at = excluded.synced_at
            """;
        command.Parameters.AddWithValue("$provider", file.Provider);
        command.Parameters.AddWithValue("$remote", file.RemotePath);
        command.Parameters.AddWithValue("$rev", file.RemoteRev);
        command.Parameters.AddWithValue("$local", file.LocalPath);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$localSize", file.LocalSize);
        command.Parameters.AddWithValue("$localModified", file.LocalModifiedAt.ToString("O"));
        command.Parameters.AddWithValue("$synced", file.SyncedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Remove(string root, string provider, string remotePath)
    {
        using var connection = MetadataIndex.Open(root);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM synced_files WHERE provider = $provider AND remote_path = $remote";
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$remote", remotePath);
        command.ExecuteNonQuery();
    }
}
