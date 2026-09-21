using System.Text;
using Homebase.Core;
using Homebase.Core.Providers;

namespace Homebase.Tests;

public sealed class ImportTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly FakeDropbox _dropbox = new();
    private readonly LibraryService _library;
    private readonly ImportService _imports;

    public ImportTests()
    {
        Directory.CreateDirectory(_temporary);
        _root = Directory.CreateDirectory(Path.Combine(_temporary, "Library")).FullName;
        _library = new LibraryService(new SettingsStore(Path.Combine(_temporary, "Config")), new MetadataIndex());
        _library.SelectRootAsync(_root, CancellationToken.None).GetAwaiter().GetResult();
        _imports = new ImportService(_library, new ImportLog(), _dropbox);
    }

    private string LocalPath(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task A_single_file_is_copied_in_and_recorded_with_its_provenance()
    {
        _dropbox.AddFile("/notes/hello.txt", "rev1", "First draft.");

        var result = await _imports.ImportAsync("/notes/hello.txt", CancellationToken.None);

        var item = Assert.Single(result.Imported);
        Assert.Equal("Files/Dropbox/notes/hello.txt", item.LocalPath);
        Assert.Equal("First draft.", await File.ReadAllTextAsync(LocalPath(item.LocalPath)));
        var recorded = Assert.Single(_imports.Imported());
        Assert.Equal("rev1", recorded.RemoteRev);
        Assert.NotEqual("", recorded.ContentHash);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".homebase"), "import.*"));
    }

    [Fact]
    public async Task A_folder_brings_its_whole_tree_and_leaves_hidden_entries_behind()
    {
        _dropbox.AddFolder("/notes");
        _dropbox.AddFolder("/notes/deeper");
        _dropbox.AddFile("/notes/one.txt", "rev1", "One.");
        _dropbox.AddFile("/notes/deeper/two.txt", "rev1", "Two.");
        _dropbox.AddFile("/notes/.secret", "rev1", "Hidden.");

        var result = await _imports.ImportAsync("/notes", CancellationToken.None);

        Assert.Equal(2, result.ImportedCount);
        Assert.Equal("One.", await File.ReadAllTextAsync(LocalPath("Files/Dropbox/notes/one.txt")));
        Assert.Equal("Two.", await File.ReadAllTextAsync(LocalPath("Files/Dropbox/notes/deeper/two.txt")));
        Assert.False(File.Exists(LocalPath("Files/Dropbox/notes/.secret")));
        Assert.Contains(result.Skipped, skip => skip.RemotePath.EndsWith(".secret"));
    }

    [Fact]
    public async Task Importing_again_brings_only_what_is_new()
    {
        _dropbox.AddFolder("/notes");
        _dropbox.AddFile("/notes/one.txt", "rev1", "One.");
        await _imports.ImportAsync("/notes", CancellationToken.None);
        _dropbox.AddFile("/notes/two.txt", "rev1", "Two.");

        var result = await _imports.ImportAsync("/notes", CancellationToken.None);

        Assert.Equal("Files/Dropbox/notes/two.txt", Assert.Single(result.Imported).LocalPath);
        Assert.Contains(result.Skipped, skip => skip.Reason.Contains("Already imported"));
        Assert.Equal(2, _imports.Imported().Count);
    }

    [Fact]
    public async Task A_newer_revision_never_replaces_the_copy_you_already_have()
    {
        // Once a file is home, the local copy is the one that counts.
        _dropbox.AddFile("/notes/hello.txt", "rev1", "First draft.");
        await _imports.ImportAsync("/notes/hello.txt", CancellationToken.None);
        _dropbox.AddFile("/notes/hello.txt", "rev2", "Changed on Dropbox.");

        var result = await _imports.ImportAsync("/notes/hello.txt", CancellationToken.None);

        Assert.Empty(result.Imported);
        Assert.Equal("First draft.", await File.ReadAllTextAsync(LocalPath("Files/Dropbox/notes/hello.txt")));
        Assert.Equal("rev1", _imports.Imported().Single().RemoteRev);
    }

    [Fact]
    public async Task Homebase_refuses_to_take_over_a_file_it_did_not_write()
    {
        var destination = LocalPath("Files/Dropbox/notes");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "hello.txt"), "Mine, from before.");
        _dropbox.AddFile("/notes/hello.txt", "rev1", "Theirs.");

        var result = await _imports.ImportAsync("/notes/hello.txt", CancellationToken.None);

        Assert.Empty(result.Imported);
        Assert.Contains(result.Skipped, skip => skip.Reason.Contains("won’t overwrite"));
        Assert.Equal("Mine, from before.", await File.ReadAllTextAsync(Path.Combine(destination, "hello.txt")));
        Assert.Empty(_imports.Imported());
    }

    [Fact]
    public async Task A_file_appearing_during_the_download_is_not_overwritten()
    {
        var destination = LocalPath("Files/Dropbox/notes/hello.txt");
        _dropbox.AddFile("/notes/hello.txt", "rev1", "From Dropbox.");
        _dropbox.WhileDownloading = () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "Written by something else.");
        };

        var result = await _imports.ImportAsync("/notes/hello.txt", CancellationToken.None);

        Assert.Empty(result.Imported);
        Assert.Equal("Written by something else.", await File.ReadAllTextAsync(destination));
        Assert.Empty(_imports.Imported());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".homebase"), "import.*"));
    }

    [Fact]
    public async Task One_unreadable_file_does_not_abandon_the_rest_of_the_folder()
    {
        _dropbox.AddFolder("/notes");
        _dropbox.AddFile("/notes/broken.txt", "rev1", "Never arrives.");
        _dropbox.AddFile("/notes/fine.txt", "rev1", "Arrives.");
        _dropbox.FailDownloadOf = "/notes/broken.txt";

        var result = await _imports.ImportAsync("/notes", CancellationToken.None);

        Assert.Equal("Files/Dropbox/notes/fine.txt", Assert.Single(result.Imported).LocalPath);
        Assert.Contains(result.Skipped, skip => skip.RemotePath.EndsWith("broken.txt"));
        Assert.Equal("Arrives.", await File.ReadAllTextAsync(LocalPath("Files/Dropbox/notes/fine.txt")));
    }

    [Fact]
    public async Task A_hidden_path_is_refused_outright()
    {
        _dropbox.AddFile("/.config/secrets.txt", "rev1", "nope");

        var result = await _imports.ImportAsync("/.config/secrets.txt", CancellationToken.None);

        Assert.Empty(result.Imported);
        Assert.Contains(result.Skipped, skip => skip.Reason.Contains("hidden"));
        Assert.Empty(_imports.Imported());
    }

    [Fact]
    public async Task A_subfolder_that_cannot_be_listed_does_not_abandon_the_rest()
    {
        // Collection finishes before any download starts, so an uncaught listing failure here
        // would discard files already found elsewhere in the tree.
        _dropbox.AddFolder("/notes");
        _dropbox.AddFolder("/notes/broken");
        _dropbox.AddFile("/notes/fine.txt", "rev1", "Arrives.");
        _dropbox.FailListingOf = "/notes/broken";

        var result = await _imports.ImportAsync("/notes", CancellationToken.None);

        Assert.Equal("Files/Dropbox/notes/fine.txt", Assert.Single(result.Imported).LocalPath);
        Assert.Contains(result.Skipped, skip => skip.RemotePath.Contains("broken"));
        Assert.Equal("Arrives.", await File.ReadAllTextAsync(LocalPath("Files/Dropbox/notes/fine.txt")));
    }

    [Fact]
    public async Task Hitting_the_file_limit_is_reported_rather_than_passed_over()
    {
        _dropbox.AddFolder("/notes");
        for (var index = 0; index < 5; index++)
            _dropbox.AddFile($"/notes/file{index}.txt", "rev1", $"File {index}.");
        var imports = new ImportService(_library, new ImportLog(), _dropbox) { MaxEntries = 2 };

        var result = await imports.ImportAsync("/notes", CancellationToken.None);

        Assert.Equal(2, result.ImportedCount);
        // The point of the finding: a truncated import must not read as a complete one.
        Assert.Contains(result.Skipped, skip => skip.Reason.Contains("wasn’t visited"));
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
        public Action? WhileDownloading { get; set; }
        public string? FailDownloadOf { get; set; }
        public string? FailListingOf { get; set; }

        public void AddFile(string path, string rev, string contents)
        {
            _entries[path] = new DropboxEntry(path, Path.GetFileName(path), path.ToLowerInvariant(), path,
                false, Encoding.UTF8.GetByteCount(contents), rev, DateTimeOffset.UtcNow);
            _contents[path] = Encoding.UTF8.GetBytes(contents);
        }

        public void AddFolder(string path) =>
            _entries[path] = new DropboxEntry(path, Path.GetFileName(path), path.ToLowerInvariant(), path,
                true, null, null, null);

        private DropboxEntry Require(string path) =>
            _entries.TryGetValue(path, out var entry) ? entry : throw new LibraryException("No such file on Dropbox.", "not_found");

        public Task<DropboxAccount> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DropboxAccount("id", "Test Account", "test@example.com"));

        public Task<IReadOnlyList<DropboxEntry>> ListFolderAsync(string path, CancellationToken cancellationToken)
        {
            if (FailListingOf is not null && path.Equals(FailListingOf, StringComparison.OrdinalIgnoreCase))
                throw new LibraryException("Dropbox couldn’t list this folder.", "provider_failed");
            var prefix = path.Length == 0 ? "/" : $"{path.TrimEnd('/')}/";
            return Task.FromResult<IReadOnlyList<DropboxEntry>>(_entries.Values
                .Where(entry => entry.PathLower.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && !entry.PathLower[prefix.Length..].Contains('/'))
                .ToArray());
        }

        public Task<DropboxEntry> GetMetadataAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(Require(path));

        public Task<Stream> DownloadAsync(string path, CancellationToken cancellationToken)
        {
            Require(path);
            if (FailDownloadOf is not null && path.Equals(FailDownloadOf, StringComparison.OrdinalIgnoreCase))
                throw new LibraryException("Dropbox couldn’t send this file.", "provider_failed");
            WhileDownloading?.Invoke();
            return Task.FromResult<Stream>(new MemoryStream(_contents[path], writable: false));
        }
    }
}
