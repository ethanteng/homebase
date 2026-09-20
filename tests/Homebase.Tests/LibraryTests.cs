using System.Net;
using System.Net.Http.Json;
using Homebase.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Homebase.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _config;

    public LibraryTests()
    {
        Directory.CreateDirectory(_temporary);
        _root = Directory.CreateDirectory(Path.Combine(_temporary, "Library")).FullName;
        _config = Path.Combine(_temporary, "Config");
    }

    private TestApp CreateApp() => new(_config);
    private static HttpClient CreateClient(TestApp app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Homebase-Request", "1");
        return client;
    }
    private async Task Select(HttpClient client, string? path = null)
    {
        var response = await client.PutAsJsonAsync("/api/library", new { path = path ?? _root });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Setup_browse_download_and_restart_preserve_ordinary_files()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Documents"));
        var filePath = Path.Combine(_root, "hello & café #1.txt");
        await File.WriteAllTextAsync(filePath, "Home is here.");
        var modified = File.GetLastWriteTimeUtc(filePath);
        await File.WriteAllTextAsync(Path.Combine(_root, ".hidden"), "hidden");
        using (var app = CreateApp())
        using (var client = CreateClient(app))
        {
            var initial = await client.GetFromJsonAsync<LibraryState>("/api/library");
            Assert.Null(initial!.RootPath);
            Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/files")).StatusCode);
            await Select(client);
            Assert.True(File.Exists(Path.Combine(_config, "settings.json")));
            var listing = await client.GetFromJsonAsync<DirectoryListing>("/api/files");
            Assert.Equal(["Documents", "hello & café #1.txt"], listing!.Entries.Select(entry => entry.Name));
            Assert.True(File.Exists(Path.Combine(_root, ".homebase", "index.db")));
            var response = await client.GetAsync("/api/files/download?path=" + Uri.EscapeDataString("hello & café #1.txt"));
            Assert.Equal("Home is here.", await response.Content.ReadAsStringAsync());
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(modified, File.GetLastWriteTimeUtc(filePath));
        }
        using var restarted = CreateApp();
        using var restartedClient = CreateClient(restarted);
        var restored = await restartedClient.GetFromJsonAsync<LibraryState>("/api/library");
        Assert.Equal(PathPolicy.NormalizeRoot(_root), restored!.RootPath);
        Assert.Equal(2, (await restartedClient.GetFromJsonAsync<DirectoryListing>("/api/files"))!.Entries.Count);
    }

    [Fact]
    public async Task Refresh_reconciles_added_changed_deleted_files_in_sqlite()
    {
        using var app = CreateApp();
        using var client = CreateClient(app);
        await Select(client);
        await File.WriteAllTextAsync(Path.Combine(_root, "gone.txt"), "gone");
        await File.WriteAllTextAsync(Path.Combine(_root, "changed.txt"), "before");
        (await client.GetAsync("/api/files")).EnsureSuccessStatusCode();
        File.Delete(Path.Combine(_root, "gone.txt"));
        await File.WriteAllTextAsync(Path.Combine(_root, "changed.txt"), "after, now longer");
        await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "new");
        var listing = await client.GetFromJsonAsync<DirectoryListing>("/api/files");
        Assert.Equal(["changed.txt", "new.txt"], listing!.Entries.Select(entry => entry.Name));
        Assert.Equal(17, listing.Entries[0].Size);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, ".homebase", "index.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM entries ORDER BY name)";
        Assert.Equal("changed.txt,new.txt", command.ExecuteScalar());
    }

    [Fact]
    public async Task Index_is_recreated_if_removed_while_app_is_stopped()
    {
        using (var app = CreateApp())
        using (var client = CreateClient(app)) { await Select(client); }
        Directory.Delete(Path.Combine(_root, ".homebase"), recursive: true);
        using var restarted = CreateApp();
        using var nextClient = CreateClient(restarted);
        (await nextClient.GetAsync("/api/files")).EnsureSuccessStatusCode();
        Assert.True(File.Exists(Path.Combine(_root, ".homebase", "index.db")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Documents/../../outside")]
    [InlineData("/etc")]
    [InlineData(".homebase")]
    [InlineData("Documents/.homebase")]
    [InlineData("Documents\\..\\outside")]
    public async Task Browser_and_download_reject_escape_and_hidden_paths(string path)
    {
        using var app = CreateApp();
        using var client = CreateClient(app);
        await Select(client);
        foreach (var endpoint in new[] { "/api/files", "/api/files/download" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(endpoint + "?path=" + Uri.EscapeDataString(path))).StatusCode);
    }

    [Fact]
    public async Task Symbolic_links_to_files_and_folders_are_never_browsed_or_downloaded()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_temporary, "Outside")).FullName;
        var secret = Path.Combine(outside, "secret.txt");
        await File.WriteAllTextAsync(secret, "outside content");
        Directory.CreateSymbolicLink(Path.Combine(_root, "linked-folder"), outside);
        File.CreateSymbolicLink(Path.Combine(_root, "linked-file"), secret);
        File.CreateSymbolicLink(Path.Combine(_root, "broken-link"), Path.Combine(outside, "missing"));
        using var app = CreateApp();
        using var client = CreateClient(app);
        await Select(client);
        var listing = await client.GetFromJsonAsync<DirectoryListing>("/api/files");
        Assert.Empty(listing!.Entries);
        Assert.Equal(3, listing.SkippedCount);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/files?path=linked-folder")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/files/download?path=linked-folder%2Fsecret.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/files/download?path=linked-file")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metadata_symlinks_do_not_write_outside_root(bool databaseLink)
    {
        var outside = Directory.CreateDirectory(Path.Combine(_temporary, "Outside")).FullName;
        if (databaseLink)
        {
            Directory.CreateDirectory(Path.Combine(_root, ".homebase"));
            File.CreateSymbolicLink(Path.Combine(_root, ".homebase", "index.db"), Path.Combine(outside, "target.db"));
        }
        else Directory.CreateSymbolicLink(Path.Combine(_root, ".homebase"), outside);
        using var app = CreateApp();
        using var client = CreateClient(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/library", new { path = _root })).StatusCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Root_switch_is_atomic_and_failed_selection_keeps_previous_root()
    {
        using var app = CreateApp();
        using var client = CreateClient(app);
        await File.WriteAllTextAsync(Path.Combine(_root, "first.txt"), "first");
        await Select(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/library", new { path = Path.Combine(_temporary, "missing") })).StatusCode);
        Assert.Equal("first.txt", Assert.Single((await client.GetFromJsonAsync<DirectoryListing>("/api/files"))!.Entries).Name);
        var second = Directory.CreateDirectory(Path.Combine(_temporary, "Second")).FullName;
        await File.WriteAllTextAsync(Path.Combine(second, "second.txt"), "second");
        await Select(client, second);
        Assert.Equal("second.txt", Assert.Single((await client.GetFromJsonAsync<DirectoryListing>("/api/files"))!.Entries).Name);
        Assert.True(File.Exists(Path.Combine(_root, "first.txt")));
    }

    [Fact]
    public async Task Nested_and_empty_folders_work_and_missing_drive_can_be_replaced()
    {
        Directory.CreateDirectory(Path.Combine(_root, "A", "Empty"));
        using var app = CreateApp();
        using var client = CreateClient(app);
        await Select(client);
        var listing = await client.GetFromJsonAsync<DirectoryListing>("/api/files?path=A%2FEmpty");
        Assert.Equal("A/Empty", listing!.Path);
        Assert.Empty(listing.Entries);
        Directory.Move(_root, _root + "-offline");
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/files")).StatusCode);
        await Select(client, _root + "-offline");
        (await client.GetAsync("/api/files")).EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:9999")]
    [InlineData("null")]
    public async Task Cross_origin_requests_cannot_read_files_or_change_the_library(string origin)
    {
        using var app = CreateApp();
        using var client = CreateClient(app);
        await Select(client);
        client.DefaultRequestHeaders.Add("Origin", origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/files")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/library", new { path = _root })).StatusCode);
    }

    [Fact]
    public async Task Mutations_require_custom_header_and_host_must_be_loopback()
    {
        using var app = CreateApp();
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/library", new { path = _root })).StatusCode);
        client.DefaultRequestHeaders.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/library")).StatusCode);
    }

    public void Dispose() => Directory.Delete(_temporary, recursive: true);

    private sealed class TestApp(string configDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Homebase:ConfigDirectory"] = configDirectory }));
    }
}
