namespace Homebase.Core.Providers;

public sealed record DropboxAccount(string AccountId, string Name, string? Email);

/// <summary>One entry as Dropbox reports it. <paramref name="Rev"/> is Dropbox's per-revision
/// identifier: when it changes, the file's contents changed.</summary>
public sealed record DropboxEntry(
    string Id,
    string Name,
    string PathLower,
    string PathDisplay,
    bool IsFolder,
    long? Size,
    string? Rev,
    DateTimeOffset? ServerModified);

/// <summary>
/// The seam between sync logic and Dropbox's HTTP API, so the engine is testable offline.
/// Implementations are responsible for supplying and refreshing access tokens.
/// </summary>
public interface IDropboxApi
{
    bool IsConfigured { get; }
    bool IsConnected { get; }
    Task<DropboxAccount> GetAccountAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DropboxEntry>> ListFolderAsync(string path, CancellationToken cancellationToken);
    Task<DropboxEntry> GetMetadataAsync(string path, CancellationToken cancellationToken);
    Task<Stream> DownloadAsync(string path, CancellationToken cancellationToken);
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
    Task ConnectAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken);
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
}
