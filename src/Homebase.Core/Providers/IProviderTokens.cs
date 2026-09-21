namespace Homebase.Core.Providers;

/// <summary>
/// Where one account's connection to one provider is kept. The seam exists so a provider client
/// never knows whose token it is holding: the caller that made it already decided that, from an
/// authenticated session.
/// </summary>
public interface IProviderTokens
{
    string? Load();
    string? LoadAccountName();
    Task SaveAsync(string refreshToken, string? accountName, CancellationToken cancellationToken);
    void Clear();
}
