using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Providers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Homebase.Tests;

public sealed class ProviderTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _config;
    private readonly StubDropbox _dropbox = new();

    public ProviderTests()
    {
        Directory.CreateDirectory(_temporary);
        _root = Directory.CreateDirectory(Path.Combine(_temporary, "Library")).FullName;
        _config = Path.Combine(_temporary, "Config");
    }

    private HttpClient CreateClient(TestApp app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Homebase-Request", "1");
        return client;
    }

    [Fact]
    public async Task A_dropbox_file_is_brought_home_and_follows_later_changes()
    {
        _dropbox.Add("/notes/hello.txt", "rev1", "First draft.");
        using var app = new TestApp(_config, _dropbox);
        using var client = CreateClient(app);
        (await client.PutAsJsonAsync("/api/library", new { path = _root })).EnsureSuccessStatusCode();

        Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>("/api/sync") ?? []);

        var tracked = await client.PostAsJsonAsync("/api/sync", new { remotePath = "/notes/hello.txt" });
        tracked.EnsureSuccessStatusCode();
        var localFile = Path.Combine(_root, "Files", "Dropbox", "notes", "hello.txt");
        Assert.Equal("First draft.", await File.ReadAllTextAsync(localFile));

        // The state crosses the wire as a name, not an enum's number.
        var status = await client.GetStringAsync("/api/sync");
        Assert.Contains("\"state\":\"current\"", status);

        // The file is an ordinary one, so the normal browser sees it.
        var listing = await client.GetFromJsonAsync<DirectoryListing>("/api/files?path=Files/Dropbox/notes");
        Assert.Equal("hello.txt", listing!.Entries.Single().Name);

        _dropbox.Add("/notes/hello.txt", "rev2", "Changed on Dropbox.");
        Assert.Contains("\"state\":\"remoteChanged\"", await client.GetStringAsync("/api/sync"));

        var refreshed = await client.PostAsync("/api/sync/refresh", null);
        refreshed.EnsureSuccessStatusCode();
        Assert.Contains("\"downloaded\":true", await refreshed.Content.ReadAsStringAsync());
        Assert.Equal("Changed on Dropbox.", await File.ReadAllTextAsync(localFile));
        Assert.Contains("\"state\":\"current\"", await client.GetStringAsync("/api/sync"));
    }

    [Fact]
    public async Task A_copy_changed_here_is_kept_and_reported()
    {
        _dropbox.Add("/notes/hello.txt", "rev1", "First draft.");
        using var app = new TestApp(_config, _dropbox);
        using var client = CreateClient(app);
        (await client.PutAsJsonAsync("/api/library", new { path = _root })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/sync", new { remotePath = "/notes/hello.txt" })).EnsureSuccessStatusCode();

        var localFile = Path.Combine(_root, "Files", "Dropbox", "notes", "hello.txt");
        await File.WriteAllTextAsync(localFile, "My own words.");
        File.SetLastWriteTimeUtc(localFile, DateTime.UtcNow.AddMinutes(5));
        _dropbox.Add("/notes/hello.txt", "rev2", "Changed on Dropbox.");

        (await client.PostAsync("/api/sync/refresh", null)).EnsureSuccessStatusCode();

        Assert.Equal("My own words.", await File.ReadAllTextAsync(localFile));
        Assert.Contains("\"state\":\"localEdited\"", await client.GetStringAsync("/api/sync"));
    }

    [Fact]
    public async Task Sync_requires_a_chosen_folder_and_the_local_request_header()
    {
        _dropbox.Add("/notes/hello.txt", "rev1", "First draft.");
        using var app = new TestApp(_config, _dropbox);
        using var bare = app.CreateClient();
        using var client = CreateClient(app);

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/sync", new { remotePath = "/notes/hello.txt" })).StatusCode);

        (await client.PutAsJsonAsync("/api/library", new { path = _root })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await bare.PostAsJsonAsync("/api/sync", new { remotePath = "/notes/hello.txt" })).StatusCode);
    }

    [Fact]
    public async Task Dropbox_reports_itself_unconfigured_without_an_app_key()
    {
        using var app = new TestApp(_config, _dropbox);
        using var client = CreateClient(app);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/providers/dropbox");

        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.False(status.GetProperty("connected").GetBoolean());
        // Connecting is refused rather than sending the browser to a half-built Dropbox URL.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/providers/dropbox/connect", null)).StatusCode);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }

    private sealed class TestApp(string configDirectory, IDropboxApi dropbox) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Homebase:ConfigDirectory"] = configDirectory }));
            builder.ConfigureTestServices(services => services.AddSingleton(dropbox));
        }
    }

    private sealed class StubDropbox : IDropboxApi
    {
        private readonly Dictionary<string, (DropboxEntry Entry, byte[] Content)> _files = new(StringComparer.OrdinalIgnoreCase);

        public bool IsConfigured => true;
        public bool IsConnected => true;

        public void Add(string path, string rev, string contents) =>
            _files[path] = (new DropboxEntry(path, Path.GetFileName(path), path.ToLowerInvariant(), path,
                false, Encoding.UTF8.GetByteCount(contents), rev, DateTimeOffset.UtcNow), Encoding.UTF8.GetBytes(contents));

        private (DropboxEntry Entry, byte[] Content) Require(string path) =>
            _files.TryGetValue(path, out var file) ? file : throw new LibraryException("No such file on Dropbox.", "not_found");

        public Task<DropboxAccount> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DropboxAccount("id", "Stub", null));
        public Task<IReadOnlyList<DropboxEntry>> ListFolderAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DropboxEntry>>(_files.Values.Select(file => file.Entry).ToArray());
        public Task<DropboxEntry> GetMetadataAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(Require(path).Entry);
        public Task<Stream> DownloadAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(Require(path).Content, writable: false));
    }
}
