using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Homebase.Core.Accounts;
using Microsoft.Data.Sqlite;

namespace Homebase.Core.Sharing;

/// <summary>
/// A link somebody made to files and folders of theirs, which anyone who has it can open without an
/// account: to look at them, and to download them too if <paramref name="CanDownload"/>.
/// </summary>
/// <param name="Token">
/// What the link's address ends in, or null when it can't be read back any more (host.key replaced).
/// The link still works then; its owner just can't copy it again.
/// </param>
/// <param name="Paths">What it shares, inside its owner's folder: one thing, or several picked together.</param>
public sealed record ShareLink(
    string Id, string UserId, string? Token, IReadOnlyList<string> Paths, bool CanDownload, DateTimeOffset CreatedAt)
{
    /// <summary>
    /// What it shares under the names its visitors see, which are the names of the things themselves.
    /// Two from different folders can come to share a name, after one is moved; the later one is
    /// numbered, so each name still leads to one thing.
    /// </summary>
    public IReadOnlyList<SharedItem> Items
    {
        get
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<SharedItem>();
            foreach (var path in Paths)
            {
                var name = path[(path.LastIndexOf('/') + 1)..];
                var candidate = name;
                for (var number = 2; !taken.Add(candidate); number++) candidate = $"{name} ({number})";
                items.Add(new SharedItem(candidate, path));
            }
            return items;
        }
    }

    /// <summary>
    /// Where something a visitor asks for is in the owner's folder, or null for the top of the link,
    /// which is a list of what it shares. A visitor's path starts with the name of one of those and can
    /// only go down from there: whatever else it names is checked again by the library's own path
    /// policy, which refuses <c>..</c>, hidden names and links, so nothing beside or above what was
    /// shared can be reached.
    /// </summary>
    public string? Locate(string? publicPath)
    {
        var path = (publicPath ?? "").Trim('/');
        if (path.Length == 0) return null;
        if (path.Contains('\\') || path.Contains('\0')) throw Missing();
        var slash = path.IndexOf('/');
        var first = slash < 0 ? path : path[..slash];
        var item = Items.FirstOrDefault(item => item.Name == first) ?? throw Missing();
        return slash < 0 ? item.Path : $"{item.Path}/{path[(slash + 1)..]}";
    }

    private static LibraryException Missing() =>
        new("That isn’t in what was shared.", "not_found");
}

/// <summary>One thing a link shares: the name its visitors see, and where it is in its owner's folder.</summary>
public sealed record SharedItem(string Name, string Path);

/// <summary>
/// Everybody's links. Each belongs to the account that made it, and only that account can see it,
/// change who may download, or turn it off; nothing here takes an owner from anywhere but a session.
/// A link is found by the hash of its token, as a session is, so its address is the whole of the key.
/// </summary>
public sealed partial class ShareLinks(ControlDatabase database, SecretProtector secrets)
{
    /// <summary>How many things one link can hold: a folder's worth of files ticked at once, not more.</summary>
    public const int MaxItems = 1000;

    // 32 random bytes, as base64url: 43 characters. Anything else is no link of ours.
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex TokenPattern { get; }

    /// <summary>
    /// A link to these, which have to be in the owner's folder already (the library checks that). One
    /// to exactly the same things is the same link, made to allow downloading or not as asked now, so
    /// sharing something twice doesn't leave two addresses to keep track of and turn off.
    /// </summary>
    public ShareLink Create(string userId, IReadOnlyList<string> paths, bool canDownload)
    {
        if (paths.Count == 0) throw new LibraryException("Choose at least one file or folder to share.");
        if (paths.Count > MaxItems)
            throw new LibraryException($"A link can share up to {MaxItems:N0} things. Share the folder they’re in instead.");
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var link in Read(connection, transaction, userId, id: null))
            if (link.Paths.Count == paths.Count && link.Paths.ToHashSet(StringComparer.Ordinal).SetEquals(paths))
            {
                if (link.CanDownload != canDownload) SetAccess(connection, transaction, link.Id, canDownload);
                transaction.Commit();
                return link with { CanDownload = canDownload };
            }
        var id = Guid.NewGuid().ToString("N");
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO share_links(id, token_hash, token, user_id, paths, can_download, created_at)
            VALUES ($id, $hash, $token, $user, $paths, $download, $created)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$hash", Fingerprint(token));
        command.Parameters.AddWithValue("$token", secrets.Seal(token, Context(userId, id)));
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(paths));
        command.Parameters.AddWithValue("$download", canDownload ? 1 : 0);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
        return new ShareLink(id, userId, token, paths, canDownload, now);
    }

    /// <summary>This account's links, newest first.</summary>
    public IReadOnlyList<ShareLink> List(string userId)
    {
        using var connection = database.Open();
        return Read(connection, null, userId, id: null).OrderByDescending(link => link.CreatedAt).ToArray();
    }

    /// <summary>The link this token is, or null. Whose it is, and whether they may still share, is the caller's to check.</summary>
    public ShareLink? Find(string? token)
    {
        if (token is null || !TokenPattern.IsMatch(token)) return null;
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"{Columns} WHERE token_hash = $hash";
        command.Parameters.AddWithValue("$hash", Fingerprint(token));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Row(reader) with { Token = token } : null;
    }

    /// <summary>Lets the people with this link download, or only look.</summary>
    public ShareLink SetAccess(string userId, string id, bool canDownload)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var link = Read(connection, transaction, userId, id).SingleOrDefault() ?? throw NoSuchLink();
        SetAccess(connection, transaction, id, canDownload);
        transaction.Commit();
        return link with { CanDownload = canDownload };
    }

    /// <summary>Turns a link off: its address stops working at once, and for good.</summary>
    public void Remove(string userId, string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM share_links WHERE id = $id AND user_id = $user";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$user", userId);
        if (command.ExecuteNonQuery() == 0) throw NoSuchLink();
    }

    /// <summary>
    /// Things moved or renamed in this account's folder, from where they were to where they are now.
    /// A link to them, or to something inside them, goes with them, so moving a shared folder into
    /// another doesn't break the address somebody was sent.
    /// </summary>
    public void Follow(string userId, IEnumerable<EditedEntry> moved)
    {
        var moves = moved.Where(move => move.From != move.To).ToArray();
        if (moves.Length == 0) return;
        Rewrite(userId, path =>
        {
            foreach (var move in moves)
            {
                if (path == move.From) return [move.To];
                if (path.StartsWith(move.From + "/", StringComparison.Ordinal))
                    return [move.To + path[move.From.Length..]];
            }
            return [path];
        });
    }

    /// <summary>
    /// Things deleted from this account's folder. Nothing in them is shared any more: a link to them
    /// alone is turned off, and one to them and other things carries on with the others. Putting them
    /// back from the Trash doesn't share them again — that's for their owner to choose.
    /// </summary>
    public void Forget(string userId, IEnumerable<string> deleted)
    {
        var gone = deleted.Select(path => path.Trim('/')).Where(path => path.Length > 0).ToArray();
        if (gone.Length == 0) return;
        Rewrite(userId, path => gone.Any(item => path == item || path.StartsWith(item + "/", StringComparison.Ordinal))
            ? []
            : [path]);
    }

    /// <summary>Every link of this account's, each of its paths changed as <paramref name="change"/> says, in one go.</summary>
    private void Rewrite(string userId, Func<string, string[]> change)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var link in Read(connection, transaction, userId, id: null))
        {
            var paths = link.Paths.SelectMany(change).Distinct(StringComparer.Ordinal).ToArray();
            if (paths.SequenceEqual(link.Paths, StringComparer.Ordinal)) continue;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (paths.Length == 0)
                command.CommandText = "DELETE FROM share_links WHERE id = $id";
            else
            {
                command.CommandText = "UPDATE share_links SET paths = $paths WHERE id = $id";
                command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(paths));
            }
            command.Parameters.AddWithValue("$id", link.Id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private const string Columns = "SELECT id, user_id, token, paths, can_download, created_at FROM share_links";

    private IEnumerable<ShareLink> Read(SqliteConnection connection, SqliteTransaction? transaction, string userId, string? id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = id is null ? $"{Columns} WHERE user_id = $user" : $"{Columns} WHERE user_id = $user AND id = $id";
        command.Parameters.AddWithValue("$user", userId);
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var links = new List<ShareLink>();
        while (reader.Read())
        {
            var link = Row(reader);
            // Sealed against its owner and its own id, so a token can't be moved onto another link.
            links.Add(link with { Token = secrets.Open((byte[])reader["token"], Context(link.UserId, link.Id)) });
        }
        return links;
    }

    private static ShareLink Row(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        null,
        JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [],
        reader.GetInt64(4) != 0,
        DateTimeOffset.Parse(reader.GetString(5)));

    private static void SetAccess(SqliteConnection connection, SqliteTransaction transaction, string id, bool canDownload)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE share_links SET can_download = $download WHERE id = $id";
        command.Parameters.AddWithValue("$download", canDownload ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static LibraryException NoSuchLink() =>
        new("There’s no such link. It may have been turned off already.", "not_found");

    private static string Context(string userId, string id) => $"share-link:{userId}:{id}";

    private static string Fingerprint(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
