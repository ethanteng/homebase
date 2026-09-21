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
