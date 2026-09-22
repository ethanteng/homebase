using System.Collections.Concurrent;
using Homebase.Core.Accounts;

namespace Homebase.Core.Providers;

/// <summary>
/// One Dropbox client per account, kept because each caches its own short-lived access token.
/// Two accounts never reach the same instance, so one person's connection can't answer for
/// another's even momentarily.
/// </summary>
public sealed class DropboxApiFactory(HttpClient client, ConnectorStore connectors, Func<string?> appKey) : IDropboxApiFactory
{
    private readonly ConcurrentDictionary<string, DropboxApi> _clients = new(StringComparer.Ordinal);

    public IDropboxConnection For(string userId) => _clients.GetOrAdd(userId, id =>
        new DropboxApi(client, new ConnectorTokens(connectors, id, DropboxApi.ProviderName), appKey));

    public void Forget(string userId) => _clients.TryRemove(userId, out _);

    /// <summary>
    /// Drops every cached client, after the host's app key changes. Each holds a short-lived access
    /// token issued to the old app, which would go on working for minutes after the change.
    /// </summary>
    public void ForgetAll() => _clients.Clear();
}
