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
    public async Task A_dropbox_file_is_brought_home_and_shows_up_in_the_browser()
    {
        _dropbox.Add("/notes/hello.txt", "rev1", "First draft.");
        using var app = new TestApp(_config, _dropbox);
        using var client = CreateClient(app);
        (await client.PutAsJsonAsync("/api/library", new { path = _root })).EnsureSuccessStatusCode();

        Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>("/api/imports") ?? []);

        var job = await BringHome(client, "/notes/hello.txt");
        Assert.Equal("Done", job.GetProperty("stage").GetString());
        Assert.Equal(1, job.GetProperty("result").GetProperty("importedCount").GetInt32());

        var localFile = Path.Combine(_root, "Files", "Dropbox", "notes", "hello.txt");
        Assert.Equal("First draft.", await File.ReadAllTextAsync(localFile));

        // The import is an ordinary file, so the normal browser sees it.
        var listing = await client.GetFromJsonAsync<DirectoryListing>("/api/files?path=Files/Dropbox/notes");
        Assert.Equal("hello.txt", listing!.Entries.Single().Name);
        Assert.Single(await client.GetFromJsonAsync<JsonElement[]>("/api/imports") ?? []);
    }

    [Fact]
    public async Task A_later_dropbox_revision_leaves_the_imported_copy_alone()
    {
        _dropbox.Add("/notes/hello.txt", "rev1", "First draft.");
        using var app = new TestApp(_config, _dropbox);
        using var client = CreateClient(app);
        (await client.PutAsJsonAsync("/api/library", new { path = _root })).EnsureSuccessStatusCode();
        await BringHome(client, "/notes/hello.txt");

        _dropbox.Add("/notes/hello.txt", "rev2", "Changed on Dropbox.");
        var again = await BringHome(client, "/notes/hello.txt");

        Assert.Equal(0, again.GetProperty("result").GetProperty("importedCount").GetInt32());
        Assert.Equal("First draft.",
            await File.ReadAllTextAsync(Path.Combine(_root, "Files", "Dropbox", "notes", "hello.txt")));
    }

    [Fact]
    public async Task Importing_requires_a_chosen_folder_and_the_local_request_header()
    {
        _dropbox.Add("/notes/hello.txt", "rev1", "First draft.");
        using var app = new TestApp(_config, _dropbox);
        using var bare = app.CreateClient();
        using var client = CreateClient(app);

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/imports", new { remotePath = "/notes/hello.txt" })).StatusCode);

        (await client.PutAsJsonAsync("/api/library", new { path = _root })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await bare.PostAsJsonAsync("/api/imports", new { remotePath = "/notes/hello.txt" })).StatusCode);
    }

    [Fact]
    public async Task Starting_an_import_answers_before_the_files_have_arrived()
    {
        _dropbox.Add("/notes/hello.txt", "rev1", "First draft.");
        using var app = new TestApp(_config, _dropbox);
        using var client = CreateClient(app);
        (await client.PutAsJsonAsync("/api/library", new { path = _root })).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/imports",
            new { remotePath = "/notes/hello.txt", label = "hello.txt" });

        response.EnsureSuccessStatusCode();
        var started = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("job");
        // The work carries on past this response, which is the whole point of the job.
        Assert.True(started.GetProperty("running").GetBoolean());
        Assert.Equal("hello.txt", started.GetProperty("label").GetString());
        await Settled(client);
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

    [Fact]
    public void Syncthing_state_is_kept_where_preferences_are_not_in_the_temporary_directory()
    {
        // With no ConfigDirectory set — the ordinary installation — Syncthing's device identity
        // and pairings must not land somewhere a cleanup can take them.
        using var app = new DefaultDirectoryApp();
        using var client = app.CreateClient();

        var home = app.Services.GetRequiredService<Homebase.Server.SyncthingHost>().Home;

        Assert.False(home.StartsWith(Path.GetTempPath(), StringComparison.Ordinal),
            $"Syncthing state would be lost from {home}");
        Assert.EndsWith("syncthing", home);
    }

    /// <summary>Starts an import the way the panel does, and waits for the job behind it to settle.</summary>
    private static async Task<JsonElement> BringHome(HttpClient client, string remotePath)
    {
        (await client.PostAsJsonAsync("/api/imports", new { remotePath })).EnsureSuccessStatusCode();
        return await Settled(client);
    }

    private static async Task<JsonElement> Settled(HttpClient client)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = (await client.GetFromJsonAsync<JsonElement>("/api/imports/job")).GetProperty("job");
            if (!job.GetProperty("running").GetBoolean()) return job;
            await Task.Delay(15);
        }
        throw new TimeoutException("The import never finished.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }

    /// <summary>The app as installed: no configuration overrides, and Syncthing not started.</summary>
    private sealed class DefaultDirectoryApp : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Homebase:Syncthing:Enabled"] = "false" }));
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
