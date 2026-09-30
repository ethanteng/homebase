using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;

namespace Homebase.Tests;

/// <summary>
/// Folder sizes, which are shown without anybody asking for them: in My files, and in the places
/// files are brought in from. Each counts what the page around it shows, and nothing it can't reach.
/// </summary>
public sealed class SizeTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;
    private readonly string _source;
    private readonly StubDropbox _dropbox = new();

    public SizeTests()
    {
        Directory.CreateDirectory(_temporary);
        _host = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
        _source = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Camera")).FullName);
    }

    private async Task<(TestHost App, HttpClient Client, string Root)> StartAsync()
    {
        var app = new TestHost(_config, _ => _dropbox);
        var client = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(client, _host);
        return (app, client, await TestHost.UserRootAsync(client));
    }

    private static void Write(string root, string relative, string contents)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    [Fact]
    public async Task A_folder_in_My_files_counts_everything_under_it_that_browsing_shows()
    {
        var (app, client, root) = await StartAsync();
        using var _ = app;
        using var __ = client;
        Write(root, "Photos/a.jpg", "12345");
        Write(root, "Photos/Trips/2025/b.jpg", "123");
        Write(root, "Photos/.DS_Store", "hidden");
        Write(root, "Photos/.stversions/old.jpg", "an old version");
        Directory.CreateDirectory(Path.Combine(root, "Empty"));
        var outside = Directory.CreateDirectory(Path.Combine(_temporary, "Outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "big.bin"), new string('x', 1000));
        Directory.CreateSymbolicLink(Path.Combine(root, "Photos", "linked"), outside);

        Assert.Equal(new FolderSize(8, 2), await client.GetFromJsonAsync<FolderSize>("/api/files/size?path=Photos"));
        Assert.Equal(new FolderSize(3, 1), await client.GetFromJsonAsync<FolderSize>("/api/files/size?path=Photos%2FTrips"));
        Assert.Equal(new FolderSize(0, 0), await client.GetFromJsonAsync<FolderSize>("/api/files/size?path=Empty"));
        Assert.Equal(new FolderSize(5, 1), await client.GetFromJsonAsync<FolderSize>("/api/files/size?path=Photos%2Fa.jpg"));

        foreach (var refused in new[] { "", "../outside", ".homebase", "Photos/.stversions", "Photos/linked" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/files/size?path=" + Uri.EscapeDataString(refused))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/files/size?path=Gone")).StatusCode);
    }

    [Fact]
    public async Task A_folder_in_a_place_on_this_computer_counts_what_an_import_would_bring()
    {
        var (app, client, _) = await StartAsync();
        using var __ = app;
        using var ___ = client;
        Write(_source, "2025/March/one.jpg", "1234");
        Write(_source, "2025/two.jpg", "12");
        Write(_source, "2025/.hidden/three.jpg", "123456");
        var added = await client.PostAsJsonAsync("/api/host/places", new { path = _source, name = "Camera" });
        added.EnsureSuccessStatusCode();
        var place = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        Assert.Equal(new FolderSize(6, 2), await client.GetFromJsonAsync<FolderSize>($"/api/imports/sources/{place}/size?path=%2F2025"));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/imports/sources/{place}/size?path=" + Uri.EscapeDataString("/../Host"))).StatusCode);
    }

    [Fact]
    public async Task A_folder_online_is_measured_once_and_remembered_for_a_while()
    {
        var (app, client, _) = await StartAsync();
        using var __ = app;
        using var ___ = client;
        _dropbox.AddFolder("/work");
        _dropbox.Add("/work/report.txt", "rev1", "Report.");
        _dropbox.Add("/work/.secret", "rev1", "Hidden.");

        Assert.Equal(new FolderSize(7, 1), await client.GetFromJsonAsync<FolderSize>("/api/imports/sources/dropbox/size?path=%2Fwork"));
        // Stepping back out and in again asks Dropbox nothing new; a few minutes on, it would.
        _dropbox.Add("/work/later.txt", "rev1", "Later.");
        Assert.Equal(new FolderSize(7, 1), await client.GetFromJsonAsync<FolderSize>("/api/imports/sources/dropbox/size?path=%2Fwork"));
    }

    public void Dispose() => Directory.Delete(_temporary, recursive: true);
}
