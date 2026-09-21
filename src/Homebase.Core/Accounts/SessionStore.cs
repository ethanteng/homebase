using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Homebase.Core.Accounts;

/// <summary>
/// Live sign-ins. Only the SHA-256 of each token is stored, so a copy of the control database
/// yields no usable session — the value itself exists only in the browser that was handed it.
/// </summary>
public sealed class SessionStore(ControlDatabase database)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    /// <summary>How stale an expiry may get before it is pushed out again, so an active session
    /// doesn't write to the database on every single request.</summary>
    private static readonly TimeSpan Refresh = TimeSpan.FromDays(1);

    public AuthSession Create(string userId)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        var expires = now + Lifetime;
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(token_hash, user_id, created_at, expires_at, seen_at)
            VALUES ($hash, $user, $created, $expires, $seen)
            """;
        command.Parameters.AddWithValue("$hash", Fingerprint(token));
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        command.Parameters.AddWithValue("$seen", now.ToString("O"));
        command.ExecuteNonQuery();
        return new AuthSession(token, expires);
    }

    /// <summary>
    /// The account this token signs in as, or null. A session whose account was disabled or
    /// deleted resolves to nothing, so revoking access doesn't wait for an expiry.
    /// </summary>
    public UserAccount? Resolve(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Fingerprint(token);
        var now = DateTimeOffset.UtcNow;
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.id, u.username, u.display_name, u.is_admin, u.created_at, u.disabled_at, s.expires_at
            FROM sessions s JOIN users u ON u.id = s.user_id
            WHERE s.token_hash = $hash
            """;
        command.Parameters.AddWithValue("$hash", hash);
        UserAccount account;
        DateTimeOffset expires;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            account = new UserAccount(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0,
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)));
            expires = DateTimeOffset.Parse(reader.GetString(6));
        }
        if (now >= expires)
        {
            Delete(token);
            return null;
        }
        if (!account.IsActive) return null;
        if (expires - now < Lifetime - Refresh)
        {
            command.Parameters.Clear();
            command.CommandText = "UPDATE sessions SET expires_at = $expires, seen_at = $seen WHERE token_hash = $hash";
            command.Parameters.AddWithValue("$expires", (now + Lifetime).ToString("O"));
            command.Parameters.AddWithValue("$seen", now.ToString("O"));
            command.Parameters.AddWithValue("$hash", hash);
            command.ExecuteNonQuery();
        }
        return account;
    }

    public void Delete(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE token_hash = $hash";
        command.Parameters.AddWithValue("$hash", Fingerprint(token));
        command.ExecuteNonQuery();
    }

    /// <summary>Signs an account out everywhere. A changed password means exactly this.</summary>
    public void DeleteAllFor(string userId, string? except = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = except is null
            ? "DELETE FROM sessions WHERE user_id = $user"
            : "DELETE FROM sessions WHERE user_id = $user AND token_hash <> $keep";
        command.Parameters.AddWithValue("$user", userId);
        if (except is not null) command.Parameters.AddWithValue("$keep", Fingerprint(except));
        command.ExecuteNonQuery();
    }

    /// <summary>Clears out sessions nobody can use any more. Called on start, not per request.</summary>
    public void PruneExpired()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE expires_at <= $now";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static string Fingerprint(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
