using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;

namespace Homebase.Tests;

/// <summary>
/// Sharing by link. A link is opened without an account, so everything about it is a door that
/// stays open to whoever has the address: it must lead only to what its owner chose, show only what
/// can't run on Uncloud's address, keep downloads to links that allow them, and stop the moment its
/// owner turns it off or can't sign in any more.
/// </summary>
public sealed class ShareTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;

    public ShareTests()
    {
        Directory.CreateDirectory(_temporary);
        _host = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
    }

    private async Task<(TestHost App, HttpClient Owner, string Root)> StartAsync(IDictionary<string, string?>? settings = null)
    {
        var app = new TestHost(_config, settings: settings);
        var owner = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(owner, _host);
        return (app, owner, await TestHost.UserRootAsync(owner));
    }

    private static async Task WriteAsync(string root, string path, string content)
    {
        var fullPath = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
    }

    private static async Task<JsonElement> ShareAsync(HttpClient client, bool canDownload, params string[] paths)
    {
        var response = await client.PostAsJsonAsync("/api/shares", new { paths, canDownload });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>What comes after /s/ in a link's address.</summary>
    private static string TokenOf(JsonElement link)
    {
        var url = link.GetProperty("url").GetString()!;
        return url[(url.IndexOf("/s/", StringComparison.Ordinal) + 3)..];
    }

    private static async Task<string[]> NamesAsync(HttpClient visitor, string token, string path)
    {
        var listing = await visitor.GetFromJsonAsync<JsonElement>($"/api/shared/{token}/files?path={Uri.EscapeDataString(path)}");
        return listing.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<string[]> ZipNamesAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        return zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task A_view_only_link_shows_a_folder_to_anyone_and_lets_nothing_be_downloaded()
    {
        var (app, owner, root) = await StartAsync();
        using var _ = app;
        await File.WriteAllBytesAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "Photos")).FullName, "beach.png"), [0x89, 0x50, 0x4E, 0x47]);
        await WriteAsync(root, "Photos/notes.txt", "hello");
        await WriteAsync(root, "Photos/page.html", "<script>alert(1)</script>");
        await WriteAsync(root, "Photos/.hidden", "never shown");
        await WriteAsync(root, "Photos/Trips/day.jpg", "day");
        await WriteAsync(root, "Private/diary.txt", "mine");

        var link = await ShareAsync(owner, canDownload: false, "Photos");
        var token = TokenOf(link);
        Assert.StartsWith("http://localhost/s/", link.GetProperty("url").GetString());
        Assert.False(link.GetProperty("canDownload").GetBoolean());

        // Nobody signed in, and nothing but a plain GET.
        using var visitor = app.CreateClient();
        var shared = await visitor.GetFromJsonAsync<JsonElement>($"/api/shared/{token}");
        Assert.Equal("owner", shared.GetProperty("sharedBy").GetString());
        Assert.False(shared.GetProperty("canDownload").GetBoolean());
        var top = shared.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Photos", top.GetProperty("name").GetString());
        Assert.Equal("Photos", top.GetProperty("path").GetString());
        Assert.True(top.GetProperty("isDirectory").GetBoolean());

        var listing = await visitor.GetFromJsonAsync<JsonElement>($"/api/shared/{token}/files?path=Photos");
        var entries = listing.GetProperty("entries").EnumerateArray().ToDictionary(
            entry => entry.GetProperty("name").GetString()!,
            entry => entry.GetProperty("preview").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("preview").GetString());
        Assert.Equal(["Trips", "beach.png", "notes.txt", "page.html"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("image", entries["beach.png"]);
        Assert.Equal("text", entries["notes.txt"]);
        Assert.Null(entries["page.html"]);
        Assert.Equal(["day.jpg"], await NamesAsync(visitor, token, "Photos/Trips"));

        // A picture is shown, inline, under a policy that lets nothing in it run.
        var picture = await visitor.GetAsync($"/api/shared/{token}/file?path=Photos/beach.png");
        picture.EnsureSuccessStatusCode();
        Assert.Equal("image/png", picture.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", picture.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("sandbox", picture.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", picture.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("noindex", picture.Headers.GetValues("X-Robots-Tag").Single());
        var text = await visitor.GetAsync($"/api/shared/{token}/file?path=Photos/notes.txt");
        Assert.Equal("text/plain", text.Content.Headers.ContentType?.MediaType);
        Assert.Equal("hello", await text.Content.ReadAsStringAsync());
        // A page is never shown on Uncloud's address, whoever shared it.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await visitor.GetAsync($"/api/shared/{token}/file?path=Photos/page.html")).StatusCode);

        // Look, don't download: neither a file nor the folder as a zip.
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync($"/api/shared/{token}/download?path=Photos/notes.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync($"/api/shared/{token}/zip?path=Photos")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync($"/api/shared/{token}/zip")).StatusCode);
    }

    [Fact]
    public async Task A_link_leads_to_nothing_beside_or_above_what_it_shares()
    {
        var (app, owner, root) = await StartAsync();
        using var _ = app;
        await WriteAsync(root, "Photos/beach.jpg", "beach");
        await WriteAsync(root, "Photos/.hidden.txt", "hidden");
        await WriteAsync(root, "Private/diary.txt", "mine");
        var outside = Directory.CreateDirectory(Path.Combine(_temporary, "Outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "outside");
        Directory.CreateSymbolicLink(Path.Combine(root, "Photos", "linked"), outside);
        var token = TokenOf(await ShareAsync(owner, canDownload: true, "Photos"));
        using var visitor = app.CreateClient();

        // The link's top is what it shares, and nothing else in the folder it came from.
        Assert.Equal(["Photos"], await NamesAsync(visitor, token, ""));
        Assert.Equal(["beach.jpg"], await NamesAsync(visitor, token, "Photos"));
        foreach (var path in new[]
                 {
                     "Private", "Private/diary.txt", "Photos/../Private/diary.txt", "Photos/%2e%2e/Private/diary.txt",
                     "/Private/diary.txt", "Photos/.hidden.txt", "Photos/linked/secret.txt", "Photos\\..\\Private",
                     "../Private/diary.txt", ".homebase/index.db"
                 })
        {
            Assert.False((await visitor.GetAsync($"/api/shared/{token}/download?path={path}")).IsSuccessStatusCode, path);
            Assert.False((await visitor.GetAsync($"/api/shared/{token}/file?path={path}")).IsSuccessStatusCode, path);
            Assert.False((await visitor.GetAsync($"/api/shared/{token}/files?path={path}")).IsSuccessStatusCode, path);
            Assert.False((await visitor.GetAsync($"/api/shared/{token}/zip?path={path}")).IsSuccessStatusCode, path);
        }
        var everything = await ZipNamesAsync(await visitor.GetAsync($"/api/shared/{token}/zip"));
        Assert.Equal(["Photos/beach.jpg"], everything);

        // An address that isn't a link, or is the shape of one and isn't, says the same as one turned off.
        foreach (var made in new[] { "nonsense", new string('A', 43), token[..^1] })
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/shared/{made}")).StatusCode);
    }

    [Fact]
    public async Task A_link_that_allows_downloading_gives_files_and_folders_as_zips()
    {
        var (app, owner, root) = await StartAsync();
        using var _ = app;
        await WriteAsync(root, "Trip/beach.jpg", "beach");
        await WriteAsync(root, "Trip/Day 2/hike.jpg", "hike");
        await WriteAsync(root, "Trip/.DS_Store", "finder");
        Directory.CreateDirectory(Path.Combine(root, "Trip", "Empty"));
        await WriteAsync(root, "notes.txt", "notes");
        var link = await ShareAsync(owner, canDownload: true, "Trip", "notes.txt");
        var token = TokenOf(link);
        using var visitor = app.CreateClient();

        var file = await visitor.GetAsync($"/api/shared/{token}/download?path=notes.txt");
        file.EnsureSuccessStatusCode();
        // Downloaded, not shown, like any download from Uncloud.
        Assert.Equal("application/octet-stream", file.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", file.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("notes", await file.Content.ReadAsStringAsync());

        Assert.Equal(["Trip", "notes.txt"], await NamesAsync(visitor, token, ""));
        Assert.Equal(
            ["Trip/Day 2/hike.jpg", "Trip/Empty/", "Trip/beach.jpg", "notes.txt"],
            await ZipNamesAsync(await visitor.GetAsync($"/api/shared/{token}/zip")));
        var folder = await visitor.GetAsync($"/api/shared/{token}/zip?path=Trip/Day%202");
        Assert.Equal("Day 2.zip", folder.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(["Day 2/hike.jpg"], await ZipNamesAsync(folder));
    }

    [Fact]
    public async Task Only_whoever_made_a_link_sees_it_changes_it_or_turns_it_off()
    {
        var (app, owner, root) = await StartAsync();
        using var _ = app;
        await WriteAsync(root, "notes.txt", "notes");
        var link = await ShareAsync(owner, canDownload: false, "notes.txt");
        var id = link.GetProperty("id").GetString()!;
        var token = TokenOf(link);
        using var member = await app.AddUserAsync(owner, "member");
        using var visitor = app.CreateClient();

        Assert.Empty((await member.GetFromJsonAsync<JsonElement>("/api/shares")).GetProperty("links").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await member.PatchAsJsonAsync($"/api/shares/{id}", new { canDownload = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.DeleteAsync($"/api/shares/{id}")).StatusCode);
        // Somebody else's files can't be shared by naming them: a path is only ever inside your own folder.
        Assert.False((await member.PostAsJsonAsync("/api/shares", new { paths = new[] { "notes.txt" }, canDownload = true })).IsSuccessStatusCode);
        // Nor is making, seeing or changing links possible without signing in.
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Anonymous().GetAsync("/api/shares")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.Anonymous().PostAsJsonAsync("/api/shares", new { paths = new[] { "notes.txt" }, canDownload = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync($"/api/shared/{token}/download?path=notes.txt")).StatusCode);

        var changed = await owner.PatchAsJsonAsync($"/api/shares/{id}", new { canDownload = true });
        changed.EnsureSuccessStatusCode();
        Assert.True((await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("canDownload").GetBoolean());
        Assert.Equal("notes", await visitor.GetStringAsync($"/api/shared/{token}/download?path=notes.txt"));

        (await owner.DeleteAsync($"/api/shares/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/shared/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/shared/{token}/download?path=notes.txt")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>("/api/shares")).GetProperty("links").EnumerateArray());
    }

    [Fact]
    public async Task Sharing_the_same_things_again_is_the_same_link()
    {
        var (app, owner, root) = await StartAsync();
        using var _ = app;
        await WriteAsync(root, "a.txt", "a");
        await WriteAsync(root, "b.txt", "b");

        var first = await ShareAsync(owner, canDownload: false, "a.txt", "b.txt");
        var again = await ShareAsync(owner, canDownload: true, "b.txt", "a.txt");
        var other = await ShareAsync(owner, canDownload: false, "a.txt");

        Assert.Equal(first.GetProperty("url").GetString(), again.GetProperty("url").GetString());
        Assert.True(again.GetProperty("canDownload").GetBoolean());
        Assert.NotEqual(first.GetProperty("url").GetString(), other.GetProperty("url").GetString());
        Assert.Equal(2, (await owner.GetFromJsonAsync<JsonElement>("/api/shares")).GetProperty("links").GetArrayLength());

        // Something can't be shared that isn't there, isn't inside My files, or is My files itself.
        foreach (var paths in new[] { new[] { "missing.txt" }, [""], [".homebase"], ["../Host"], Array.Empty<string>() })
            Assert.False((await owner.PostAsJsonAsync("/api/shares", new { paths, canDownload = true })).IsSuccessStatusCode,
                string.Join(",", paths));
    }

    [Fact]
    public async Task A_link_goes_where_what_it_shares_is_moved_and_stops_when_that_is_deleted()
    {
        var (app, owner, root) = await StartAsync();
        using var _ = app;
        await WriteAsync(root, "Docs/report.txt", "report");
        await WriteAsync(root, "Docs/Sub/inner.txt", "inner");
        await WriteAsync(root, "Archive/old.txt", "old");
        var link = await ShareAsync(owner, canDownload: true, "Docs/report.txt", "Docs/Sub");
        var token = TokenOf(link);
        using var visitor = app.CreateClient();
        async Task<string[]> Shared() => (await owner.GetFromJsonAsync<JsonElement>("/api/shares"))
            .GetProperty("links").EnumerateArray().Single()
            .GetProperty("items").EnumerateArray().Select(item => item.GetProperty("path").GetString()!).Order(StringComparer.Ordinal).ToArray();

        (await owner.PostAsJsonAsync("/api/files/rename", new { path = "Docs", name = "Papers" })).EnsureSuccessStatusCode();
        Assert.Equal(["Papers/Sub", "Papers/report.txt"], await Shared());
        Assert.Equal("report", await visitor.GetStringAsync($"/api/shared/{token}/download?path=report.txt"));

        (await owner.PostAsJsonAsync("/api/files/move", new { paths = new[] { "Papers" }, destination = "Archive" })).EnsureSuccessStatusCode();
        Assert.Equal(["Archive/Papers/Sub", "Archive/Papers/report.txt"], await Shared());
        Assert.Equal("inner", await visitor.GetStringAsync($"/api/shared/{token}/download?path=Sub/inner.txt"));

        // A copy is something new, which nobody has shared.
        (await owner.PostAsJsonAsync("/api/files/copy", new { paths = new[] { "Archive/Papers" }, destination = "" })).EnsureSuccessStatusCode();
        Assert.Equal(["Archive/Papers/Sub", "Archive/Papers/report.txt"], await Shared());

        // Deleting one thing in it leaves the rest shared; deleting what holds the last of it turns it off.
        (await owner.PostAsJsonAsync("/api/files/delete", new { paths = new[] { "Archive/Papers/report.txt" } })).EnsureSuccessStatusCode();
        Assert.Equal(["Archive/Papers/Sub"], await Shared());
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/shared/{token}/download?path=report.txt")).StatusCode);
        (await owner.PostAsJsonAsync("/api/files/delete", new { paths = new[] { "Archive" } })).EnsureSuccessStatusCode();
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>("/api/shares")).GetProperty("links").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/shared/{token}")).StatusCode);

        // Putting it back from the Trash doesn't share it again: that's for its owner to choose.
        var bin = await owner.GetFromJsonAsync<JsonElement>("/api/bin");
        var ids = bin.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("id").GetString()).ToArray();
        (await owner.PostAsJsonAsync("/api/bin/restore", new { ids })).EnsureSuccessStatusCode();
        Assert.True(File.Exists(Path.Combine(root, "Archive", "Papers", "Sub", "inner.txt")));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/shared/{token}")).StatusCode);
    }

    [Fact]
    public async Task A_link_stops_working_while_its_owner_is_turned_off_and_goes_with_them()
    {
        var (app, owner, _) = await StartAsync();
        using var __ = app;
        using var member = await app.AddUserAsync(owner, "member");
        var memberRoot = await TestHost.UserRootAsync(member);
        await WriteAsync(memberRoot, "song.mp3", "la la");
        var token = TokenOf(await ShareAsync(member, canDownload: true, "song.mp3"));
        var users = await owner.GetFromJsonAsync<JsonElement>("/api/users");
        var id = users.EnumerateArray().Single(user => user.GetProperty("username").GetString() == "member").GetProperty("id").GetString();
        using var visitor = app.CreateClient();
        Assert.Equal("la la", await visitor.GetStringAsync($"/api/shared/{token}/download?path=song.mp3"));

        (await owner.PatchAsJsonAsync($"/api/users/{id}", new { disabled = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/shared/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/shared/{token}/download?path=song.mp3")).StatusCode);

        (await owner.PatchAsJsonAsync($"/api/users/{id}", new { disabled = false })).EnsureSuccessStatusCode();
        Assert.Equal("la la", await visitor.GetStringAsync($"/api/shared/{token}/download?path=song.mp3"));

        (await owner.DeleteAsync($"/api/users/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/shared/{token}")).StatusCode);
    }

    [Fact]
    public async Task A_link_is_made_with_the_address_other_computers_reach_this_Uncloud_at()
    {
        var (app, owner, root) = await StartAsync(new Dictionary<string, string?>
        {
            ["Homebase:PublicUrl"] = "https://files.example.com/",
            ["Homebase:AllowedHosts"] = "files.example.com"
        });
        using var _ = app;
        await WriteAsync(root, "notes.txt", "notes");

        var link = await ShareAsync(owner, canDownload: false, "notes.txt");

        Assert.StartsWith("https://files.example.com/s/", link.GetProperty("url").GetString());
        var shares = await owner.GetFromJsonAsync<JsonElement>("/api/shares");
        Assert.True(shares.GetProperty("reachable").GetBoolean());
    }

    [Fact]
    public async Task A_link_on_a_host_nothing_else_can_reach_says_so()
    {
        var (app, owner, root) = await StartAsync();
        using var _ = app;
        await WriteAsync(root, "notes.txt", "notes");
        await ShareAsync(owner, canDownload: false, "notes.txt");

        var shares = await owner.GetFromJsonAsync<JsonElement>("/api/shares");

        Assert.False(shares.GetProperty("reachable").GetBoolean());
        Assert.StartsWith("http://localhost/s/", shares.GetProperty("links")[0].GetProperty("url").GetString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
