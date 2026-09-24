using Microsoft.Data.Sqlite;

namespace Homebase.Core.Providers;

/// <summary>Records what has been imported, in the library's own database.</summary>
public sealed class ImportLog
{
    public IReadOnlyList<ImportedFile> List(string root)
    {
        using var connection = MetadataIndex.Open(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider, remote_path, remote_rev, local_path, size, content_hash, imported_at
            FROM imported_files ORDER BY imported_at DESC, local_path
            """;
        using var reader = command.ExecuteReader();
        var files = new List<ImportedFile>();
        while (reader.Read())
            files.Add(new ImportedFile(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6))));
        return files;
    }

    public bool Contains(string root, string provider, string remotePath)
    {
        using var connection = MetadataIndex.Open(root);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM imported_files WHERE provider = $provider AND remote_path = $remote";
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$remote", remotePath);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Where in the library imports have already landed, whichever place they came from. Two
    /// sources can share a destination on purpose — a Dropbox folder synced onto this computer and
    /// the same account online both belong in Dropbox/ — and a file the other one already
    /// brought home is not a conflict to report, it is the file being here, which is the point.
    /// </summary>
    public HashSet<string> LocalPaths(string root)
    {
        using var connection = MetadataIndex.Open(root);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT local_path FROM imported_files";
        using var reader = command.ExecuteReader();
        // The log's own comparison, which SQLite makes case-sensitive.
        var paths = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }

    public void Record(string root, ImportedFile file)
    {
        using var connection = MetadataIndex.Open(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO imported_files(provider, remote_path, remote_rev, local_path, size, content_hash, imported_at)
            VALUES ($provider, $remote, $rev, $local, $size, $hash, $imported)
            ON CONFLICT(provider, remote_path) DO UPDATE SET
                remote_rev = excluded.remote_rev, local_path = excluded.local_path, size = excluded.size,
                content_hash = excluded.content_hash, imported_at = excluded.imported_at
            """;
        command.Parameters.AddWithValue("$provider", file.Provider);
        command.Parameters.AddWithValue("$remote", file.RemotePath);
        command.Parameters.AddWithValue("$rev", file.RemoteRev);
        command.Parameters.AddWithValue("$local", file.LocalPath);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$hash", file.ContentHash);
        command.Parameters.AddWithValue("$imported", file.ImportedAt.ToString("O"));
        command.ExecuteNonQuery();
    }
}
