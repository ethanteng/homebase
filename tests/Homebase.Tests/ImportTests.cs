using System.Text;
using Homebase.Core;
using Homebase.Core.Providers;
using Microsoft.Extensions.Logging.Abstractions;

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
        _library = new LibraryService(_root, new MetadataIndex());
        _library.Initialize();
        _imports = new ImportService(_library, new ImportLog(), NullLogger<ImportService>.Instance);
    }

    private string LocalPath(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task A_single_file_is_copied_in_and_recorded_with_its_provenance()
    {
        _dropbox.AddFile("/notes/hello.txt", "rev1", "First draft.");

        var result = await _imports.ImportAsync(_dropbox, "/notes/hello.txt", CancellationToken.None);

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

        var result = await _imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);

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
        await _imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);
        _dropbox.AddFile("/notes/two.txt", "rev1", "Two.");

        var result = await _imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);

        Assert.Equal("Files/Dropbox/notes/two.txt", Assert.Single(result.Imported).LocalPath);
        Assert.Contains(result.Skipped, skip => skip.Reason.Contains("Already imported"));
        Assert.Equal(2, _imports.Imported().Count);
    }

    [Fact]
    public async Task A_newer_revision_never_replaces_the_copy_you_already_have()
    {
        // Once a file is home, the local copy is the one that counts.
        _dropbox.AddFile("/notes/hello.txt", "rev1", "First draft.");
        await _imports.ImportAsync(_dropbox, "/notes/hello.txt", CancellationToken.None);
        _dropbox.AddFile("/notes/hello.txt", "rev2", "Changed on Dropbox.");

        var result = await _imports.ImportAsync(_dropbox, "/notes/hello.txt", CancellationToken.None);

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

        var result = await _imports.ImportAsync(_dropbox, "/notes/hello.txt", CancellationToken.None);

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

        var result = await _imports.ImportAsync(_dropbox, "/notes/hello.txt", CancellationToken.None);

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

        var result = await _imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);

        Assert.Equal("Files/Dropbox/notes/fine.txt", Assert.Single(result.Imported).LocalPath);
        Assert.Contains(result.Skipped, skip => skip.RemotePath.EndsWith("broken.txt"));
        Assert.Equal("Arrives.", await File.ReadAllTextAsync(LocalPath("Files/Dropbox/notes/fine.txt")));
    }

    [Fact]
    public async Task A_file_that_times_out_does_not_abandon_the_rest_of_the_folder()
    {
        // A timeout arrives as cancellation, which used to escape the per-file catch and end the
        // whole import — the rest of the folder was lost to one slow file.
        _dropbox.AddFolder("/notes");
        _dropbox.AddFile("/notes/slow.txt", "rev1", "Never arrives.");
        _dropbox.AddFile("/notes/fine.txt", "rev1", "Arrives.");
        _dropbox.TimeOutDownloadOf = "/notes/slow.txt";

        var result = await _imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);

        Assert.Equal("Files/Dropbox/notes/fine.txt", Assert.Single(result.Imported).LocalPath);
        var skip = Assert.Single(result.Skipped, skip => skip.RemotePath.EndsWith("slow.txt"));
        // "A task was canceled" tells the person nothing about their file.
        Assert.Equal("Getting this file took too long.", skip.Reason);
        Assert.False(skip.Expected);
    }

    [Fact]
    public async Task A_file_that_cannot_be_written_does_not_abandon_the_rest_of_the_folder()
    {
        _dropbox.AddFolder("/notes");
        _dropbox.AddFile("/notes/broken.txt", "rev1", "Never lands.");
        _dropbox.AddFile("/notes/fine.txt", "rev1", "Arrives.");
        _dropbox.FailWriteOf = "/notes/broken.txt";

        var result = await _imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);

        Assert.Equal("Files/Dropbox/notes/fine.txt", Assert.Single(result.Imported).LocalPath);
        Assert.Contains(result.Skipped, skip => skip.RemotePath.EndsWith("broken.txt"));
    }

    [Fact]
    public async Task A_cancelled_import_stops_instead_of_skipping_its_way_to_the_end()
    {
        // Setting aside a timeout must not also set aside the caller giving up.
        _dropbox.AddFolder("/notes");
        for (var index = 0; index < 4; index++)
            _dropbox.AddFile($"/notes/file{index}.txt", "rev1", $"File {index}.");
        using var cancellation = new CancellationTokenSource();
        _dropbox.WhileDownloading = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _imports.ImportAsync(_dropbox, "/notes", cancellation.Token));
    }

    [Fact]
    public async Task Bringing_a_folder_home_twice_reports_no_problems_the_second_time()
    {
        // "Already imported" is the ordinary outcome, not something to wave at a person.
        _dropbox.AddFolder("/notes");
        _dropbox.AddFile("/notes/one.txt", "rev1", "One.");
        await _imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);

        var result = await _imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);

        Assert.Empty(result.Imported);
        Assert.All(result.Skipped, skip => Assert.True(skip.Expected));
    }

    [Fact]
    public void The_drive_a_folder_lives_on_reports_its_free_space()
    {
        // An external drive is its own volume, so this has to answer about the path, not the boot disk.
        var report = Storage.For(_root);

        Assert.NotNull(report);
        Assert.True(report.FreeBytes > 0);
        Assert.True(report.TotalBytes >= report.FreeBytes);
    }

    [Fact]
    public async Task A_folder_that_would_fill_the_drive_is_refused_before_anything_arrives()
    {
        _dropbox.AddFolder("/notes");
        _dropbox.AddFile("/notes/big.bin", "rev1", new string('x', 4096));
        var imports = new ImportService(_library, new ImportLog(), NullLogger<ImportService>.Instance)
        {
            Space = _ => new StorageReport(FreeBytes: 1024, TotalBytes: 8192),
            Headroom = 0
        };

        var error = await Assert.ThrowsAsync<LibraryException>(
            () => imports.ImportAsync(_dropbox, "/notes", CancellationToken.None));

        Assert.Equal("unavailable", error.Code);
        // Both numbers, so the message says what to do about it.
        Assert.Contains("4 KB", error.Message);
        Assert.Contains("1 KB", error.Message);
        Assert.False(Directory.Exists(LocalPath("Files/Dropbox/notes")));
        Assert.Empty(_imports.Imported());
    }

    [Fact]
    public async Task Room_is_left_on_the_drive_rather_than_filling_it_to_the_last_byte()
    {
        _dropbox.AddFile("/notes/hello.txt", "rev1", "1234567890");
        var imports = new ImportService(_library, new ImportLog(), NullLogger<ImportService>.Instance)
        {
            // Exactly enough room for the file and nothing to spare, which is not enough.
            Space = _ => new StorageReport(FreeBytes: 10, TotalBytes: 100),
            Headroom = 1024
        };

        await Assert.ThrowsAsync<LibraryException>(
            () => imports.ImportAsync(_dropbox, "/notes/hello.txt", CancellationToken.None));
    }

    [Fact]
    public async Task A_folder_already_home_still_fits_however_little_room_is_left()
    {
        // Bringing a folder again costs nothing on disk, so a full drive mustn't refuse it.
        _dropbox.AddFolder("/notes");
        _dropbox.AddFile("/notes/one.txt", "rev1", "One.");
        await _imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);
        var imports = new ImportService(_library, new ImportLog(), NullLogger<ImportService>.Instance)
        {
            Space = _ => new StorageReport(FreeBytes: 0, TotalBytes: 8192)
        };

        var result = await imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);

        Assert.Empty(result.Imported);
        Assert.All(result.Skipped, skip => Assert.True(skip.Expected));
    }

    [Fact]
    public async Task A_file_whose_place_is_taken_is_not_counted_against_the_drive()
    {
        // Uncloud won't overwrite it, so it is never downloaded — holding the folder back over
        // room it was never going to need would refuse the files that could have arrived.
        var destination = LocalPath("Files/Dropbox/notes");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "big.bin"), "Mine, from before.");
        _dropbox.AddFolder("/notes");
        _dropbox.AddFile("/notes/big.bin", "rev1", new string('x', 4096));
        _dropbox.AddFile("/notes/small.txt", "rev1", "Small.");
        var imports = new ImportService(_library, new ImportLog(), NullLogger<ImportService>.Instance)
        {
            Space = _ => new StorageReport(FreeBytes: 1024, TotalBytes: 8192),
            Headroom = 0
        };

        var result = await imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);

        Assert.Equal("Files/Dropbox/notes/small.txt", Assert.Single(result.Imported).LocalPath);
        Assert.Contains(result.Skipped, skip => skip.Reason.Contains("won’t overwrite"));
        Assert.Equal("Mine, from before.",
            await File.ReadAllTextAsync(Path.Combine(destination, "big.bin")));
    }

    [Fact]
    public async Task A_file_standing_where_a_folder_belongs_blocks_only_what_is_under_it()
    {
        // Nothing can be written beneath an ordinary file, so those bytes are never fetched and
        // must not be counted — least of all against the files that could have arrived.
        var notes = Directory.CreateDirectory(LocalPath("Files/Dropbox/notes")).FullName;
        await File.WriteAllTextAsync(Path.Combine(notes, "archive"), "Not a folder.");
        _dropbox.AddFolder("/notes");
        _dropbox.AddFolder("/notes/archive");
        _dropbox.AddFile("/notes/archive/big.bin", "rev1", new string('x', 4096));
        _dropbox.AddFile("/notes/small.txt", "rev1", "Small.");
        var imports = new ImportService(_library, new ImportLog(), NullLogger<ImportService>.Instance)
        {
            Space = _ => new StorageReport(FreeBytes: 1024, TotalBytes: 8192),
            Headroom = 0
        };

        var result = await imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);

        Assert.Equal("Files/Dropbox/notes/small.txt", Assert.Single(result.Imported).LocalPath);
        Assert.Contains(result.Skipped, skip => skip.RemotePath.EndsWith("big.bin"));
        Assert.Equal("Not a folder.", await File.ReadAllTextAsync(Path.Combine(notes, "archive")));
    }

    [Fact]
    public async Task Measuring_leaves_out_a_file_whose_place_is_already_taken()
    {
        var destination = LocalPath("Files/Dropbox/notes");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "big.bin"), "Mine, from before.");
        _dropbox.AddFolder("/notes");
        _dropbox.AddFile("/notes/big.bin", "rev1", new string('x', 4096));
        _dropbox.AddFile("/notes/small.txt", "rev1", "Small.");
        var imports = new ImportService(_library, new ImportLog(), NullLogger<ImportService>.Instance)
        {
            Space = _ => new StorageReport(FreeBytes: 1024, TotalBytes: 8192),
            Headroom = 0
        };

        var estimate = await imports.MeasureAsync(_dropbox, "/notes", CancellationToken.None);

        Assert.Equal(2, estimate.FileCount);
        Assert.Equal(1, estimate.NewFileCount);
        Assert.Equal(6, estimate.NewBytes);
        Assert.True(estimate.Fits);
    }

    [Fact]
    public async Task Measuring_a_folder_leaves_out_what_is_already_home()
    {
        _dropbox.AddFolder("/notes");
        _dropbox.AddFile("/notes/one.txt", "rev1", "12345");
        _dropbox.AddFile("/notes/two.txt", "rev1", "1234567890");
        await _imports.ImportAsync(_dropbox, "/notes/one.txt", CancellationToken.None);
        var imports = new ImportService(_library, new ImportLog(), NullLogger<ImportService>.Instance)
        {
            Space = _ => new StorageReport(FreeBytes: 1_000_000, TotalBytes: 2_000_000)
        };

        var estimate = await imports.MeasureAsync(_dropbox, "/notes", CancellationToken.None);

        Assert.Equal(2, estimate.FileCount);
        Assert.Equal(15, estimate.Bytes);
        Assert.Equal(1, estimate.NewFileCount);
        Assert.Equal(10, estimate.NewBytes);
        Assert.Equal(1_000_000, estimate.FreeBytes);
        Assert.True(estimate.Fits);
    }

    [Fact]
    public async Task Measuring_says_when_a_folder_would_not_fit()
    {
        _dropbox.AddFolder("/notes");
        _dropbox.AddFile("/notes/big.bin", "rev1", new string('x', 4096));
        var imports = new ImportService(_library, new ImportLog(), NullLogger<ImportService>.Instance)
        {
            Space = _ => new StorageReport(FreeBytes: 1024, TotalBytes: 8192),
            Headroom = 0
        };

        var estimate = await imports.MeasureAsync(_dropbox, "/notes", CancellationToken.None);

        Assert.Equal(4096, estimate.NewBytes);
        Assert.False(estimate.Fits);
    }

    [Fact]
    public async Task A_hidden_path_is_refused_outright()
    {
        _dropbox.AddFile("/.config/secrets.txt", "rev1", "nope");

        var result = await _imports.ImportAsync(_dropbox, "/.config/secrets.txt", CancellationToken.None);

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

        var result = await _imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);

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
        var imports = new ImportService(_library, new ImportLog(), NullLogger<ImportService>.Instance)
            { MaxEntries = 2 };

        var result = await imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);

        Assert.Equal(2, result.ImportedCount);
        // The point of the finding: a truncated import must not read as a complete one.
        Assert.Contains(result.Skipped, skip => skip.Reason.Contains("wasn’t visited"));
    }

    [Fact]
    public async Task An_import_runs_on_its_own_and_says_how_far_it_has_got()
    {
        _dropbox.AddFolder("/notes");
        for (var index = 0; index < 3; index++)
            _dropbox.AddFile($"/notes/file{index}.txt", "rev1", $"File {index}.");
        var jobs = new ImportJobs(_imports, NullLogger<ImportJobs>.Instance);

        var started = jobs.Start(_dropbox, DropboxApi.ProviderName, "/notes", "notes");

        // The request that starts an import answers before the files arrive.
        Assert.True(started.Running);
        Assert.Equal("notes", started.Label);
        var finished = await Settled(jobs);
        Assert.Equal(ImportStage.Done, finished.Stage);
        Assert.Equal(3, finished.Result!.ImportedCount);
        Assert.Equal(3, finished.TotalFiles);
        Assert.Equal(3, finished.CompletedFiles);
        Assert.Null(finished.CurrentFile);
    }

    [Fact]
    public async Task A_second_import_is_refused_while_one_is_still_running()
    {
        _dropbox.AddFile("/notes/one.txt", "rev1", "One.");
        _dropbox.Hold = new TaskCompletionSource();
        var jobs = new ImportJobs(_imports, NullLogger<ImportJobs>.Instance);
        jobs.Start(_dropbox, DropboxApi.ProviderName, "/notes/one.txt", "one.txt");
        await WaitFor(() => jobs.Current is { Stage: ImportStage.Bringing });

        var error = Assert.Throws<LibraryException>(() => jobs.Start(_dropbox, DropboxApi.ProviderName, "/notes/one.txt", "one.txt"));

        Assert.Equal("busy", error.Code);
        _dropbox.Hold.SetResult();
        await Settled(jobs);
    }

    [Fact]
    public async Task Stopping_an_import_ends_it_rather_than_working_through_the_rest()
    {
        _dropbox.AddFolder("/notes");
        for (var index = 0; index < 3; index++)
            _dropbox.AddFile($"/notes/file{index}.txt", "rev1", $"File {index}.");
        _dropbox.Hold = new TaskCompletionSource();
        var jobs = new ImportJobs(_imports, NullLogger<ImportJobs>.Instance);
        jobs.Start(_dropbox, DropboxApi.ProviderName, "/notes", "notes");
        await WaitFor(() => jobs.Current is { Stage: ImportStage.Bringing });

        jobs.Cancel();

        var finished = await Settled(jobs);
        Assert.Equal(ImportStage.Stopped, finished.Stage);
        Assert.Null(finished.CurrentFile);
        // Stopping is not a failure, and nothing half-written is left behind.
        Assert.Null(finished.Error);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".homebase"), "import.*"));
    }

    [Fact]
    public async Task An_import_that_cannot_start_is_reported_on_the_job_rather_than_thrown_away()
    {
        _dropbox.AddFolder("/notes");
        _dropbox.AddFile("/notes/big.bin", "rev1", new string('x', 4096));
        var imports = new ImportService(_library, new ImportLog(), NullLogger<ImportService>.Instance)
        {
            Space = _ => new StorageReport(FreeBytes: 1024, TotalBytes: 8192),
            Headroom = 0
        };
        var jobs = new ImportJobs(imports, NullLogger<ImportJobs>.Instance);

        jobs.Start(_dropbox, DropboxApi.ProviderName, "/notes", "notes");

        var finished = await Settled(jobs);
        Assert.Equal(ImportStage.Failed, finished.Stage);
        Assert.Contains("free", finished.Error);
    }

    private static async Task<ImportJob> Settled(ImportJobs jobs)
    {
        await WaitFor(() => jobs.Current is { Running: false });
        return jobs.Current!;
    }

    private static async Task WaitFor(Func<bool> reached)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (reached()) return;
            await Task.Delay(15);
        }
        throw new TimeoutException("The import never reached the state this test was waiting for.");
    }

    public void Dispose()
    {
        _library.Dispose();
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }

    /// <summary>A download that fails partway, the way a dropped connection or a full disk does.</summary>
    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The connection dropped.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Two_files_whose_names_differ_only_in_case_are_two_files()
    {
        // The estimate and the import have to agree about what is already home, and the log is
        // SQLite, whose text comparison is case-sensitive. Matching case-insensitively when
        // measuring made them disagree: the estimate wrote this file off as already here, and the
        // import then fetched it anyway — over room the drive was never checked for.
        //
        // Built from the log rather than from two files on disk, because whether a volume can hold
        // both at once is exactly the thing that differs between the machines this runs on.
        var source = new CaseKeepingSource();
        source.Add("/Photo.jpg", "one");
        source.Add("/photo.jpg", "another");
        new ImportLog().Record(_root, new ImportedFile(
            source.ProviderId, "/Photo.jpg", "rev1", "Files/Camera/Photo.jpg", 3, "hash", DateTimeOffset.UtcNow));

        var estimate = await _imports.MeasureAsync(source, "/", CancellationToken.None);

        Assert.Equal(2, estimate.FileCount);
        Assert.Equal(1, estimate.NewFileCount);
    }

    [Fact]
    public async Task A_file_another_place_already_brought_home_is_not_a_problem_to_report()
    {
        // Two places are allowed to share a destination, and often should: a Dropbox folder synced
        // onto this computer and the same account online are the same files, and somebody who uses
        // both wants one copy — not a page of warnings claiming Uncloud won't overwrite files it
        // didn't put there, about files it did put there.
        _dropbox.AddFolder("/notes");
        _dropbox.AddFile("/notes/one.txt", "rev1", "One.");
        await _imports.ImportAsync(_dropbox, "/notes", CancellationToken.None);

        // The same file offered again by something that writes to the same place.
        var alongside = new CaseKeepingSource { Destination = "Files/Dropbox" };
        alongside.Add("/notes/one.txt", "One.");

        var result = await _imports.ImportAsync(alongside, "/", CancellationToken.None);

        Assert.Empty(result.Imported);
        var skip = Assert.Single(result.Skipped);
        // Expected, so it never reaches the "Not brought home" list a person is meant to act on.
        Assert.True(skip.Expected);
        // And the estimate agrees rather than counting room for a file that is already here.
        var estimate = await _imports.MeasureAsync(alongside, "/", CancellationToken.None);
        Assert.Equal(0, estimate.NewFileCount);
    }

    /// <summary>A source that names its files exactly as they are, the way a folder on disk does.</summary>
    private sealed class CaseKeepingSource : IImportSource
    {
        private readonly Dictionary<string, (SourceEntry Entry, byte[] Content)> _files = new(StringComparer.Ordinal);

        public string ProviderId => "folder:test";
        public string Destination { get; init; } = "Files/Camera";
        public string DestinationPrefix => Destination;

        public void Add(string path, string contents) => _files[path] = (
            new SourceEntry(path, Path.GetFileName(path), path, path, false,
                Encoding.UTF8.GetByteCount(contents), "rev1", DateTimeOffset.UtcNow),
            Encoding.UTF8.GetBytes(contents));

        public Task<SourceEntry> GetMetadataAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(_files.TryGetValue(path, out var file)
                ? file.Entry
                : new SourceEntry(path, "root", path, path, true, null, null, null));

        public Task<IReadOnlyList<SourceEntry>> ListFolderAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceEntry>>(_files.Values.Select(file => file.Entry).ToArray());

        public Task<Stream> OpenAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(_files[path].Content, writable: false));
    }

    private sealed class FakeDropbox : IDropboxApi
    {
        private readonly Dictionary<string, SourceEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _contents = new(StringComparer.OrdinalIgnoreCase);

        public bool IsConfigured => true;
        public bool IsConnected => true;
        public Action? WhileDownloading { get; set; }
        public string? FailDownloadOf { get; set; }
        public string? FailListingOf { get; set; }
        public string? TimeOutDownloadOf { get; set; }
        public string? FailWriteOf { get; set; }
        /// <summary>Keeps downloads open so a test can look at an import that is still running.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public void AddFile(string path, string rev, string contents)
        {
            _entries[path] = new SourceEntry(path, Path.GetFileName(path), path.ToLowerInvariant(), path,
                false, Encoding.UTF8.GetByteCount(contents), rev, DateTimeOffset.UtcNow);
            _contents[path] = Encoding.UTF8.GetBytes(contents);
        }

        public void AddFolder(string path) =>
            _entries[path] = new SourceEntry(path, Path.GetFileName(path), path.ToLowerInvariant(), path,
                true, null, null, null);

        private SourceEntry Require(string path) =>
            _entries.TryGetValue(path, out var entry) ? entry : throw new LibraryException("No such file on Dropbox.", "not_found");

        public Task<DropboxAccount> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DropboxAccount("id", "Test Account", "test@example.com"));

        public Task<IReadOnlyList<SourceEntry>> ListFolderAsync(string path, CancellationToken cancellationToken)
        {
            if (FailListingOf is not null && path.Equals(FailListingOf, StringComparison.OrdinalIgnoreCase))
                throw new LibraryException("Dropbox couldn’t list this folder.", "provider_failed");
            var prefix = path.Length == 0 ? "/" : $"{path.TrimEnd('/')}/";
            return Task.FromResult<IReadOnlyList<SourceEntry>>(_entries.Values
                .Where(entry => entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && !entry.Path[prefix.Length..].Contains('/'))
                .ToArray());
        }

        public Task<SourceEntry> GetMetadataAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(Require(path));

        public async Task<Stream> OpenAsync(string path, CancellationToken cancellationToken)
        {
            Require(path);
            if (Hold is not null) await Hold.Task.WaitAsync(cancellationToken);
            if (FailDownloadOf is not null && path.Equals(FailDownloadOf, StringComparison.OrdinalIgnoreCase))
                throw new LibraryException("Dropbox couldn’t send this file.", "provider_failed");
            if (TimeOutDownloadOf is not null && path.Equals(TimeOutDownloadOf, StringComparison.OrdinalIgnoreCase))
                // How HttpClient reports its own timeout, as opposed to a cancelled request.
                throw new TaskCanceledException("A task was canceled.", new TimeoutException());
            WhileDownloading?.Invoke();
            if (FailWriteOf is not null && path.Equals(FailWriteOf, StringComparison.OrdinalIgnoreCase))
                return new UnreadableStream();
            return new MemoryStream(_contents[path], writable: false);
        }
    }
}
