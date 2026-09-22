namespace Homebase.Core.Accounts;

/// <summary>
/// Each account's own provider connections. A refresh token reaches somebody's whole Dropbox,
/// so it is sealed under the host key and bound to the user and provider it was stored for.
/// </summary>
public sealed class ConnectorStore(ControlDatabase database, SecretProtector protector)
{
    public string? LoadSecret(string userId, string provider)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT secret FROM connectors WHERE user_id = $user AND provider = $provider";
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$provider", provider);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return protector.Open((byte[])reader["secret"], Context(userId, provider));
    }

    public string? LoadAccountName(string userId, string provider)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_name FROM connectors WHERE user_id = $user AND provider = $provider";
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$provider", provider);
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) ? reader.GetString(0) : null;
    }

    public void Save(string userId, string provider, string secret, string? accountName)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO connectors(user_id, provider, account_name, secret, connected_at)
            VALUES ($user, $provider, $name, $secret, $connected)
            ON CONFLICT(user_id, provider) DO UPDATE SET
                account_name = excluded.account_name, secret = excluded.secret,
                connected_at = excluded.connected_at
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$name", (object?)accountName ?? DBNull.Value);
        command.Parameters.AddWithValue("$secret", protector.Seal(secret, Context(userId, provider)));
        command.Parameters.AddWithValue("$connected", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Clear(string userId, string provider)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM connectors WHERE user_id = $user AND provider = $provider";
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$provider", provider);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Drops the connections to one provider held by the accounts <paramref name="affected"/> picks
    /// out. Used when the host's app key changes: tokens issued to the old app cannot be refreshed
    /// against the new one, so keeping them would only fail later, somewhere nobody is looking.
    ///
    /// Not every account is affected, which is why this takes a predicate rather than clearing the
    /// table: somebody connecting through their own app key is untouched by the host's changing,
    /// and signing them out would be exactly the dependence on an administrator that having their
    /// own key is meant to remove.
    /// </summary>
    public int ClearAll(string provider, Func<string, bool> affected)
    {
        var holders = new List<string>();
        using var connection = database.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT user_id FROM connectors WHERE provider = $provider";
            command.Parameters.AddWithValue("$provider", provider);
            using var reader = command.ExecuteReader();
            while (reader.Read()) holders.Add(reader.GetString(0));
        }
        var cleared = 0;
        foreach (var userId in holders.Where(affected))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM connectors WHERE user_id = $user AND provider = $provider";
            command.Parameters.AddWithValue("$user", userId);
            command.Parameters.AddWithValue("$provider", provider);
            cleared += command.ExecuteNonQuery();
        }
        return cleared;
    }

    /// <summary>
    /// Authenticated alongside the token but never encrypted. Moving a row between accounts
    /// therefore breaks the seal rather than handing the new owner somebody else's Dropbox.
    /// </summary>
    private static string Context(string userId, string provider) => $"{userId}:{provider}";
}
