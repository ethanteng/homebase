using System.Collections.Concurrent;
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
    ILoggerFactory loggers) : IDisposable
{
    private readonly ConcurrentDictionary<string, UserWorkspace> _workspaces = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private string? _builtFor;

    public UserWorkspace For(UserAccount account) => For(account.Id);

    public UserWorkspace For(string userId)
    {
        var root = host.RequireRoot();
        lock (_lock)
        {
            // Moving the host's folder invalidates every cached root at once, so the workspaces
            // built against the old one are dropped rather than left pointing somewhere stale.
            if (_builtFor is not null && _builtFor != root) Clear();
            _builtFor = root;
        }
        return _workspaces.GetOrAdd(userId, id => new UserWorkspace(
            id, UserPaths.RootFor(root, id), index, log, dropbox.For(id), loggers));
    }

    /// <summary>Drops one account's workspace, after it is disabled or deleted.</summary>
    public void Forget(string userId)
    {
        if (_workspaces.TryRemove(userId, out var workspace)) workspace.Dispose();
        dropbox.Forget(userId);
    }

    public void Dispose()
    {
        lock (_lock) Clear();
    }

    private void Clear()
    {
        foreach (var key in _workspaces.Keys)
            if (_workspaces.TryRemove(key, out var workspace))
            {
                workspace.Dispose();
                dropbox.Forget(key);
            }
    }
}
