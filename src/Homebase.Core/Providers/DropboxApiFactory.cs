using System.Collections.Concurrent;
using Homebase.Core.Accounts;

namespace Homebase.Core.Providers;

/// <summary>
/// One Dropbox client per account, kept because each caches its own short-lived access token.
/// Two accounts never reach the same instance, so one person's connection can't answer for
/// another's even momentarily.
/// </summary>
public sealed class DropboxApiFactory(HttpClient client, ConnectorStore connectors, string? appKey) : IDropboxApiFactory
{
    private readonly ConcurrentDictionary<string, DropboxApi> _clients = new(StringComparer.Ordinal);

    public IDropboxConnection For(string userId) => _clients.GetOrAdd(userId, id =>
        new DropboxApi(client, new ConnectorTokens(connectors, id, DropboxApi.ProviderName), appKey));

    public void Forget(string userId) => _clients.TryRemove(userId, out _);
}
