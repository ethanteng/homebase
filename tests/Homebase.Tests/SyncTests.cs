using System.Text;
using Homebase.Core;
using Homebase.Core.Providers;

namespace Homebase.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly FakeDropbox _dropbox = new();
    private readonly LibraryService _library;
    private readonly SyncService _sync;

    public SyncTests()
    {
        Directory.CreateDirectory(_temporary);
        _root = Directory.CreateDirectory(Path.Combine(_temporary, "Library")).FullName;
        _library = new LibraryService(new SettingsStore(Path.Combine(_temporary, "Config")), new MetadataIndex());
        _library.SelectRootAsync(_root, CancellationToken.None).GetAwaiter().GetResult();
        _sync = new SyncService(_library, new SyncedFileStore(), _dropbox);
    }

    private string LocalPath(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Syncing_a_file_writes_it_into_the_library_and_records_the_revision()
    {
        _dropbox.AddFile("/Notes/hello.txt", "rev1", "First draft.");

        var outcome = await _sync.TrackAsync("/Notes/hello.txt", CancellationToken.None);

        Assert.Equal("Files/Dropbox/Notes/hello.txt", outcome.LocalPath);
        Assert.True(outcome.Downloaded);
        Assert.Equal("First draft.", await File.ReadAllTextAsync(LocalPath(outcome.LocalPath)));
        var tracked = Assert.Single(_sync.Tracked());
        Assert.Equal("rev1", tracked.RemoteRev);
        Assert.Equal(SyncState.Current, (await _sync.StatusAsync(CancellationToken.None)).Single().State);
        // Nothing is left behind in the metadata folder after a completed download.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".homebase"), "download.*"));
    }

    [Fact]
    public async Task A_new_remote_revision_replaces_the_local_copy_on_refresh()
    {
        _dropbox.AddFile("/Notes/hello.txt", "rev1", "First draft.");
        await _sync.TrackAsync("/Notes/hello.txt", CancellationToken.None);

        _dropbox.AddFile("/Notes/hello.txt", "rev2", "Edited elsewhere.");
        Assert.Equal(SyncState.RemoteChanged, (await _sync.StatusAsync(CancellationToken.None)).Single().State);

        var outcome = Assert.Single(await _sync.RefreshAsync(CancellationToken.None));

        Assert.True(outcome.Downloaded);
        Assert.Equal("Edited elsewhere.", await File.ReadAllTextAsync(LocalPath(outcome.LocalPath)));
        Assert.Equal("rev2", _sync.Tracked().Single().RemoteRev);
        Assert.Equal(SyncState.Current, (await _sync.StatusAsync(CancellationToken.None)).Single().State);
    }

    [Fact]
    public async Task A_copy_edited_here_is_reported_and_never_overwritten()
    {
        _dropbox.AddFile("/Notes/hello.txt", "rev1", "First draft.");
        await _sync.TrackAsync("/Notes/hello.txt", CancellationToken.None);
        var localFile = LocalPath("Files/Dropbox/Notes/hello.txt");
        await File.WriteAllTextAsync(localFile, "My own words.");
        File.SetLastWriteTimeUtc(localFile, DateTime.UtcNow.AddMinutes(5));

        _dropbox.AddFile("/Notes/hello.txt", "rev2", "Edited elsewhere.");
        Assert.Equal(SyncState.LocalEdited, (await _sync.StatusAsync(CancellationToken.None)).Single().State);

        var outcome = Assert.Single(await _sync.RefreshAsync(CancellationToken.None));

        Assert.False(outcome.Downloaded);
        Assert.Equal(SyncState.LocalEdited, outcome.State);
        Assert.Equal("My own words.", await File.ReadAllTextAsync(localFile));
        Assert.Equal("rev1", _sync.Tracked().Single().RemoteRev);
    }

    [Fact]
    public async Task A_deleted_local_copy_is_downloaded_again()
    {
        _dropbox.AddFile("/Notes/hello.txt", "rev1", "First draft.");
        await _sync.TrackAsync("/Notes/hello.txt", CancellationToken.None);
        File.Delete(LocalPath("Files/Dropbox/Notes/hello.txt"));
        Assert.Equal(SyncState.LocalMissing, (await _sync.StatusAsync(CancellationToken.None)).Single().State);

        var outcome = Assert.Single(await _sync.RefreshAsync(CancellationToken.None));

        Assert.True(outcome.Downloaded);
        Assert.Equal("First draft.", await File.ReadAllTextAsync(LocalPath("Files/Dropbox/Notes/hello.txt")));
    }

    [Fact]
    public async Task Homebase_refuses_to_take_over_a_file_it_did_not_write()
    {
        var destination = LocalPath("Files/Dropbox/Notes");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "hello.txt"), "Mine, from before.");
        _dropbox.AddFile("/Notes/hello.txt", "rev1", "Theirs.");

        var error = await Assert.ThrowsAsync<LibraryException>(
            () => _sync.TrackAsync("/Notes/hello.txt", CancellationToken.None));

        Assert.Equal("conflict", error.Code);
        Assert.Equal("Mine, from before.", await File.ReadAllTextAsync(Path.Combine(destination, "hello.txt")));
        Assert.Empty(_sync.Tracked());
    }

    [Fact]
    public async Task Hidden_remote_paths_and_folders_are_refused()
    {
        _dropbox.AddFile("/.config/secrets.txt", "rev1", "nope");
        _dropbox.AddFolder("/Notes");

        Assert.Equal("unsupported",
            (await Assert.ThrowsAsync<LibraryException>(() => _sync.TrackAsync("/.config/secrets.txt", CancellationToken.None))).Code);
        await Assert.ThrowsAsync<LibraryException>(() => _sync.TrackAsync("/Notes", CancellationToken.None));
        Assert.Empty(_sync.Tracked());
    }

    [Fact]
    public async Task The_same_file_is_only_tracked_once()
    {
        _dropbox.AddFile("/Notes/hello.txt", "rev1", "First draft.");
        await _sync.TrackAsync("/Notes/hello.txt", CancellationToken.None);

        await Assert.ThrowsAsync<LibraryException>(() => _sync.TrackAsync("/Notes/hello.txt", CancellationToken.None));
        Assert.Single(_sync.Tracked());
    }

    [Fact]
    public async Task An_edit_keeping_the_same_size_and_timestamp_is_still_protected()
    {
        // The cheap size-and-timestamp check cannot see this edit, so the recorded content
        // hash is the only thing standing between a newer revision and the user's words.
        _dropbox.AddFile("/notes/hello.txt", "rev1", "aaaaaaaaaaaa");
        await _sync.TrackAsync("/notes/hello.txt", CancellationToken.None);
        var localFile = LocalPath("Files/Dropbox/notes/hello.txt");
        var written = File.GetLastWriteTimeUtc(localFile);
        await File.WriteAllTextAsync(localFile, "bbbbbbbbbbbb");
        File.SetLastWriteTimeUtc(localFile, written);
        Assert.Equal(12, new FileInfo(localFile).Length);

        _dropbox.AddFile("/notes/hello.txt", "rev2", "cccccccccccc");
        var outcome = Assert.Single(await _sync.RefreshAsync(CancellationToken.None));

        Assert.False(outcome.Downloaded);
        Assert.Equal(SyncState.LocalEdited, outcome.State);
        Assert.Equal("bbbbbbbbbbbb", await File.ReadAllTextAsync(localFile));
    }

    [Fact]
    public async Task A_file_appearing_during_the_download_is_not_overwritten()
    {
        var destination = LocalPath("Files/Dropbox/notes/hello.txt");
        _dropbox.AddFile("/notes/hello.txt", "rev1", "From Dropbox.");
        // The race the destination check alone can't cover: the file arrives mid-transfer.
        _dropbox.WhileDownloading = () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "Written by something else.");
        };

        var error = await Assert.ThrowsAsync<LibraryException>(
            () => _sync.TrackAsync("/notes/hello.txt", CancellationToken.None));

        Assert.Equal("conflict", error.Code);
        Assert.Equal("Written by something else.", await File.ReadAllTextAsync(destination));
        Assert.Empty(_sync.Tracked());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".homebase"), "download.*"));
    }

    [Fact]
    public async Task An_unreachable_provider_is_reported_as_unknown_rather_than_up_to_date()
    {
        _dropbox.AddFile("/notes/hello.txt", "rev1", "First draft.");
        await _sync.TrackAsync("/notes/hello.txt", CancellationToken.None);
        _dropbox.Offline = true;

        var status = Assert.Single(await _sync.StatusAsync(CancellationToken.None));

        Assert.Equal(SyncState.RemoteUnavailable, status.State);
        Assert.Null(status.RemoteRev);
    }

    [Fact]
    public async Task One_missing_remote_file_does_not_strand_the_others()
    {
        _dropbox.AddFile("/notes/gone.txt", "rev1", "Here for now.");
        _dropbox.AddFile("/notes/kept.txt", "rev1", "First draft.");
        await _sync.TrackAsync("/notes/gone.txt", CancellationToken.None);
        await _sync.TrackAsync("/notes/kept.txt", CancellationToken.None);
        _dropbox.Remove("/notes/gone.txt");
        _dropbox.AddFile("/notes/kept.txt", "rev2", "Edited elsewhere.");

        var outcomes = await _sync.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        Assert.Equal(SyncState.RemoteUnavailable,
            outcomes.Single(outcome => outcome.LocalPath.EndsWith("gone.txt")).State);
        // The file after the failure still updated.
        Assert.Equal("Edited elsewhere.", await File.ReadAllTextAsync(LocalPath("Files/Dropbox/notes/kept.txt")));
    }

    public void Dispose()
    {
        _library.Dispose();
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }

    private sealed class FakeDropbox : IDropboxApi
    {
        private readonly Dictionary<string, DropboxEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _contents = new(StringComparer.OrdinalIgnoreCase);

        public bool IsConfigured => true;
        public bool IsConnected => true;
        public bool Offline { get; set; }
        public Action? WhileDownloading { get; set; }

        public void Remove(string path) => _entries.Remove(path);

        public void AddFile(string path, string rev, string contents)
        {
            _entries[path] = new DropboxEntry(path, Path.GetFileName(path), path.ToLowerInvariant(), path,
                false, Encoding.UTF8.GetByteCount(contents), rev, DateTimeOffset.UtcNow);
            _contents[path] = Encoding.UTF8.GetBytes(contents);
        }

        public void AddFolder(string path) =>
            _entries[path] = new DropboxEntry(path, Path.GetFileName(path), path.ToLowerInvariant(), path,
                true, null, null, null);

        private DropboxEntry Require(string path)
        {
            if (Offline) throw new LibraryException("Dropbox is unreachable.", "provider_failed");
            return _entries.TryGetValue(path, out var entry)
                ? entry
                : throw new LibraryException("No such file on Dropbox.", "not_found");
        }

        public Task<DropboxAccount> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DropboxAccount("id", "Test Account", "test@example.com"));
        public Task<IReadOnlyList<DropboxEntry>> ListFolderAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DropboxEntry>>(_entries.Values.ToArray());
        public Task<DropboxEntry> GetMetadataAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(Require(path));
        public Task<Stream> DownloadAsync(string path, CancellationToken cancellationToken)
        {
            Require(path);
            WhileDownloading?.Invoke();
            return Task.FromResult<Stream>(new MemoryStream(_contents[path], writable: false));
        }
    }
}
