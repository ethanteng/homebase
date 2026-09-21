using Microsoft.Data.Sqlite;

namespace Homebase.Core;

// A disposable cache of observed filesystem metadata, never the source of file contents.
public sealed class MetadataIndex
{
    // Shared with the provider sync store so both use one database and one set of path checks.
    internal static SqliteConnection Open(string root)
    {
        var path = PathPolicy.PrepareMetadata(root);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS entries (
                    path TEXT PRIMARY KEY, parent_path TEXT NOT NULL, name TEXT NOT NULL,
                    is_directory INTEGER NOT NULL, size INTEGER, modified_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_entries_parent ON entries(parent_path);
                CREATE TABLE IF NOT EXISTS directories (
                    path TEXT PRIMARY KEY, indexed_at TEXT NOT NULL, skipped_count INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS synced_files (
                    provider TEXT NOT NULL, remote_path TEXT NOT NULL, remote_rev TEXT NOT NULL,
                    local_path TEXT NOT NULL, size INTEGER NOT NULL,
                    local_size INTEGER NOT NULL, local_modified_at TEXT NOT NULL,
                    local_hash TEXT NOT NULL DEFAULT '', synced_at TEXT NOT NULL,
                    PRIMARY KEY (provider, remote_path)
                );
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
            AddMissingColumn(connection, "synced_files", "local_hash", "TEXT NOT NULL DEFAULT ''");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // CREATE TABLE IF NOT EXISTS leaves an older table alone, so new columns are added explicitly.
    private static void AddMissingColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var existing = connection.CreateCommand();
        existing.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column";
        existing.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt64(existing.ExecuteScalar()) > 0) return;
        using var add = connection.CreateCommand();
        add.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        add.ExecuteNonQuery();
    }

    public void Initialize(string root)
    {
        using var connection = Open(root);
    }

    public void ReplaceDirectory(string root, DirectoryListing listing, CancellationToken cancellationToken)
    {
        using var connection = Open(root);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM entries WHERE parent_path = $parent";
        command.Parameters.AddWithValue("$parent", listing.Path);
        command.ExecuteNonQuery();

        command.CommandText = """
            INSERT INTO entries(path, parent_path, name, is_directory, size, modified_at)
            VALUES ($path, $parent, $name, $directory, $size, $modified)
            ON CONFLICT(path) DO UPDATE SET parent_path = excluded.parent_path, name = excluded.name,
                is_directory = excluded.is_directory, size = excluded.size, modified_at = excluded.modified_at
            """;
        command.Parameters.Add("$path", SqliteType.Text);
        command.Parameters.Add("$name", SqliteType.Text);
        command.Parameters.Add("$directory", SqliteType.Integer);
        command.Parameters.Add("$size", SqliteType.Integer);
        command.Parameters.Add("$modified", SqliteType.Text);
        command.Prepare();
        foreach (var entry in listing.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.Parameters["$path"].Value = entry.Path;
            command.Parameters["$name"].Value = entry.Name;
            command.Parameters["$directory"].Value = entry.IsDirectory ? 1 : 0;
            command.Parameters["$size"].Value = (object?)entry.Size ?? DBNull.Value;
            command.Parameters["$modified"].Value = entry.ModifiedAt.ToString("O");
            command.ExecuteNonQuery();
        }
        command.Parameters.Clear();
        command.CommandText = "INSERT OR REPLACE INTO directories(path, indexed_at, skipped_count) VALUES ($path, $time, $skipped)";
        command.Parameters.AddWithValue("$path", listing.Path);
        command.Parameters.AddWithValue("$time", listing.IndexedAt.ToString("O"));
        command.Parameters.AddWithValue("$skipped", listing.SkippedCount);
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
