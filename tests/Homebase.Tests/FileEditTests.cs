using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;

namespace Homebase.Tests;

/// <summary>
/// Copying, moving and deleting in My files. Each of them writes to disk on a request's say-so, so
/// the same rules as browsing apply to every path they are handed — and on top of those, nothing is
/// ever overwritten and nothing Syncthing is keeping is pulled out from under it.
/// </summary>
public sealed class FileEditTests : IDisposable
{
    private const string Laptop = "ZZZZZZZ-YYYYYYY-XXXXXXX-WWWWWWW-VVVVVVV-UUUUUUU-TTTTTTT-SSSSSSS";

    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;
    private readonly FakeSyncthing _syncthing = new();

    public FileEditTests()
    {
        Directory.CreateDirectory(_temporary);
        _host = PathPolicy.NormalizeRoot(
            Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
    }

    private async Task<(TestHost App, HttpClient Client, string Root)> StartAsync()
    {
        var app = new TestHost(_config, syncthing: _syncthing);
        var client = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(client, _host);
        return (app, client, await TestHost.UserRootAsync(client));
    }

    private static Task<HttpResponseMessage> EditAsync(HttpClient client, string action, string[] paths, string? destination = null) =>
        client.PostAsJsonAsync($"/api/files/{action}", new { paths, destination });

    /// <summary>Where each item went, as the response reports it.</summary>
    private static async Task<(string From, string To)[]> ItemsAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("items").EnumerateArray()
            .Select(item => (item.GetProperty("from").GetString()!, item.GetProperty("to").GetString()!)).ToArray();
    }

    private static async Task WriteAsync(string root, string path, string content)
    {
        var fullPath = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
    }

    [Fact]
    public async Task Copy_leaves_the_original_and_never_overwrites_what_is_there()
    {
        var (app, client, root) = await StartAsync();
        using var _ = app;
        using var __ = client;
        await WriteAsync(root, "Notes.txt", "notes");
        var lastChanged = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(root, "Notes.txt"), lastChanged);
        await WriteAsync(root, "Photos/beach.jpg", "beach");
        await WriteAsync(root, "Photos/Trips/day.jpg", "day");
        await WriteAsync(root, "Photos/.DS_Store", "finder");
        var outside = Directory.CreateDirectory(Path.Combine(_temporary, "Outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "outside");
        Directory.CreateSymbolicLink(Path.Combine(root, "Photos", "linked"), outside);
        await WriteAsync(root, "Archive/Notes.txt", "older notes");

        var copied = await ItemsAsync(await EditAsync(client, "copy", ["Notes.txt", "Photos"], "Archive"));

        Assert.Equal([("Notes.txt", "Archive/Notes copy.txt"), ("Photos", "Archive/Photos")], copied);
        Assert.Equal("older notes", await File.ReadAllTextAsync(Path.Combine(root, "Archive", "Notes.txt")));
        Assert.Equal("notes", await File.ReadAllTextAsync(Path.Combine(root, "Archive", "Notes copy.txt")));
        Assert.Equal(lastChanged, File.GetLastWriteTimeUtc(Path.Combine(root, "Archive", "Notes copy.txt")));
        Assert.Equal("day", await File.ReadAllTextAsync(Path.Combine(root, "Archive", "Photos", "Trips", "day.jpg")));
        // What browsing never shows is left behind, and a link is never followed out of the folder.
        Assert.False(File.Exists(Path.Combine(root, "Archive", "Photos", ".DS_Store")));
        Assert.False(Path.Exists(Path.Combine(root, "Archive", "Photos", "linked")));
        Assert.Equal("notes", await File.ReadAllTextAsync(Path.Combine(root, "Notes.txt")));
        Assert.True(File.Exists(Path.Combine(root, "Photos", "beach.jpg")));

        // Copying into the folder it is already in makes a duplicate beside it, and another after that.
        Assert.Equal("Notes copy.txt", (await ItemsAsync(await EditAsync(client, "copy", ["Notes.txt"], ""))).Single().To);
        Assert.Equal("Notes copy 2.txt", (await ItemsAsync(await EditAsync(client, "copy", ["Notes.txt"], ""))).Single().To);
        // A copy is built out of sight and moved into place, and nothing of the building is left.
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, ".homebase"), "copy.*"));
    }

    [Fact]
    public async Task Move_takes_files_and_folders_elsewhere_and_keeps_both_when_a_name_is_taken()
    {
        var (app, client, root) = await StartAsync();
        using var _ = app;
        using var __ = client;
        await WriteAsync(root, "a.txt", "mine");
        await WriteAsync(root, "Docs/a.txt", "already there");
        await WriteAsync(root, "Folder/inner.txt", "inner");

        var moved = await ItemsAsync(await EditAsync(client, "move", ["a.txt", "Folder", "Folder/inner.txt"], "Docs"));

        // Something inside a folder that is also moving goes with the folder.
        Assert.Equal([("a.txt", "Docs/a 2.txt"), ("Folder", "Docs/Folder")], moved);
        Assert.Equal("already there", await File.ReadAllTextAsync(Path.Combine(root, "Docs", "a.txt")));
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(root, "Docs", "a 2.txt")));
        Assert.Equal("inner", await File.ReadAllTextAsync(Path.Combine(root, "Docs", "Folder", "inner.txt")));
        Assert.False(File.Exists(Path.Combine(root, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, "Folder")));

        // Where it already is, nothing happens at all.
        Assert.Empty(await ItemsAsync(await EditAsync(client, "move", ["Docs/a.txt"], "Docs")));
        Assert.Equal("already there", await File.ReadAllTextAsync(Path.Combine(root, "Docs", "a.txt")));

        // A folder can't go inside itself, and refusing one item moves none of the others.
        foreach (var into in new[] { "Docs", "Docs/Folder" })
            Assert.Equal(HttpStatusCode.BadRequest, (await EditAsync(client, "move", ["Docs/a 2.txt", "Docs"], into)).StatusCode);
        Assert.True(File.Exists(Path.Combine(root, "Docs", "a 2.txt")));
    }

    [Fact]
    public async Task Delete_removes_files_and_folders_with_everything_in_them()
    {
        var (app, client, root) = await StartAsync();
        using var _ = app;
        using var __ = client;
        await WriteAsync(root, "old.txt", "old");
        await WriteAsync(root, "Old stuff/deep/file.txt", "deep");
        await WriteAsync(root, "Old stuff/.hidden", "hidden");
        await WriteAsync(root, "keep.txt", "keep");
        var outside = Directory.CreateDirectory(Path.Combine(_temporary, "Outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "outside");
        Directory.CreateSymbolicLink(Path.Combine(root, "Old stuff", "linked"), outside);

        var response = await EditAsync(client, "delete", ["old.txt", "Old stuff", "Old stuff/deep/file.txt"]);

        response.EnsureSuccessStatusCode();
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deleted").GetInt32());
        Assert.False(File.Exists(Path.Combine(root, "old.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, "Old stuff")));
        // A link inside a deleted folder goes with it; what it pointed at does not.
        Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "secret.txt")));
        Assert.Equal("keep.txt", Assert.Single((await client.GetFromJsonAsync<DirectoryListing>("/api/files"))!.Entries).Name);
        Assert.True(File.Exists(Path.Combine(root, ".homebase", "index.db")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Docs/../../outside")]
    [InlineData("/etc")]
    [InlineData(".homebase")]
    [InlineData(".homebase/index.db")]
    [InlineData("Docs\\..\\outside")]
    [InlineData("")]
    [InlineData("linked")]
    [InlineData("linked/secret.txt")]
    public async Task Edits_refuse_the_paths_browsing_refuses(string path)
    {
        var (app, client, root) = await StartAsync();
        using var _ = app;
        using var __ = client;
        await WriteAsync(root, "Docs/a.txt", "a");
        var outside = Directory.CreateDirectory(Path.Combine(_temporary, "Outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "outside");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);

        Assert.Equal(HttpStatusCode.BadRequest, (await EditAsync(client, "copy", [path], "Docs")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EditAsync(client, "move", [path], "Docs")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EditAsync(client, "delete", [path])).StatusCode);
        if (path.Length > 0)
        {
            // Nor can anything be put there.
            Assert.Equal(HttpStatusCode.BadRequest, (await EditAsync(client, "copy", ["Docs/a.txt"], path)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await EditAsync(client, "move", ["Docs/a.txt"], path)).StatusCode);
        }

        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(root, "Docs", "a.txt")));
        Assert.Equal(["secret.txt"], Directory.EnumerateFileSystemEntries(outside).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(root, ".homebase", "index.db")));
    }

    [Fact]
    public async Task Missing_items_and_folders_are_reported_rather_than_made()
    {
        var (app, client, root) = await StartAsync();
        using var _ = app;
        using var __ = client;
        await WriteAsync(root, "a.txt", "a");

        Assert.Equal(HttpStatusCode.NotFound, (await EditAsync(client, "delete", ["gone.txt"])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EditAsync(client, "move", ["a.txt", "gone.txt"], "")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EditAsync(client, "move", ["a.txt"], "Nowhere")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EditAsync(client, "copy", ["a.txt"], "Nowhere")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EditAsync(client, "delete", [])).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(root, "Nowhere")));
        Assert.True(File.Exists(Path.Combine(root, "a.txt")));
    }

    [Fact]
    public async Task A_synced_folder_and_any_folder_holding_one_stay_where_Syncthing_expects_them()
    {
        var (app, client, root) = await StartAsync();
        using var _ = app;
        using var __ = client;
        await WriteAsync(root, "Work/Diaries/entry.txt", "entry");
        await WriteAsync(root, "Work/Diaries/draft.txt", "draft");
        Directory.CreateDirectory(Path.Combine(root, "Elsewhere"));
        (await client.PostAsJsonAsync("/api/sync/devices", new { deviceId = Laptop, name = "Laptop" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/sync/folders", new { path = "Work/Diaries" })).EnsureSuccessStatusCode();

        foreach (var path in new[] { "Work", "Work/Diaries" })
        {
            var moving = await EditAsync(client, "move", [path], "Elsewhere");
            Assert.Equal(HttpStatusCode.Conflict, moving.StatusCode);
            Assert.Contains("My computers", (await moving.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await EditAsync(client, "delete", [path])).StatusCode);
        }
        Assert.True(File.Exists(Path.Combine(root, "Work", "Diaries", "entry.txt")));

        // What is inside is still everyone's own to change, and a copy is just a folder.
        (await EditAsync(client, "delete", ["Work/Diaries/draft.txt"])).EnsureSuccessStatusCode();
        (await EditAsync(client, "move", ["Work/Diaries/entry.txt"], "Elsewhere")).EnsureSuccessStatusCode();
        Assert.Equal("Work copy", (await ItemsAsync(await EditAsync(client, "copy", ["Work"], ""))).Single().To);
        Assert.True(Directory.Exists(Path.Combine(root, "Work copy", "Diaries")));
        Assert.False(File.Exists(Path.Combine(root, "Work copy", "Diaries", ".stignore")));
    }

    public void Dispose() => Directory.Delete(_temporary, recursive: true);
}
