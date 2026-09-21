using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Homebase.Core.Accounts;

/// <summary>The accounts on this host, and the only place a password is ever checked.</summary>
public sealed partial class UserStore(ControlDatabase database)
{
    // Deliberately narrow: a username is typed at a sign-in prompt, not a display name.
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,31}$")]
    private static partial Regex UsernamePattern { get; }

    /// <summary>
    /// Verified against when no such account exists, so that a wrong username and a wrong
    /// password cost the same time and the sign-in form can't be used to enumerate accounts.
    /// </summary>
    private static readonly string Decoy = PasswordHasher.Hash(Guid.NewGuid().ToString("N"));

    public int Count()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IReadOnlyList<UserAccount> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"{Columns} ORDER BY username";
        using var reader = command.ExecuteReader();
        var users = new List<UserAccount>();
        while (reader.Read()) users.Add(Read(reader));
        return users;
    }

    public UserAccount? Find(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"{Columns} WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public UserAccount Create(string? username, string? displayName, string password, bool isAdmin)
    {
        var name = Normalize(username);
        PasswordHasher.Require(password);
        using var connection = database.Open();
        return Insert(connection, null, name, displayName, password, isAdmin);
    }

    /// <summary>
    /// The first account on this host, which is an administrator. The count and the insert are one
    /// write transaction because setup is open to anybody until it succeeds: two people racing a
    /// fresh host would otherwise both see no accounts and both end up administrators, leaving one
    /// of them holding an account the other never meant to create. In practice the race is hard to
    /// reach at the moment, because opening a connection runs the schema DDL and so takes the write
    /// lock — but that is an accident of how <see cref="ControlDatabase.Open"/> happens to work,
    /// not a guarantee, and it would vanish the moment opening were made cheaper.
    /// </summary>
    public UserAccount CreateFirstAdmin(string? username, string? displayName, string password)
    {
        var name = Normalize(username);
        PasswordHasher.Require(password);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM users";
            if (Convert.ToInt32(count.ExecuteScalar()) != 0)
                throw new LibraryException("This Uncloud already has accounts. Sign in instead.", "conflict");
        }
        var account = Insert(connection, transaction, name, displayName, password, isAdmin: true);
        transaction.Commit();
        return account;
    }

    private static UserAccount Insert(
        SqliteConnection connection, SqliteTransaction? transaction,
        string name, string? displayName, string password, bool isAdmin)
    {
        var account = new UserAccount(Guid.NewGuid().ToString("N"), name,
            string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim(), isAdmin,
            DateTimeOffset.UtcNow, null);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO users(id, username, display_name, password_hash, is_admin, created_at, disabled_at)
            VALUES ($id, $username, $display, $hash, $admin, $created, NULL)
            """;
        command.Parameters.AddWithValue("$id", account.Id);
        command.Parameters.AddWithValue("$username", account.Username);
        command.Parameters.AddWithValue("$display", account.DisplayName);
        command.Parameters.AddWithValue("$hash", PasswordHasher.Hash(password));
        command.Parameters.AddWithValue("$admin", isAdmin ? 1 : 0);
        command.Parameters.AddWithValue("$created", account.CreatedAt.ToString("O"));
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new LibraryException($"Somebody on this Uncloud is already called “{name}”.", "conflict");
        }
        return account;
    }

    /// <summary>The account these credentials belong to, or null. Disabled accounts never match.</summary>
    public UserAccount? Authenticate(string? username, string? password)
    {
        var name = (username ?? "").Trim().ToLowerInvariant();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, username, display_name, is_admin, created_at, disabled_at, password_hash
            FROM users WHERE username = $username
            """;
        command.Parameters.AddWithValue("$username", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            // Still pay for a hash, so a missing account and a wrong password are indistinguishable.
            PasswordHasher.Verify(password ?? "", Decoy);
            return null;
        }
        var account = Read(reader);
        var matched = PasswordHasher.Verify(password ?? "", reader.GetString(6));
        return matched && account.IsActive ? account : null;
    }

    public void SetPassword(string id, string password)
    {
        PasswordHasher.Require(password);
        using var connection = database.Open();
        Execute(connection, null, "UPDATE users SET password_hash = $value WHERE id = $id",
            id, PasswordHasher.Hash(password));
    }

    public void SetDisplayName(string id, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new LibraryException("A name can't be blank.");
        using var connection = database.Open();
        Execute(connection, null, "UPDATE users SET display_name = $value WHERE id = $id", id, displayName.Trim());
    }

    public void SetDisabled(string id, bool disabled) => Guarded(id, disabled ? "disable" : null,
        "UPDATE users SET disabled_at = $value WHERE id = $id",
        disabled ? DateTimeOffset.UtcNow.ToString("O") : null);

    public void SetAdmin(string id, bool isAdmin) => Guarded(id, isAdmin ? null : "step down from",
        "UPDATE users SET is_admin = $value WHERE id = $id", isAdmin ? 1 : 0);

    public void Delete(string id) => Guarded(id, "delete", "DELETE FROM users WHERE id = $id", null);

    /// <summary>
    /// A change that could cost this host its last administrator, made as one write transaction
    /// with the check that says it may not. Checking on one connection and writing on another
    /// lets two administrators demote each other at once and leave nobody holding the keys.
    /// </summary>
    private void Guarded(string id, string? verb, string sql, object? value)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (verb is not null) RequireAnotherAdmin(connection, transaction, id, verb);
        Execute(connection, transaction, sql, id, value);
        transaction.Commit();
    }

    /// <summary>
    /// Refuses to leave the host with no way in. Losing the last administrator would mean nobody
    /// could choose a folder or add an account again, short of editing the database by hand.
    /// </summary>
    private static void RequireAnotherAdmin(
        SqliteConnection connection, SqliteTransaction transaction, string id, string verb)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM users
            WHERE is_admin = 1 AND disabled_at IS NULL AND id <> $id
            """;
        command.Parameters.AddWithValue("$id", id);
        if (Convert.ToInt32(command.ExecuteScalar()) == 0)
            throw new LibraryException(
                $"This is the only administrator left, so Uncloud won't {verb} it. Make somebody else an administrator first.",
                "conflict");
    }

    private static void Execute(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, string id, object? value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        if (sql.Contains("$value", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$value", value ?? DBNull.Value);
        if (command.ExecuteNonQuery() == 0) throw new LibraryException("There's no such account.", "not_found");
    }

    private static string Normalize(string? username)
    {
        var name = (username ?? "").Trim().ToLowerInvariant();
        if (!UsernamePattern.IsMatch(name))
            throw new LibraryException(
                "A username is 2 to 32 characters: letters, numbers, dots, dashes and underscores, starting with a letter or number.");
        return name;
    }

    private const string Columns =
        "SELECT id, username, display_name, is_admin, created_at, disabled_at FROM users";

    private static UserAccount Read(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0,
        DateTimeOffset.Parse(reader.GetString(4)),
        reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)));
}
