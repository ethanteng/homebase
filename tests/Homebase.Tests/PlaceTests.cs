using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Core.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homebase.Tests;

/// <summary>
/// Bringing files in from a folder on the host's own computer — the way a household with a Dropbox
/// app already syncing to disk gets its files in without touching a developer console.
///
/// Most of these are about the boundary rather than the copying: the process can read anything its
/// operating-system user can, so which folders are shareable is the whole of what keeps one
/// account out of another's files.
/// </summary>
public sealed class PlaceTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;
    private readonly string _source;

    public PlaceTests()
    {
        Directory.CreateDirectory(_temporary);
        _host = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
        _source = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "TheirDropbox")).FullName);
    }

    private TestHost CreateApp() => new(_config, _ => new StubDropbox());

    private async Task<(HttpClient Client, string Root)> StartAsync(TestHost app)
    {
        var client = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(client, _host);
        return (client, await TestHost.UserRootAsync(client));
    }

    private async Task<string> AddPlaceAsync(HttpClient admin, string path, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/host/places", new { path, name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private void Write(string relative, string contents)
    {
        var full = Path.Combine(_source, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    [Fact]
    public async Task A_folder_on_this_computer_is_brought_home_without_anything_to_sign_in_to()
    {
        Write("Photos/2024/beach.jpg", "a photograph");
        Write("Photos/notes.txt", "where we went");
        using var app = CreateApp();
        var (client, root) = await StartAsync(app);
        using var _ = client;
        var place = await AddPlaceAsync(client, _source, "Dropbox");

        // It shows up as somewhere to bring files in from, alongside the online accounts.
        var sources = await client.GetFromJsonAsync<JsonElement>("/api/imports/sources");
        var listed = Assert.Single(sources.GetProperty("places").EnumerateArray().ToArray());
        Assert.Equal("Dropbox", listed.GetProperty("name").GetString());
        Assert.True(listed.GetProperty("available").GetBoolean());

        // And browsing it is the same shape of answer as browsing an online account.
        var entries = await client.GetFromJsonAsync<JsonElement[]>($"/api/imports/sources/{place}/files?path=");
        var folder = Assert.Single(entries!);
        Assert.Equal("Photos", folder.GetProperty("name").GetString());
        Assert.True(folder.GetProperty("isFolder").GetBoolean());

        var job = await BringHomeAsync(client, place, "/Photos");
        Assert.Equal("Done", job.GetProperty("stage").GetString());
        Assert.Equal(2, job.GetProperty("result").GetProperty("importedCount").GetInt32());

        // Named after the place, so two folders brought in never land on top of each other.
        Assert.Equal("a photograph",
            await File.ReadAllTextAsync(Path.Combine(root, "Files", "Dropbox", "Photos", "2024", "beach.jpg")));
        Assert.Equal("where we went",
            await File.ReadAllTextAsync(Path.Combine(root, "Files", "Dropbox", "Photos", "notes.txt")));
        // The originals are a copy source and nothing else: they stay exactly where they were.
        Assert.True(File.Exists(Path.Combine(_source, "Photos", "2024", "beach.jpg")));
    }

    [Fact]
    public async Task Bringing_the_same_folder_again_brings_only_what_is_new()
    {
        Write("one.txt", "One.");
        using var app = CreateApp();
        var (client, root) = await StartAsync(app);
        using var _ = client;
        var place = await AddPlaceAsync(client, _source, "Backup drive");
        await BringHomeAsync(client, place, "/");

        Write("two.txt", "Two.");
        var again = await BringHomeAsync(client, place, "/");

        Assert.Equal(1, again.GetProperty("result").GetProperty("importedCount").GetInt32());
        Assert.True(File.Exists(Path.Combine(root, "Files", "Backup drive", "two.txt")));
    }

    [Fact]
    public async Task A_place_cannot_be_the_folder_everybody_elses_files_are_in()
    {
        using var app = CreateApp();
        var (client, _) = await StartAsync(app);
        using var __ = client;

        // The host's folder itself, somewhere inside it, and somewhere holding it: each would hand
        // every member a way to read every other account's files.
        foreach (var path in new[] { _host, Path.Combine(_host, "users"), _temporary })
        {
            Directory.CreateDirectory(path);
            var response = await client.PostAsJsonAsync("/api/host/places", new { path, name = "Sneaky" });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_place_cannot_be_where_the_passwords_are_kept()
    {
        using var app = CreateApp();
        var (client, _) = await StartAsync(app);
        using var __ = client;
        Directory.CreateDirectory(_config);

        var response = await client.PostAsJsonAsync("/api/host/places", new { path = _config, name = "Settings" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_host_folder_cannot_move_inside_a_place_everybody_reads_from()
    {
        Write("diary.txt", "Private.");
        using var app = CreateApp();
        var (client, root) = await StartAsync(app);
        using var __ = client;
        await AddPlaceAsync(client, _source, "Old drive");

        // The refusal to add a place holding the host's folder is only half of it: the same pair can
        // be arrived at from the other end, by moving the host's folder into an existing place.
        var moved = Directory.CreateDirectory(Path.Combine(_source, "NewHost")).FullName;
        var response = await client.PutAsJsonAsync("/api/host", new { path = moved });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Old drive", problem.GetProperty("detail").GetString());
        // And the host's folder is the one it always was, so nobody's files have moved.
        Assert.Equal(root, await TestHost.UserRootAsync(client));
    }

    [Fact]
    public void A_folder_named_one_way_and_reached_another_is_still_the_folder_it_is()
    {
        // Every refusal here works by comparing two paths, so both sides have to be resolved to
        // the real folder first or the check passes on a spelling. Resolving once is not enough:
        // a link resolves to the target it stores, and that target can run through another link.
        //
        // This is macOS's arrangement, built by hand so it is tested everywhere: /var is a link
        // into /private/var, and every temporary folder — and Uncloud's preference directory on a
        // host whose home directory sits behind a link — is reached through it.
        var settings = Directory.CreateDirectory(Path.Combine(_temporary, "Settings")).FullName;
        var real = Directory.CreateDirectory(Path.Combine(settings, "private", "Preferences")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(settings, "var"), Path.Combine(settings, "private"));
        // Its stored target runs through that link, so one pass leaves the link in the answer.
        var reachedBy = Path.Combine(settings, "private", "PreferencesLink");
        Directory.CreateSymbolicLink(reachedBy, Path.Combine(settings, "var", "Preferences"));

        // Uncloud was told where its settings are by the name with the links in it.
        var database = new ControlDatabase(reachedBy);
        var places = new ImportPlaces(database, new HostService(database), reachedBy);

        // Offering the same folder under its real name must not get past the refusal.
        var refusal = Assert.Throws<LibraryException>(() => places.Add(real, "Sneaky"));
        Assert.Equal("forbidden", refusal.Code);
        Assert.Contains("passwords", refusal.Message);
    }

    /// <summary>
    /// <paramref name="path"/> with every link in it resolved away, so that resolving it again
    /// changes nothing. Only tests that count resolution passes need this.
    /// </summary>
    private static string Settled(string path)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var resolved = PathPolicy.NormalizeRoot(path);
            if (resolved == path) return path;
            path = resolved;
        }
        throw new InvalidOperationException($"{path} never stopped moving.");
    }

    [Fact]
    public void A_folder_reached_through_too_many_links_is_refused_rather_than_guessed_at()
    {
        // Resolving is bounded, because a cycle of links would otherwise loop forever. That bound
        // has to end in a refusal and not in a half-resolved answer: a path still moving when the
        // passes run out is one Uncloud cannot say the real folder of, and every check here is a
        // comparison against that folder — letting it through decides it isn't the host's folder
        // on the strength of a name that doesn't say where it goes.
        //
        // The arrangement is the one that needs a second look: a link whose target runs through
        // another link, which is the only shape that needs one at all, since a single pass already
        // follows a chain of links to its end. It takes two rewrites and a third pass to see that
        // it has stopped moving. Rather than contriving one that outlasts the real bound, the bound
        // is brought down — the behaviour under test is what happens when the passes run out.
        //
        // Counting passes means starting from a folder that costs none. macOS hands out temporary
        // folders under /var, which is itself a link, and a link's stored target brings that back
        // unresolved every time — so an ambient temporary folder would add a pass on macOS and
        // move the bound this test is aimed at.
        var settings = Directory.CreateDirectory(Path.Combine(Settled(_temporary), "Links", "Settings")).FullName;
        var real = Directory.CreateDirectory(Path.Combine(settings, "private", "Preferences")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(settings, "var"), Path.Combine(settings, "private"));
        var reachedBy = Path.Combine(settings, "private", "PreferencesLink");
        Directory.CreateSymbolicLink(reachedBy, Path.Combine(settings, "var", "Preferences"));

        var database = new ControlDatabase(reachedBy);
        var host = new HostService(database);

        // Three passes is enough to see through it, and the folder is refused for what it holds.
        var seeing = new ImportPlaces(database, host, reachedBy) { Rewrites = 3 };
        Assert.Contains("passwords", Assert.Throws<LibraryException>(() => seeing.Add(real, "Sneaky")).Message);

        // Two is not, and the answer is a refusal rather than the half-resolved path it got to.
        var blinkered = new ImportPlaces(database, host, reachedBy) { Rewrites = 2 };
        var refusal = Assert.Throws<LibraryException>(() => blinkered.Add(real, "Sneaky"));
        Assert.Equal("forbidden", refusal.Code);
        Assert.Contains("too many linked folders", refusal.Message);
    }

    [Fact]
    public async Task Removing_a_place_stops_an_import_already_running_from_it()
    {
        // Deleting the row does not reach an import already going: it holds the source it started
        // with, which carries the folder it resolved to and never asks again. An administrator
        // removing a folder shared by mistake is trying to stop the reading, not just the starting.
        //
        // Held open at its first file rather than raced against a real one, because whether a copy
        // of some number of small files is still going a moment later is not a thing to assert.
        var source = new StubDropbox { Gated = true };
        source.AddFolder("/notes");
        source.Add("/notes/one.txt", "rev1", "One.");
        source.Add("/notes/two.txt", "rev1", "Two.");
        var database = new ControlDatabase(_config);
        var host = new HostService(database);
        host.SelectRoot(_host);
        var workspaces = new UserWorkspaces(host, new MetadataIndex(), new ImportLog(),
            new OneStub(source), new ImportPlaces(database, host, _config), NullLoggerFactory.Instance);

        var workspace = workspaces.For("someone");
        workspace.Jobs.Start(source, "folder:abc", "/notes", "notes");
        await source.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A place nobody is importing from is nobody's import to stop.
        Assert.Equal(0, workspaces.CancelImportsFrom("folder:something-else"));
        Assert.Equal(1, workspaces.CancelImportsFrom("folder:abc"));

        source.Gate.SetResult();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (workspace.Jobs.Current is { Running: true } && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(15);
        Assert.Equal(ImportStage.Stopped, workspace.Jobs.Current!.Stage);
    }

    /// <summary>One stub for whoever asks, so a workspace can be built around a held-open import.</summary>
    private sealed class OneStub(StubDropbox stub) : IDropboxApiFactory
    {
        public IDropboxConnection For(string userId) => stub;
        public void Forget(string userId) { }
        public void ForgetAll() { }
    }

    [Fact]
    public void A_place_that_comes_to_hold_the_host_folder_is_refused_when_it_is_used()
    {
        // Defence in depth for the pair above. Both ways in are refused, but a place is checked
        // again every time it is used, so a relationship that arises some other way — a link that
        // changes, a place added before this check existed — is caught at the point of reading.
        var database = new ControlDatabase(_config);
        var host = new HostService(database);
        var places = new ImportPlaces(database, host, _config);
        var added = places.Add(_source, "Old drive");
        Assert.Equal(added.Id, places.Require(added.Id).Id);

        // Moved on the service directly, which is not the route an administrator takes.
        host.SelectRoot(Directory.CreateDirectory(Path.Combine(_source, "NewHost")).FullName);

        var refusal = Assert.Throws<LibraryException>(() => places.Require(added.Id));
        Assert.Equal("forbidden", refusal.Code);
    }

    [Fact]
    public async Task Only_an_administrator_decides_which_folders_everybody_can_read()
    {
        using var app = CreateApp();
        var (admin, _) = await StartAsync(app);
        using var __ = admin;
        using var member = await app.AddUserAsync(admin, "bo");

        // Adding a place shares it with everyone here, so it is the host's decision, not a member's.
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/host/places")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync("/api/host/places", new { path = _source, name = "Mine" })).StatusCode);

        var place = await AddPlaceAsync(admin, _source, "Family photos");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/host/places/{place}")).StatusCode);
        // What they can do is use one, which is the point of adding it.
        (await member.GetAsync($"/api/imports/sources/{place}/files?path=")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_request_cannot_walk_out_of_the_place_it_was_given()
    {
        Write("inside.txt", "Fine.");
        await File.WriteAllTextAsync(Path.Combine(_temporary, "outside.txt"), "Not yours.");
        using var app = CreateApp();
        var (client, _) = await StartAsync(app);
        using var __ = client;
        var place = await AddPlaceAsync(client, _source, "Theirs");

        foreach (var path in new[] { "..", "../", "/..", "sub/../..", _temporary, "/etc" })
        {
            var response = await client.GetAsync(
                $"/api/imports/sources/{place}/files?path={Uri.EscapeDataString(path)}");
            Assert.False(response.IsSuccessStatusCode, $"{path} was allowed");
        }

        // An id that names no place is refused rather than guessed at.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/imports/sources/deadbeef/files?path=")).StatusCode);
    }

    [Fact]
    public async Task A_link_inside_a_place_is_left_out_rather_than_followed()
    {
        Write("real.txt", "Mine.");
        var secret = Path.Combine(_temporary, "elsewhere");
        Directory.CreateDirectory(secret);
        await File.WriteAllTextAsync(Path.Combine(secret, "secrets.txt"), "Not yours.");
        // A link in somebody's synced folder could point anywhere, the host's folder included.
        Directory.CreateSymbolicLink(Path.Combine(_source, "shortcut"), secret);
        using var app = CreateApp();
        var (client, root) = await StartAsync(app);
        using var __ = client;
        var place = await AddPlaceAsync(client, _source, "Theirs");

        var entries = await client.GetFromJsonAsync<JsonElement[]>($"/api/imports/sources/{place}/files?path=");

        Assert.Equal(["real.txt"], entries!.Select(entry => entry.GetProperty("name").GetString()));
        // Nor does asking for it by name get anywhere, listing or no listing.
        Assert.False((await client.GetAsync($"/api/imports/sources/{place}/files?path=%2Fshortcut")).IsSuccessStatusCode);

        await BringHomeAsync(client, place, "/");
        Assert.False(File.Exists(Path.Combine(root, "Files", "Theirs", "shortcut", "secrets.txt")));
    }

    [Fact]
    public async Task Two_places_cannot_share_one_folder_in_the_library()
    {
        var other = Directory.CreateDirectory(Path.Combine(_temporary, "Second")).FullName;
        using var app = CreateApp();
        var (client, _) = await StartAsync(app);
        using var __ = client;
        await AddPlaceAsync(client, _source, "Dropbox");

        // Both would write into Files/Dropbox, which would make "already imported" ambiguous.
        var response = await client.PostAsJsonAsync("/api/host/places", new { path = other, name = "Dropbox" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Nor can the same folder be added twice under two names.
        var again = await client.PostAsJsonAsync("/api/host/places", new { path = _source, name = "Dropbox again" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Removing_a_place_leaves_what_was_already_brought_home_alone()
    {
        Write("keep.txt", "Mine now.");
        using var app = CreateApp();
        var (client, root) = await StartAsync(app);
        using var __ = client;
        var place = await AddPlaceAsync(client, _source, "Old drive");
        await BringHomeAsync(client, place, "/");

        (await client.DeleteAsync($"/api/host/places/{place}")).EnsureSuccessStatusCode();

        Assert.Equal("Mine now.", await File.ReadAllTextAsync(Path.Combine(root, "Files", "Old drive", "keep.txt")));
        // The record of where it came from is theirs too, and stays.
        Assert.Single(await client.GetFromJsonAsync<JsonElement[]>("/api/imports") ?? []);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/imports/sources"))
            .GetProperty("places").EnumerateArray());
    }

    [Fact]
    public async Task A_place_whose_drive_is_unplugged_says_so_instead_of_failing_obscurely()
    {
        var drive = Directory.CreateDirectory(Path.Combine(_temporary, "Ext")).FullName;
        using var app = CreateApp();
        var (client, _) = await StartAsync(app);
        using var __ = client;
        var place = await AddPlaceAsync(client, drive, "Ext");
        Directory.Delete(drive);

        var sources = await client.GetFromJsonAsync<JsonElement>("/api/imports/sources");
        Assert.False(sources.GetProperty("places").EnumerateArray().Single()
            .GetProperty("available").GetBoolean());

        var response = await client.GetAsync($"/api/imports/sources/{place}/files?path=");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("isn’t on this computer", problem.GetProperty("detail").GetString());
    }

    /// <summary>Starts an import and waits for it, which is how the panel watches one too.</summary>
    private static async Task<JsonElement> BringHomeAsync(HttpClient client, string source, string remotePath)
    {
        var started = await client.PostAsJsonAsync("/api/imports", new { remotePath, source });
        started.EnsureSuccessStatusCode();
        return await SettledAsync(client);
    }

    /// <summary>Waits for whatever import is running to stop, however it stops.</summary>
    private static async Task<JsonElement> SettledAsync(HttpClient client)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = (await client.GetFromJsonAsync<JsonElement>("/api/imports/job")).GetProperty("job");
            if (job.ValueKind != JsonValueKind.Null && !job.GetProperty("running").GetBoolean()) return job;
            await Task.Delay(15);
        }
        throw new TimeoutException("The import never finished.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }
}
