using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
using Microsoft.Data.Sqlite;

namespace Homebase.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;

    public LibraryTests()
    {
        Directory.CreateDirectory(_temporary);
        // Resolved the way the host will resolve it: on macOS /var is a link into /private/var,
        // so a raw temporary path never equals the one the server answers with.
        _host = PathPolicy.NormalizeRoot(
            Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
    }

    private TestHost CreateApp() => new(_config);

    /// <summary>A host with its folder chosen, signed in, and the caller's own folder found.</summary>
    private async Task<(HttpClient Client, string Root)> StartAsync(TestHost app, string? hostRoot = null)
    {
        var client = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(client, hostRoot ?? _host);
        return (client, await TestHost.UserRootAsync(client));
    }

    private async Task<(HttpClient Client, string Root)> ReturnAsync(TestHost app)
    {
        var client = await app.SignInAsync("owner");
        return (client, await TestHost.UserRootAsync(client));
    }

    [Fact]
    public async Task Setup_browse_download_and_restart_preserve_ordinary_files()
    {
        string root;
        using (var app = CreateApp())
        {
            var client = await app.Anonymous().GetFromJsonAsync<JsonElement>("/api/session");
            Assert.True(client.GetProperty("setupNeeded").GetBoolean());

            (var signedIn, root) = await StartAsync(app);
            using var _ = signedIn;
            Directory.CreateDirectory(Path.Combine(root, "Documents"));
            var filePath = Path.Combine(root, "hello & café #1.txt");
            await File.WriteAllTextAsync(filePath, "Home is here.");
            var modified = File.GetLastWriteTimeUtc(filePath);
            await File.WriteAllTextAsync(Path.Combine(root, ".hidden"), "hidden");

            // Everyone's files live under the host's folder, named by account, never beside it.
            Assert.Equal(Path.Combine(_host, "users"), Path.GetDirectoryName(root));

            var listing = await signedIn.GetFromJsonAsync<DirectoryListing>("/api/files");
            Assert.Equal(["Documents", "hello & café #1.txt"], listing!.Entries.Select(entry => entry.Name));
            Assert.True(File.Exists(Path.Combine(root, ".homebase", "index.db")));
            var response = await signedIn.GetAsync("/api/files/download?path=" + Uri.EscapeDataString("hello & café #1.txt"));
            Assert.Equal("Home is here.", await response.Content.ReadAsStringAsync());
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(modified, File.GetLastWriteTimeUtc(filePath));
        }
        using var restarted = CreateApp();
        var (restoredClient, restoredRoot) = await ReturnAsync(restarted);
        using var __ = restoredClient;
        Assert.Equal(root, restoredRoot);
        Assert.Equal(2, (await restoredClient.GetFromJsonAsync<DirectoryListing>("/api/files"))!.Entries.Count);
    }

    [Fact]
    public async Task Refresh_reconciles_added_changed_deleted_files_in_sqlite()
    {
        using var app = CreateApp();
        var (client, root) = await StartAsync(app);
        using var _ = client;
        await File.WriteAllTextAsync(Path.Combine(root, "gone.txt"), "gone");
        await File.WriteAllTextAsync(Path.Combine(root, "changed.txt"), "before");
        (await client.GetAsync("/api/files")).EnsureSuccessStatusCode();
        File.Delete(Path.Combine(root, "gone.txt"));
        await File.WriteAllTextAsync(Path.Combine(root, "changed.txt"), "after, now longer");
        await File.WriteAllTextAsync(Path.Combine(root, "new.txt"), "new");
        var listing = await client.GetFromJsonAsync<DirectoryListing>("/api/files");
        Assert.Equal(["changed.txt", "new.txt"], listing!.Entries.Select(entry => entry.Name));
        Assert.Equal(17, listing.Entries[0].Size);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, ".homebase", "index.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM entries ORDER BY name)";
        Assert.Equal("changed.txt,new.txt", command.ExecuteScalar());
    }

    [Fact]
    public async Task Index_is_recreated_if_removed_while_app_is_stopped()
    {
        string root;
        using (var app = CreateApp())
        {
            (var client, root) = await StartAsync(app);
            client.Dispose();
        }
        Directory.Delete(Path.Combine(root, ".homebase"), recursive: true);
        using var restarted = CreateApp();
        var (nextClient, _) = await ReturnAsync(restarted);
        using var __ = nextClient;
        (await nextClient.GetAsync("/api/files")).EnsureSuccessStatusCode();
        Assert.True(File.Exists(Path.Combine(root, ".homebase", "index.db")));
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
        var (client, _) = await StartAsync(app);
        using var __ = client;
        foreach (var endpoint in new[] { "/api/files", "/api/files/download" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(endpoint + "?path=" + Uri.EscapeDataString(path))).StatusCode);
    }

    [Fact]
    public async Task Symbolic_links_to_files_and_folders_are_never_browsed_or_downloaded()
    {
        using var app = CreateApp();
        var (client, root) = await StartAsync(app);
        using var _ = client;
        var outside = Directory.CreateDirectory(Path.Combine(_temporary, "Outside")).FullName;
        var secret = Path.Combine(outside, "secret.txt");
        await File.WriteAllTextAsync(secret, "outside content");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked-folder"), outside);
        File.CreateSymbolicLink(Path.Combine(root, "linked-file"), secret);
        File.CreateSymbolicLink(Path.Combine(root, "broken-link"), Path.Combine(outside, "missing"));
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
    public async Task Metadata_symlinks_do_not_write_outside_an_account_folder(bool databaseLink)
    {
        var outside = Directory.CreateDirectory(Path.Combine(_temporary, "Outside")).FullName;
        string root;
        using (var first = CreateApp())
        {
            (var client, root) = await StartAsync(first);
            client.Dispose();
        }
        Directory.Delete(Path.Combine(root, ".homebase"), recursive: true);
        if (databaseLink)
        {
            Directory.CreateDirectory(Path.Combine(root, ".homebase"));
            File.CreateSymbolicLink(Path.Combine(root, ".homebase", "index.db"), Path.Combine(outside, "target.db"));
        }
        else Directory.CreateSymbolicLink(Path.Combine(root, ".homebase"), outside);

        using var app = CreateApp();
        using var returning = await app.SignInAsync("owner");
        Assert.Equal(HttpStatusCode.BadRequest, (await returning.GetAsync("/api/files")).StatusCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Host_folder_switch_moves_everyone_and_a_failed_switch_keeps_the_old_one()
    {
        using var app = CreateApp();
        var (client, root) = await StartAsync(app);
        using var _ = client;
        await File.WriteAllTextAsync(Path.Combine(root, "first.txt"), "first");
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync("/api/host", new { path = Path.Combine(_temporary, "missing") })).StatusCode);
        Assert.Equal("first.txt", Assert.Single((await client.GetFromJsonAsync<DirectoryListing>("/api/files"))!.Entries).Name);

        var second = PathPolicy.NormalizeRoot(
            Directory.CreateDirectory(Path.Combine(_temporary, "Second")).FullName);
        await TestHost.SetHostRootAsync(client, second);
        var moved = await TestHost.UserRootAsync(client);
        Assert.StartsWith(second, moved, StringComparison.Ordinal);
        Assert.Empty((await client.GetFromJsonAsync<DirectoryListing>("/api/files"))!.Entries);

        await TestHost.SetHostRootAsync(client, _host);
        Assert.Equal("first.txt", Assert.Single((await client.GetFromJsonAsync<DirectoryListing>("/api/files"))!.Entries).Name);
        Assert.True(File.Exists(Path.Combine(root, "first.txt")));
    }

    [Fact]
    public async Task Nested_and_empty_folders_work_and_missing_drive_can_be_replaced()
    {
        using var app = CreateApp();
        var (client, root) = await StartAsync(app);
        using var _ = client;
        Directory.CreateDirectory(Path.Combine(root, "A", "Empty"));
        var listing = await client.GetFromJsonAsync<DirectoryListing>("/api/files?path=A%2FEmpty");
        Assert.Equal("A/Empty", listing!.Path);
        Assert.Empty(listing.Entries);
        Directory.Move(_host, _host + "-offline");
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/files")).StatusCode);
        await TestHost.SetHostRootAsync(client, _host + "-offline");
        (await client.GetAsync("/api/files")).EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:9999")]
    [InlineData("null")]
    public async Task Cross_origin_requests_cannot_read_files_or_change_the_host(string origin)
    {
        using var app = CreateApp();
        var (client, _) = await StartAsync(app);
        using var __ = client;
        client.DefaultRequestHeaders.Add("Origin", origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/files")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/host", new { path = _host })).StatusCode);
    }

    [Fact]
    public async Task Mutations_require_custom_header_and_host_must_be_allowed()
    {
        using var app = CreateApp();
        var (signedIn, _) = await StartAsync(app);
        signedIn.Dispose();
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/host", new { path = _host })).StatusCode);
        client.DefaultRequestHeaders.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/library")).StatusCode);
    }

    public void Dispose() => Directory.Delete(_temporary, recursive: true);
}
