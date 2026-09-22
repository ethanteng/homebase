using Homebase.Core.Providers;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Accounts;

/// <summary>
/// The isolation boundary, and the only way to reach anybody's files. A handler asks for the
/// workspace of the account the request was authenticated as; it cannot ask for any other, and
/// it cannot name a folder. Everything downstream — path resolution, imports, provider tokens —
/// then works from a root this class chose.
/// </summary>
public sealed class UserWorkspaces(
    HostService host,
    MetadataIndex index,
    ImportLog log,
    IDropboxApiFactory dropbox,
    ImportPlaces places,
    ILoggerFactory loggers)
{
    private readonly Dictionary<string, UserWorkspace> _workspaces = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private string? _builtFor;

    public UserWorkspace For(UserAccount account) => For(account.Id);

    public UserWorkspace For(string userId)
    {
        var root = host.RequireRoot();
        // Reading the host's folder and settling which workspace belongs to it happen together,
        // so a folder changed mid-request can't leave a workspace pointing at the old one. The
        // lock is only contended on an account's first request or a change of folder; making a
        // workspace opens a SQLite file, which is why it is not held for every request.
        lock (_lock)
        {
            // Moving the host's folder invalidates every root at once. The workspaces built
            // against the old one are dropped rather than disposed: a request already inside one
            // has to be able to finish on it, and a running import finishes where it started.
            if (_builtFor != root)
            {
                foreach (var key in _workspaces.Keys) dropbox.Forget(key);
                _workspaces.Clear();
                _builtFor = root;
            }
            if (_workspaces.TryGetValue(userId, out var existing)) return existing;
            var workspace = new UserWorkspace(
                userId, UserPaths.RootFor(root, userId), index, log, dropbox.For(userId), places, loggers);
            _workspaces[userId] = workspace;
            return workspace;
        }
    }

    /// <summary>
    /// Drops one account's workspace, after it is disabled or deleted. Any import it had running
    /// is stopped first: the background work holds its own workspace and a cached access token,
    /// so without this it would carry on downloading into a folder whose owner has just had their
    /// access taken away. Whatever already arrived stays, as it does for any stopped import.
    /// </summary>
    public void Forget(string userId)
    {
        UserWorkspace? workspace;
        lock (_lock)
        {
            _workspaces.Remove(userId, out workspace);
        }
        workspace?.Jobs.Cancel();
        dropbox.Forget(userId);
    }

    /// <summary>
    /// Drops every account's workspace, after the host's Dropbox app key changes. Forgetting the
    /// cached clients at the factory is not enough on its own: a workspace already built holds the
    /// client it was made with, and that client answers from a cached access token without going
    /// back to the refresh token that has just been deleted. Until that token expired, an account
    /// whose connection was supposedly signed out could carry on reading Dropbox.
    ///
    /// So each held client is disconnected, which is what clears its cached token, and any import
    /// running on it is stopped — the same treatment an account gets when it is disabled.
    ///
    /// Only the accounts <paramref name="affected"/> picks out: somebody connecting through their
    /// own app key is untouched by the host's changing, and stopping their import would be exactly
    /// the dependence on an administrator that having their own key is meant to remove.
    /// </summary>
    public void ForgetAll(Func<string, bool> affected)
    {
        UserWorkspace[] workspaces;
        lock (_lock)
        {
            workspaces = _workspaces.Where(entry => affected(entry.Key)).Select(entry => entry.Value).ToArray();
            foreach (var workspace in workspaces) _workspaces.Remove(workspace.UserId);
        }
        foreach (var workspace in workspaces)
        {
            workspace.Jobs.Cancel();
            workspace.Dropbox.Disconnect();
            dropbox.Forget(workspace.UserId);
        }
    }

    /// <summary>
    /// The same, for one account, after that account changes the Dropbox app key it connects
    /// through. Their connection was authorised against the old app and cannot be refreshed against
    /// the new one, so it has to go — and only theirs, because nobody else's key moved.
    /// </summary>
    public void ForgetDropbox(string userId)
    {
        UserWorkspace? workspace;
        lock (_lock)
        {
            _workspaces.Remove(userId, out workspace);
        }
        workspace?.Jobs.Cancel();
        workspace?.Dropbox.Disconnect();
        dropbox.Forget(userId);
    }
}
