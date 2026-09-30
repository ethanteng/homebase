using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private static Task<HttpResponseMessage> RenameAsync(HttpClient client, string path, string? name) =>
        client.PostAsJsonAsync("/api/files/rename", new { path, name });

    private static async Task<BinListing> BinAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<BinListing>("/api/bin"))!;

    private static Task<HttpResponseMessage> BinAsync(HttpClient client, string action, params string[] ids) =>
        client.PostAsJsonAsync($"/api/bin/{action}", new { ids });

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
    public async Task A_name_at_the_length_limit_makes_room_for_copy_or_a_number_rather_than_failing()
    {
        var (app, client, root) = await StartAsync();
        using var _ = app;
        using var __ = client;
        // 255 bytes each, the most a drive allows: one in plain letters, one in two-byte ones.
        var plain = new string('x', 251) + ".txt";
        var accented = new string('é', 125) + "x.txt";
        foreach (var name in new[] { plain, accented })
        {
            await WriteAsync(root, name, "mine");
            await WriteAsync(root, $"Docs/{name}", "already there");
        }

        var copied = (await ItemsAsync(await EditAsync(client, "copy", [plain, accented], ""))).Select(item => item.To).ToArray();
        var moved = (await ItemsAsync(await EditAsync(client, "move", [plain, accented], "Docs"))).Select(item => Path.GetFileName(item.To)).ToArray();

        foreach (var (name, ending) in copied.Select(name => (name, " copy.txt")).Concat(moved.Select(name => (name, " 2.txt"))))
        {
            Assert.EndsWith(ending, name);
            Assert.InRange(Encoding.UTF8.GetByteCount(name), ending.Length + 1, 255);
            // Shortened between characters, never through one.
            Assert.DoesNotContain('\uFFFD', name);
        }
        Assert.StartsWith("éé", moved[1]);
        Assert.Equal("already there", await File.ReadAllTextAsync(Path.Combine(root, "Docs", plain)));
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(root, "Docs", moved[0])));
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(root, copied[1])));
    }

    [Fact]
    public async Task Rename_changes_only_the_name_and_never_takes_one_already_in_use()
    {
        var (app, client, root) = await StartAsync();
        using var _ = app;
        using var __ = client;
        await WriteAsync(root, "Docs/draft.txt", "draft");
        await WriteAsync(root, "Docs/final.txt", "final");
        await WriteAsync(root, "Docs/Old/inner.txt", "inner");

        async Task<(string From, string To)> Renamed(string path, string name)
        {
            var response = await RenameAsync(client, path, name);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (body.GetProperty("from").GetString()!, body.GetProperty("to").GetString()!);
        }

        // Spaces either side of what was typed aren't part of the name.
        Assert.Equal(("Docs/draft.txt", "Docs/Chapter one.txt"), await Renamed("Docs/draft.txt", " Chapter one.txt "));
        Assert.Equal(("Docs/Old", "Docs/Archive"), await Renamed("Docs/Old", "Archive"));
        Assert.Equal("draft", await File.ReadAllTextAsync(Path.Combine(root, "Docs", "Chapter one.txt")));
        Assert.Equal("inner", await File.ReadAllTextAsync(Path.Combine(root, "Docs", "Archive", "inner.txt")));
        Assert.False(File.Exists(Path.Combine(root, "Docs", "draft.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, "Docs", "Old")));

        // Only the capitals changing is a rename too, even on a drive that doesn't tell them apart.
        Assert.Equal(("Docs/final.txt", "Docs/Final.txt"), await Renamed("Docs/final.txt", "Final.txt"));
        Assert.Contains("Final.txt", (await client.GetFromJsonAsync<DirectoryListing>("/api/files?path=Docs"))!
            .Entries.Select(entry => entry.Name));
        Assert.Equal(("Docs/Final.txt", "Docs/Final.txt"), await Renamed("Docs/Final.txt", "Final.txt"));

        // A name already there is refused, not changed: the name is the whole point of a rename.
        var taken = await RenameAsync(client, "Docs/Final.txt", "Chapter one.txt");
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("already something called", (await taken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal("final", await File.ReadAllTextAsync(Path.Combine(root, "Docs", "Final.txt")));
        Assert.Equal("draft", await File.ReadAllTextAsync(Path.Combine(root, "Docs", "Chapter one.txt")));

        foreach (var name in new[] { "", "   ", ".hidden", "..", "a/b", "a\\b", "a:b", "tab\there", new string('x', 256), null })
            Assert.Equal(HttpStatusCode.BadRequest, (await RenameAsync(client, "Docs/Final.txt", name)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RenameAsync(client, "Docs/gone.txt", "anything")).StatusCode);
        Assert.Equal(["Archive", "Chapter one.txt", "Final.txt"],
            (await client.GetFromJsonAsync<DirectoryListing>("/api/files?path=Docs"))!.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public async Task Delete_moves_files_and_folders_with_everything_in_them_to_the_bin()
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
        Assert.Equal("keep.txt", Assert.Single((await client.GetFromJsonAsync<DirectoryListing>("/api/files"))!.Entries).Name);
        var bin = await BinAsync(client);
        Assert.Equal(["Old stuff", "old.txt"], bin.Entries.Select(entry => entry.Path).Order(StringComparer.Ordinal));
        // Everything in the folder counts, hidden or not, since all of it still takes up room.
        Assert.Equal(4 + 6 + 3, bin.Bytes);
        // The bin is Uncloud's own and can't be reached as a path.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/files?path=.homebase%2Fbin")).StatusCode);

        // Emptying it is for good. A link inside a deleted folder goes with it; what it pointed at does not.
        var emptied = await client.PostAsync("/api/bin/empty", null);
        emptied.EnsureSuccessStatusCode();
        Assert.Equal(2, (await emptied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deleted").GetInt32());
        Assert.Empty((await BinAsync(client)).Entries);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, ".homebase", "bin")));
        Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "secret.txt")));
        Assert.True(File.Exists(Path.Combine(root, ".homebase", "index.db")));
    }

    [Fact]
    public async Task Putting_things_back_returns_them_where_they_were_and_keeps_whatever_took_their_place()
    {
        var (app, client, root) = await StartAsync();
        using var _ = app;
        using var __ = client;
        await WriteAsync(root, "Documents/Taxes/2025.pdf", "taxes");
        await WriteAsync(root, "Old/deep/file.txt", "deep");
        (await EditAsync(client, "delete", ["Documents/Taxes/2025.pdf", "Old"])).EnsureSuccessStatusCode();
        var bin = await BinAsync(client);
        var taxes = bin.Entries.Single(entry => entry.Name == "2025.pdf");
        Assert.Equal("Documents/Taxes/2025.pdf", taxes.Path);
        Assert.False(taxes.IsDirectory);
        Assert.Equal(5, taxes.Size);
        Assert.Equal(LibraryService.KeepDeletedFor, taxes.ExpiresAt - taxes.DeletedAt);
        // The folders it was in have gone since, and something else is called Old now.
        Directory.Delete(Path.Combine(root, "Documents"), recursive: true);
        await WriteAsync(root, "Old/new.txt", "new");

        var restored = await ItemsAsync(await BinAsync(client, "restore", [.. bin.Entries.Select(entry => entry.Id)]));

        Assert.Equal([("Documents/Taxes/2025.pdf", "Documents/Taxes/2025.pdf"), ("Old", "Old 2")],
            restored.OrderBy(item => item.From, StringComparer.Ordinal));
        Assert.Equal("taxes", await File.ReadAllTextAsync(Path.Combine(root, "Documents", "Taxes", "2025.pdf")));
        Assert.Equal("deep", await File.ReadAllTextAsync(Path.Combine(root, "Old 2", "deep", "file.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(root, "Old", "new.txt")));
        Assert.Empty((await BinAsync(client)).Entries);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, ".homebase", "bin")));
    }

    [Fact]
    public async Task The_bin_lets_go_of_things_when_asked_or_once_their_time_is_up()
    {
        var (app, client, root) = await StartAsync();
        using var _ = app;
        using var __ = client;
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) await WriteAsync(root, name, name);
        (await EditAsync(client, "delete", ["a.txt", "b.txt", "c.txt"])).EnsureSuccessStatusCode();
        var ids = (await BinAsync(client)).Entries.ToDictionary(entry => entry.Name, entry => entry.Id);

        var deleted = await BinAsync(client, "delete", ids["a.txt"]);
        deleted.EnsureSuccessStatusCode();
        Assert.Equal(1, (await deleted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deleted").GetInt32());
        Assert.Equal(["b.txt", "c.txt"], (await BinAsync(client)).Entries.Select(entry => entry.Name).Order(StringComparer.Ordinal));

        // A request names things in the bin by id and nothing else.
        Assert.Equal(HttpStatusCode.NotFound, (await BinAsync(client, "restore", ids["a.txt"])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await BinAsync(client, "restore", "../../b.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await BinAsync(client, "delete", "*")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await BinAsync(client, "restore")).StatusCode);

        // Thirty days on, b.txt goes for good the next time anybody looks.
        var record = Path.Combine(root, ".homebase", "bin", $"{ids["b.txt"]}.json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(record))!;
        node["deletedAt"] = DateTimeOffset.UtcNow - LibraryService.KeepDeletedFor - TimeSpan.FromMinutes(1);
        await File.WriteAllTextAsync(record, node.ToJsonString());

        Assert.Equal("c.txt", Assert.Single((await BinAsync(client)).Entries).Name);
        Assert.False(Directory.Exists(Path.Combine(root, ".homebase", "bin", ids["b.txt"])));
        Assert.False(File.Exists(record));
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
        Assert.Equal(HttpStatusCode.BadRequest, (await RenameAsync(client, path, "renamed")).StatusCode);
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
            Assert.Equal(HttpStatusCode.Conflict, (await RenameAsync(client, path, "Renamed")).StatusCode);
        }
        Assert.True(File.Exists(Path.Combine(root, "Work", "Diaries", "entry.txt")));

        // What is inside is still everyone's own to change, and a copy is just a folder.
        (await EditAsync(client, "delete", ["Work/Diaries/draft.txt"])).EnsureSuccessStatusCode();
        (await EditAsync(client, "move", ["Work/Diaries/entry.txt"], "Elsewhere")).EnsureSuccessStatusCode();
        Assert.Equal("Work copy", (await ItemsAsync(await EditAsync(client, "copy", ["Work"], ""))).Single().To);
        Assert.True(Directory.Exists(Path.Combine(root, "Work copy", "Diaries")));
        Assert.False(File.Exists(Path.Combine(root, "Work copy", "Diaries", ".stignore")));
    }

    [Fact]
    public async Task The_folder_an_import_is_still_writing_into_is_left_alone_until_it_finishes()
    {
        var dropbox = new StubDropbox { Gated = true };
        dropbox.AddFolder("/work");
        dropbox.Add("/work/first.txt", "rev1", "First.");
        using var app = new TestHost(_config, _ => dropbox);
        using var client = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(client, _host);
        var root = await TestHost.UserRootAsync(client);
        await WriteAsync(root, "Elsewhere/mine.txt", "mine");
        await WriteAsync(root, "Dropbox/old.txt", "old");
        (await EditAsync(client, "delete", ["Dropbox/old.txt"])).EnsureSuccessStatusCode();
        var binned = Assert.Single((await BinAsync(client)).Entries).Id;

        (await client.PostAsJsonAsync("/api/imports", new { remotePath = "/work" })).EnsureSuccessStatusCode();
        await dropbox.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The import makes the folders it needs as it goes, so a move or delete here would be put
        // back file by file, and a copy would catch it half full.
        Assert.Equal(HttpStatusCode.Conflict, (await EditAsync(client, "move", ["Dropbox"], "Elsewhere")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EditAsync(client, "delete", ["Dropbox/work"])).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EditAsync(client, "copy", ["Dropbox"], "")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EditAsync(client, "move", ["Elsewhere/mine.txt"], "Dropbox/work")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await BinAsync(client, "restore", binned)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await RenameAsync(client, "Dropbox", "Old Dropbox")).StatusCode);
        // Everywhere else is still anybody's to change.
        (await EditAsync(client, "move", ["Elsewhere/mine.txt"], "")).EnsureSuccessStatusCode();

        dropbox.Gate.TrySetResult();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while ((await client.GetFromJsonAsync<JsonElement>("/api/imports/job")).GetProperty("job").GetProperty("running").GetBoolean())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The import never finished.");
            await Task.Delay(15);
        }

        (await BinAsync(client, "restore", binned)).EnsureSuccessStatusCode();
        Assert.Equal([("Dropbox", "Elsewhere/Dropbox")], await ItemsAsync(await EditAsync(client, "move", ["Dropbox"], "Elsewhere")));
        Assert.Equal("First.", await File.ReadAllTextAsync(Path.Combine(root, "Elsewhere", "Dropbox", "work", "first.txt")));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(root, "Elsewhere", "Dropbox", "old.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, "Dropbox")));
    }

    public void Dispose() => Directory.Delete(_temporary, recursive: true);
}
