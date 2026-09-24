using System.Text;
using Homebase.Core;
using Homebase.Core.Providers;

namespace Homebase.Tests;

/// <summary>A Dropbox account that exists only in memory, so imports can be tested offline.</summary>
public sealed class StubDropbox : IDropboxConnection
{
    private readonly Dictionary<string, (SourceEntry Entry, byte[] Content)> _files = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured { get; init; } = true;
    public bool IsConnected { get; set; } = true;
    public string? AccountName { get; set; } = "Stub";

    /// <summary>
    /// The key in force for this account, settable because the real one is read afresh every time
    /// and so can change under a sign-in that is already out at dropbox.com.
    /// </summary>
    public string Key { get; set; } = "app-key";

    public string AppKey => IsConfigured
        ? Key
        : throw new LibraryException("No Dropbox app key here.", "provider_unconfigured");

    /// <summary>Held shut, a download waits here until a test lets it through.</summary>
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Completes as soon as the first download reaches the gate.</summary>
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Gated { get; init; }
    public IReadOnlyList<string> Downloaded => _downloaded;

    private readonly List<string> _downloaded = [];

    public void Add(string path, string rev, string contents) =>
        _files[path] = (new SourceEntry(path, Path.GetFileName(path), path.ToLowerInvariant(), path,
            false, Encoding.UTF8.GetByteCount(contents), rev, DateTimeOffset.UtcNow), Encoding.UTF8.GetBytes(contents));

    public void AddFolder(string path) =>
        _files[path] = (new SourceEntry(path, Path.GetFileName(path), path.ToLowerInvariant(), path,
            true, null, null, DateTimeOffset.UtcNow), []);

    private (SourceEntry Entry, byte[] Content) Require(string path) =>
        _files.TryGetValue(path, out var file) ? file : throw new LibraryException("No such file on Dropbox.", "not_found");

    public Task<DropboxAccount> GetAccountAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new DropboxAccount("id", AccountName ?? "Stub", null));
    // Only the files: handing a folder back its own entry would walk the import in circles.
    public Task<IReadOnlyList<SourceEntry>> ListFolderAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceEntry>>(
            _files.Values.Where(file => !file.Entry.IsFolder).Select(file => file.Entry).ToArray());
    public Task<SourceEntry> GetMetadataAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(Require(path).Entry);
    public async Task<Stream> OpenAsync(string path, CancellationToken cancellationToken)
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

    /// <summary>
    /// The redirect URI the exchange was given, which Dropbox requires to be the one the sign-in
    /// started with — so a test can tell that it was remembered rather than worked out again.
    /// </summary>
    public string? ExchangedWith { get; private set; }

    /// <summary>The app key the exchange was given, for the same reason.</summary>
    public string? ExchangedUnder { get; private set; }

    public Task ConnectAsync(string code, string verifier, string appKey, string redirectUri, CancellationToken cancellationToken)
    {
        ExchangedWith = redirectUri;
        ExchangedUnder = appKey;
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

    /// <summary>
    /// Every stub handed out so far, for a test that cares what happened to somebody's connection
    /// without wanting to know their account id to ask — asking by id would quietly make a new one.
    /// </summary>
    public IReadOnlyList<StubDropbox> All
    {
        get { lock (_lock) return _byUser.Values.ToArray(); }
    }

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
