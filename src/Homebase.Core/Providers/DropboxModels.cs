namespace Homebase.Core.Providers;

public sealed record DropboxAccount(string AccountId, string Name, string? Email);

/// <summary>
/// The seam between sync logic and Dropbox's HTTP API, so the engine is testable offline.
/// Implementations are responsible for supplying and refreshing access tokens.
///
/// A Dropbox account is one of the places files can be brought in from, so the reading half is
/// <see cref="IImportSource"/> and the import engine sees nothing Dropbox-specific at all. Where
/// entries land, and what "already imported" means, are the same for every Dropbox connection.
/// </summary>
public interface IDropboxApi : IImportSource
{
    bool IsConfigured { get; }
    bool IsConnected { get; }
    Task<DropboxAccount> GetAccountAsync(CancellationToken cancellationToken);

    string IImportSource.ProviderId => DropboxApi.ProviderName;
    string IImportSource.DestinationPrefix => "Dropbox";
}

/// <summary>
/// A Dropbox client that can also be connected and disconnected: what one account's provider
/// panel acts on. Kept apart from <see cref="IDropboxApi"/> so import logic, which has no
/// business signing anybody in, can only see the reading half.
/// </summary>
public interface IDropboxConnection : IDropboxApi
{
    /// <summary>The app key, or a refusal explaining that this host has none.</summary>
    string AppKey { get; }
    string? AccountName { get; }
    /// <param name="appKey">
    /// The app key the sign-in was started with, which need not be the one in force now: somebody
    /// else can change which Dropbox app an account uses while its browser is away at dropbox.com.
    /// Dropbox checks the code against the app it was issued to, so the exchange has to present
    /// that one or lose a sign-in the person completed correctly.
    /// </param>
    Task ConnectAsync(string code, string verifier, string appKey, string? redirectUri, CancellationToken cancellationToken);
    void Disconnect();
}

/// <summary>
/// Makes the Dropbox client belonging to one account. Every caller passes a user id that came
/// from an authenticated session, so two accounts can never share a token or an access cache.
/// </summary>
public interface IDropboxApiFactory
{
    IDropboxConnection For(string userId);
    /// <summary>Drops any cached client for this account, after a sign-out or a deletion.</summary>
    void Forget(string userId);
    /// <summary>Drops every cached client, after the host's Dropbox app key changes.</summary>
    void ForgetAll();
}
