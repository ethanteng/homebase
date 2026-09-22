using System.Text;
using Homebase.Core;
using Homebase.Core.Providers;

namespace Homebase.Tests;

/// <summary>A Dropbox account that exists only in memory, so imports can be tested offline.</summary>
public sealed class StubDropbox : IDropboxConnection
{
    private readonly Dictionary<string, (DropboxEntry Entry, byte[] Content)> _files = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured { get; init; } = true;
    public bool IsConnected { get; set; } = true;
    public string? AccountName { get; set; } = "Stub";

    public string AppKey => IsConfigured
        ? "app-key"
        : throw new LibraryException("No Dropbox app key here.", "provider_unconfigured");

    /// <summary>Held shut, a download waits here until a test lets it through.</summary>
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Completes as soon as the first download reaches the gate.</summary>
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Gated { get; init; }
    public IReadOnlyList<string> Downloaded => _downloaded;

    private readonly List<string> _downloaded = [];

    public void Add(string path, string rev, string contents) =>
        _files[path] = (new DropboxEntry(path, Path.GetFileName(path), path.ToLowerInvariant(), path,
            false, Encoding.UTF8.GetByteCount(contents), rev, DateTimeOffset.UtcNow), Encoding.UTF8.GetBytes(contents));

    public void AddFolder(string path) =>
        _files[path] = (new DropboxEntry(path, Path.GetFileName(path), path.ToLowerInvariant(), path,
            true, null, null, DateTimeOffset.UtcNow), []);

    private (DropboxEntry Entry, byte[] Content) Require(string path) =>
        _files.TryGetValue(path, out var file) ? file : throw new LibraryException("No such file on Dropbox.", "not_found");

    public Task<DropboxAccount> GetAccountAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new DropboxAccount("id", AccountName ?? "Stub", null));
    // Only the files: handing a folder back its own entry would walk the import in circles.
    public Task<IReadOnlyList<DropboxEntry>> ListFolderAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DropboxEntry>>(
            _files.Values.Where(file => !file.Entry.IsFolder).Select(file => file.Entry).ToArray());
    public Task<DropboxEntry> GetMetadataAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(Require(path).Entry);
    public async Task<Stream> DownloadAsync(string path, CancellationToken cancellationToken)
    {
        var file = Require(path);
        if (Gated)
        {
            // Deliberately deaf to cancellation: the point is to prove the import loop stops
            // between files once access is revoked, not that one download noticed.
            Reached.TrySetResult();
            await Gate.Task;
        }
        lock (_downloaded) _downloaded.Add(path);
        return new MemoryStream(file.Content, writable: false);
    }

    public Task ConnectAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public void Disconnect() => IsConnected = false;
}

/// <summary>Remembers one stub per account, so a test can see that two don't share a connection.</summary>
public sealed class StubDropboxes
{
    private readonly Dictionary<string, StubDropbox> _byUser = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public bool Configured { get; init; } = true;

    public StubDropbox For(string userId)
    {
        lock (_lock)
        {
            if (!_byUser.TryGetValue(userId, out var stub))
                _byUser[userId] = stub = new StubDropbox { IsConfigured = Configured };
            return stub;
        }
    }
}
