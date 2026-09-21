using Homebase.Core.Accounts;

namespace Homebase.Core.Providers;

/// <summary>One account's provider connection, sealed in the host's control database.</summary>
public sealed class ConnectorTokens(ConnectorStore store, string userId, string provider) : IProviderTokens
{
    public string? Load() => store.LoadSecret(userId, provider);
    public string? LoadAccountName() => store.LoadAccountName(userId, provider);

    public Task SaveAsync(string refreshToken, string? accountName, CancellationToken cancellationToken)
    {
        store.Save(userId, provider, refreshToken, accountName);
        return Task.CompletedTask;
    }

    public void Clear() => store.Clear(userId, provider);
}
