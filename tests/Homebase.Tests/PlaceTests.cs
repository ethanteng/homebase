using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Core.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homebase.Tests;

/// <summary>
/// Bringing files in from a folder on the host's own computer — the way a household with a Dropbox
/// app already syncing to disk gets its files in without touching a developer console.
///
/// Most of these are about the boundary rather than the copying: the process can read anything its
/// operating-system user can, so which folders are readable, and by whom, is the whole of what keeps
/// one account out of another's files. A folder is its owner's alone until they share it.
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

    private static async Task ShareAsync(HttpClient owner, string place, bool shared)
    {
        var response = await owner.PatchAsJsonAsync($"/api/host/places/{place}", new { shared });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> IdOfAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("user").GetProperty("id").GetString()!;

    private static async Task<JsonElement[]> PlacesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/imports/sources")).GetProperty("places").EnumerateArray().ToArray();

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

        // It shows up as somewhere to bring files in from, alongside the online accounts, and as
        // this account's own rather than anybody else's.
        var sources = await client.GetFromJsonAsync<JsonElement>("/api/imports/sources");
        var listed = Assert.Single(sources.GetProperty("places").EnumerateArray().ToArray());
        Assert.Equal("Dropbox", listed.GetProperty("name").GetString());
        Assert.True(listed.GetProperty("available").GetBoolean());
        Assert.True(listed.GetProperty("mine").GetBoolean());
        Assert.Equal("Dropbox", listed.GetProperty("destination").GetString());
        Assert.False(listed.GetProperty("shared").GetBoolean());
        Assert.Equal("dropbox", Assert.Single(sources.GetProperty("accounts").EnumerateArray().ToArray())
            .GetProperty("id").GetString());

        // And browsing it is the same shape of answer as browsing an online account.
        var entries = await client.GetFromJsonAsync<JsonElement[]>($"/api/imports/sources/{place}/files?path=");
        var folder = Assert.Single(entries!);
        Assert.Equal("Photos", folder.GetProperty("name").GetString());
        Assert.True(folder.GetProperty("isFolder").GetBoolean());

        var job = await BringHomeAsync(client, place, "/Photos");
        Assert.Equal("Done", job.GetProperty("stage").GetString());
        Assert.Equal(2, job.GetProperty("result").GetProperty("importedCount").GetInt32());

        // At the top of My files, in a folder named after the place, so it is where somebody would
        // look for it — and two folders brought in never land on top of each other.
        Assert.Equal("a photograph",
            await File.ReadAllTextAsync(Path.Combine(root, "Dropbox", "Photos", "2024", "beach.jpg")));
        Assert.Equal("where we went",
            await File.ReadAllTextAsync(Path.Combine(root, "Dropbox", "Photos", "notes.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, "Files")));
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
        Assert.True(File.Exists(Path.Combine(root, "Backup drive", "two.txt")));
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
        var detail = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
        Assert.Contains("Old drive", detail);
        Assert.DoesNotContain("{", detail);
        // And the host's folder is the one it always was, so nobody's files have moved.
        Assert.Equal(root, await TestHost.UserRootAsync(client));

        // Another administrator is refused the same move, without being told the name of a folder
        // that is private to somebody else.
        using var other = await app.AddUserAsync(client, "cy", isAdmin: true);
        var theirs = await other.PutAsJsonAsync("/api/host", new { path = moved });
        Assert.Equal(HttpStatusCode.Conflict, theirs.StatusCode);
        Assert.DoesNotContain("Old drive",
            (await theirs.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
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
        var refusal = Assert.Throws<LibraryException>(() => places.Add("someone", real, "Sneaky"));
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
        Assert.Contains("passwords", Assert.Throws<LibraryException>(() => seeing.Add("someone", real, "Sneaky")).Message);

        // Two is not, and the answer is a refusal rather than the half-resolved path it got to.
        var blinkered = new ImportPlaces(database, host, reachedBy) { Rewrites = 2 };
        var refusal = Assert.Throws<LibraryException>(() => blinkered.Add("someone", real, "Sneaky"));
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
        // Recorded under the place's id, which is what a request names it by.
        workspace.Jobs.Start(source, "abc", "/notes", "notes");
        await source.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A place nobody is importing from is nobody's import to stop, and nor is one somebody
        // else is left free to carry on with.
        Assert.Equal(0, workspaces.CancelImportsFrom("something-else"));
        Assert.Equal(0, workspaces.CancelImportsFrom("abc", reader => reader != "someone"));
        Assert.Equal(1, workspaces.CancelImportsFrom("abc"));

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
        var owner = new UserStore(database).Create("owner", "Owner", TestHost.Password, isAdmin: true);
        var added = places.Add(owner.Id, _source, "Old drive");
        Assert.Equal(added.Id, places.Require(added.Id, owner.Id).Id);

        // Moved on the service directly, which is not the route an administrator takes.
        host.SelectRoot(Directory.CreateDirectory(Path.Combine(_source, "NewHost")).FullName);

        var refusal = Assert.Throws<LibraryException>(() => places.Require(added.Id, owner.Id));
        Assert.Equal("forbidden", refusal.Code);
    }

    [Fact]
    public void An_installer_left_open_is_not_offered_as_a_drive()
    {
        // Opening Uncloud's download mounts it under /Volumes, and macOS numbers the next one
        // opened while it's still there, so a Mac that has installed a few builds is offered
        // "Uncloud", "Uncloud 1", "Uncloud 2"… as drives to bring files in from.
        var volumes = Directory.CreateDirectory(Path.Combine(_temporary, "Volumes")).FullName;
        foreach (var name in new[] { "Uncloud", "Uncloud 1" })
        {
            var installer = Directory.CreateDirectory(Path.Combine(volumes, name)).FullName;
            Directory.CreateDirectory(Path.Combine(installer, "Uncloud.app", "Contents"));
            Directory.CreateSymbolicLink(Path.Combine(installer, "Applications"), "/Applications");
            Directory.CreateDirectory(Path.Combine(installer, ".background"));
            File.WriteAllText(Path.Combine(installer, ".DS_Store"), "");
        }
        // An app on a drive with anything else on it is a drive with files on it.
        var backup = Directory.CreateDirectory(Path.Combine(volumes, "Backup")).FullName;
        Directory.CreateDirectory(Path.Combine(backup, "Old.app"));
        Directory.CreateDirectory(Path.Combine(backup, "Photos"));
        // So is one whose shortcut leads somewhere other than Applications.
        var tools = Directory.CreateDirectory(Path.Combine(volumes, "Tools")).FullName;
        Directory.CreateDirectory(Path.Combine(tools, "Tool.app"));
        Directory.CreateSymbolicLink(Path.Combine(tools, "Notes"), _source);
        // And one with nothing on it yet is still a drive somebody might mean.
        Directory.CreateDirectory(Path.Combine(volumes, "Blank"));

        var database = new ControlDatabase(_config);
        var places = new ImportPlaces(database, new HostService(database), _config)
        {
            Home = Directory.CreateDirectory(Path.Combine(_temporary, "Home")).FullName,
            DriveFolders = [volumes]
        };

        var offered = places.Suggestions("someone");
        Assert.Equal(["Backup", "Blank", "Tools"], offered.Select(place => place.Name));
        Assert.All(offered, place => Assert.Equal("drive", place.Kind));
    }

    [Fact]
    public async Task Only_somebody_who_looks_after_this_host_can_add_one_of_its_folders()
    {
        using var app = CreateApp();
        var (admin, _) = await StartAsync(app);
        using var __ = admin;
        using var member = await app.AddUserAsync(admin, "bo");

        // This computer's folders are its administrator's own files as much as anybody's; a member
        // naming one would be reading them. So a member isn't offered any, and can't add one.
        var sources = await member.GetFromJsonAsync<JsonElement>("/api/imports/sources");
        Assert.False(sources.GetProperty("canAddFolders").GetBoolean());
        Assert.Empty(sources.GetProperty("suggestions").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync("/api/host/places", new { path = _source, name = "Mine" })).StatusCode);

        var place = await AddPlaceAsync(admin, _source, "Family photos");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/host/places/{place}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PatchAsJsonAsync($"/api/host/places/{place}", new { shared = true })).StatusCode);
    }

    [Fact]
    public async Task A_folder_is_private_to_whoever_added_it_until_they_share_it()
    {
        Write("diary.txt", "Mine.");
        using var app = CreateApp();
        var (admin, _) = await StartAsync(app);
        using var __ = admin;
        using var member = await app.AddUserAsync(admin, "bo");
        using var otherAdmin = await app.AddUserAsync(admin, "cy", isAdmin: true);
        var place = await AddPlaceAsync(admin, _source, "Documents");

        // Nobody else sees it, administrators included, and asking for it by id finds nothing
        // rather than confirming that somebody's private folder is there.
        foreach (var other in new[] { member, otherAdmin })
        {
            Assert.Empty(await PlacesAsync(other));
            Assert.Equal(HttpStatusCode.NotFound,
                (await other.GetAsync($"/api/imports/sources/{place}/files?path=")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await other.PostAsJsonAsync("/api/imports", new { remotePath = "/", source = place })).StatusCode);
        }
        // Nor can another administrator share it, or take it away, on its owner's behalf.
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherAdmin.PatchAsJsonAsync($"/api/host/places/{place}", new { shared = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherAdmin.DeleteAsync($"/api/host/places/{place}")).StatusCode);

        await ShareAsync(admin, place, shared: true);

        // Shared, it is everybody's to bring files in from — by name and by who shared it, but not
        // by where it sits on the host's disk, which is only its owner's business.
        var seen = Assert.Single(await PlacesAsync(member));
        Assert.Equal("Documents", seen.GetProperty("name").GetString());
        Assert.False(seen.GetProperty("mine").GetBoolean());
        Assert.Equal("owner", seen.GetProperty("sharedBy").GetString());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("path").ValueKind);
        (await member.GetAsync($"/api/imports/sources/{place}/files?path=")).EnsureSuccessStatusCode();

        await ShareAsync(admin, place, shared: false);
        Assert.Empty(await PlacesAsync(member));
        Assert.Equal(HttpStatusCode.NotFound,
            (await member.GetAsync($"/api/imports/sources/{place}/files?path=")).StatusCode);
    }

    [Fact]
    public async Task A_folder_stops_being_readable_through_somebody_who_no_longer_looks_after_this_host()
    {
        using var app = CreateApp();
        var (admin, _) = await StartAsync(app);
        using var __ = admin;
        using var member = await app.AddUserAsync(admin, "bo");
        using var formerAdmin = await app.AddUserAsync(admin, "cy", isAdmin: true);
        var place = await AddPlaceAsync(formerAdmin, _source, "Old drive");
        await ShareAsync(formerAdmin, place, shared: true);
        Assert.Single(await PlacesAsync(member));

        var id = await IdOfAsync(formerAdmin);
        (await admin.PatchAsJsonAsync($"/api/users/{id}", new { isAdmin = false })).EnsureSuccessStatusCode();

        // Being able to read this computer's folders came with looking after it, and so did being
        // able to share one. Neither outlives it.
        Assert.Empty(await PlacesAsync(formerAdmin));
        Assert.Empty(await PlacesAsync(member));
        Assert.Equal(HttpStatusCode.NotFound,
            (await member.GetAsync($"/api/imports/sources/{place}/files?path=")).StatusCode);
    }

    [Fact]
    public async Task Unsharing_a_folder_stops_anybody_else_partway_through_bringing_it_home()
    {
        // The share is what the reading rested on, so taking it back has to reach an import already
        // under way, which holds the folder it started on and never asks again. Held open at its
        // first file through the real workspace, so this goes through the same wiring a request does.
        using var app = CreateApp();
        var (admin, _) = await StartAsync(app);
        using var __ = admin;
        using var member = await app.AddUserAsync(admin, "bo");
        var place = await AddPlaceAsync(admin, _source, "Family photos");
        await ShareAsync(admin, place, shared: true);

        var reading = await HeldOpenAsync(app, await IdOfAsync(member), place);
        var owners = await HeldOpenAsync(app, await IdOfAsync(admin), place);
        await ShareAsync(admin, place, shared: false);

        Assert.Equal(ImportStage.Stopped, await SettledAsync(reading));
        // Its owner's own import is theirs, and is left to finish.
        Assert.Equal(ImportStage.Done, await SettledAsync(owners));
    }

    [Fact]
    public async Task Removing_a_folder_stops_an_import_already_running_from_it()
    {
        using var app = CreateApp();
        var (admin, _) = await StartAsync(app);
        using var __ = admin;
        var place = await AddPlaceAsync(admin, _source, "Old drive");
        var running = await HeldOpenAsync(app, await IdOfAsync(admin), place);

        (await admin.DeleteAsync($"/api/host/places/{place}")).EnsureSuccessStatusCode();

        Assert.Equal(ImportStage.Stopped, await SettledAsync(running));
    }

    /// <summary>
    /// An import recorded as coming from <paramref name="place"/>, in <paramref name="userId"/>'s own
    /// workspace, held at its first file until the stub's gate is opened — which the overload of
    /// SettledAsync below does once whatever the test is about has happened.
    /// </summary>
    private static async Task<(ImportJobs Jobs, StubDropbox Source)> HeldOpenAsync(TestHost app, string userId, string place)
    {
        var source = new StubDropbox { Gated = true };
        source.AddFolder("/notes");
        source.Add("/notes/one.txt", "rev1", "One.");
        source.Add("/notes/two.txt", "rev1", "Two.");
        var jobs = app.Services.GetRequiredService<UserWorkspaces>().For(userId).Jobs;
        jobs.Start(source, place, "/notes", "notes");
        await source.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (jobs, source);
    }

    private static async Task<ImportStage> SettledAsync((ImportJobs Jobs, StubDropbox Source) held)
    {
        held.Source.Gate.TrySetResult();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (held.Jobs.Current is { Running: true } && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(15);
        return held.Jobs.Current!.Stage;
    }

    [Fact]
    public void Folders_added_before_they_had_owners_stay_shared_by_the_longest_standing_administrator()
    {
        // A host from before folders belonged to anybody added them under a warning that everyone
        // could read them. Taking that away from people halfway through using one isn't Uncloud's
        // decision to make, so they carry on shared, owned by whoever has looked after it longest.
        Directory.CreateDirectory(_config);
        using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_config, "homebase.db")};Pooling=False"))
        {
            legacy.Open();
            using var command = legacy.CreateCommand();
            command.CommandText = """
                CREATE TABLE users (
                    id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE, display_name TEXT NOT NULL,
                    password_hash TEXT NOT NULL, is_admin INTEGER NOT NULL,
                    created_at TEXT NOT NULL, disabled_at TEXT
                );
                CREATE TABLE import_places (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL, added_at TEXT NOT NULL
                );
                INSERT INTO users VALUES ('member', 'bo', 'Bo', 'x', 0, '2024-01-01T00:00:00+00:00', NULL);
                INSERT INTO users VALUES ('second', 'cy', 'Cy', 'x', 1, '2024-06-01T00:00:00+00:00', NULL);
                INSERT INTO users VALUES ('first', 'al', 'Al', 'x', 1, '2024-03-01T00:00:00+00:00', NULL);
                INSERT INTO import_places VALUES ('p1', 'Family', '/somewhere', '2024-07-01T00:00:00+00:00');
                PRAGMA user_version = 4;
                """;
            command.ExecuteNonQuery();
        }

        var database = new ControlDatabase(_config);
        var places = new ImportPlaces(database, new HostService(database), _config);

        var place = Assert.Single(places.VisibleTo("member"));
        Assert.Equal("first", place.OwnerId);
        Assert.True(place.Shared);
        // And opening it again, as every request does, leaves it as it is.
        Assert.Equal("first", Assert.Single(places.VisibleTo("second")).OwnerId);
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
        Assert.False(File.Exists(Path.Combine(root, "Theirs", "shortcut", "secrets.txt")));
    }

    [Fact]
    public async Task Choosing_the_same_folder_twice_is_the_same_folder_and_a_second_name_is_numbered()
    {
        var other = Directory.CreateDirectory(Path.Combine(_temporary, "Second")).FullName;
        using var app = CreateApp();
        var (client, _) = await StartAsync(app);
        using var __ = client;
        var first = await AddPlaceAsync(client, _source, "Dropbox");

        // Choosing it again, from a suggestion or the folder chooser, is not a mistake to explain.
        Assert.Equal(first, await AddPlaceAsync(client, _source, "Dropbox again"));

        // Two folders with one name would write into one folder in My files, so the second is
        // numbered rather than refused: having two folders called Photos is an ordinary thing.
        var response = await client.PostAsJsonAsync("/api/host/places", new { path = other, name = "Dropbox" });
        response.EnsureSuccessStatusCode();
        Assert.Equal("Dropbox 2", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        Assert.Equal(2, (await PlacesAsync(client)).Length);
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

        Assert.Equal("Mine now.", await File.ReadAllTextAsync(Path.Combine(root, "Old drive", "keep.txt")));
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
