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
                userId, UserPaths.RootFor(root, userId), index, log, dropbox.For(userId), loggers);
            _workspaces[userId] = workspace;
            return workspace;
        }
    }

    /// <summary>Drops one account's workspace, after it is disabled or deleted.</summary>
    public void Forget(string userId)
    {
        lock (_lock) _workspaces.Remove(userId);
        dropbox.Forget(userId);
    }
}
