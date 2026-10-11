using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Core.Providers;
using Uncloud.Desktop;

namespace Homebase.Tests;

/// <summary>
/// A host that runs in an operating-system account of its own, which is how the Mac app sets
/// Uncloud up so that nobody signed in at the Mac can look through everybody's files. That account
/// has no screen and can't read anybody's folders, so: it is handed its first folder rather than
/// asked for one, it says why a folder it can't reach won't do, and the Mac's folders reach it
/// through the Uncloud app of whoever is signed in there.
/// </summary>
public sealed class OwnAccountTests : IAsyncDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    // A Unix socket's path has to fit in about a hundred bytes, which a test folder nested as deep
    // as the rest doesn't on a Mac.
    private readonly string _bridgeFolder = Path.Combine(Path.GetTempPath(), "uc-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _config;
    private readonly string _host;
    private readonly string _home;
    private readonly string _drives;
    private HostFoldersBridge? _bridge;

    public OwnAccountTests()
    {
        Directory.CreateDirectory(_temporary);
        Directory.CreateDirectory(_bridgeFolder);
        _config = Path.Combine(_temporary, "Config");
        _host = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _home = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Home")).FullName);
        _drives = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Volumes")).FullName);
    }

    private string Socket => Path.Combine(_bridgeFolder, "b.sock");

    private async Task<HostFoldersBridge> OpenAppAsync()
    {
        _bridge = await HostFoldersBridge.StartAsync(Socket,
            new LocalHostFolders { Home = _home, DriveFolders = [_drives] }, [_config], CancellationToken.None);
        return _bridge;
    }

    private async Task CloseAppAsync()
    {
        if (_bridge is null) return;
        await _bridge.DisposeAsync();
        _bridge = null;
    }

    private TestHost OwnAccountHost(IDictionary<string, string?>? more = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Homebase:OwnAccount"] = "true",
            ["Homebase:HostFolders:Socket"] = Socket
        };
        foreach (var (key, value) in more ?? new Dictionary<string, string?>()) settings[key] = value;
        return new TestHost(_config, _ => new StubDropbox(), settings);
    }

    private string Write(string folder, string relative, string contents)
    {
        var full = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    [Fact]
    public async Task A_host_set_up_with_a_folder_starts_with_it_and_one_chosen_later_is_kept()
    {
        var given = Directory.CreateDirectory(Path.Combine(_temporary, "Given")).FullName;
        using (var app = OwnAccountHost(new Dictionary<string, string?> { ["Homebase:Root"] = given }))
        {
            // Nobody is asked where files live: the app that set this host up already said.
            using var admin = await app.SignUpAsync();
            var host = await admin.GetFromJsonAsync<JsonElement>("/api/host");
            Assert.Equal(PathPolicy.NormalizeRoot(given), host.GetProperty("rootPath").GetString());
            Assert.True(Directory.Exists(Path.Combine(given, UserPaths.UsersDirectory)));
            await TestHost.SetHostRootAsync(admin, _host);
        }

        // Once somebody has chosen, that choice is the one kept, whatever the app first said.
        using var again = OwnAccountHost(new Dictionary<string, string?> { ["Homebase:Root"] = given });
        using var signedIn = await again.SignInAsync("owner");
        Assert.Equal(_host, (await signedIn.GetFromJsonAsync<JsonElement>("/api/host")).GetProperty("rootPath").GetString());
    }

    [Fact]
    public void A_folder_that_isnt_there_to_start_with_leaves_the_host_asking_for_one()
    {
        var database = new ControlDatabase(_config);
        var host = new HostService(database, initial: Path.Combine(_temporary, "Unplugged"));
        Assert.Null(host.RootPath);
    }

    [Fact]
    public async Task A_host_in_its_own_account_offers_no_folder_chooser_and_says_why_a_folder_wont_do()
    {
        using var app = OwnAccountHost();
        using var admin = await app.SignUpAsync();

        // A daemon has no screen to put a chooser on, so the page asks for a path instead.
        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/host")).GetProperty("canPickFolder").GetBoolean());

        // A folder its account can't reach looks to it like one that isn't there, so the answer
        // says what will work rather than "that folder doesn't exist".
        var response = await admin.PutAsJsonAsync("/api/host", new { path = Path.Combine(_temporary, "Somewhere") });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString();
        Assert.Contains("Use Another Folder", detail);
    }

    [Fact]
    public async Task A_host_running_as_its_person_still_says_a_missing_folder_is_missing()
    {
        using var app = new TestHost(_config, _ => new StubDropbox());
        using var admin = await app.SignUpAsync();
        var response = await admin.PutAsJsonAsync("/api/host", new { path = Path.Combine(_temporary, "Somewhere") });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_Macs_folders_reach_a_host_in_its_own_account_through_the_app_signed_in_there()
    {
        Directory.CreateDirectory(Path.Combine(_home, "Documents"));
        Write(_home, "Documents/Letters/to gran.txt", "Dear Gran");
        Directory.CreateDirectory(Path.Combine(_drives, "Backup"));
        await OpenAppAsync();
        using var app = OwnAccountHost();
        using var admin = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(admin, _host);
        var root = await TestHost.UserRootAsync(admin);

        // What's offered is what the person at the Mac has, not what Uncloud's own account has.
        var sources = await admin.GetFromJsonAsync<JsonElement>("/api/imports/sources");
        Assert.Equal(JsonValueKind.Null, sources.GetProperty("computerUnavailable").ValueKind);
        var offered = sources.GetProperty("suggestions").EnumerateArray()
            .ToDictionary(place => place.GetProperty("name").GetString()!, place => place.GetProperty("kind").GetString());
        Assert.Equal("folder", offered["Documents"]);
        Assert.Equal("drive", offered["Backup"]);

        var added = await admin.PostAsJsonAsync("/api/host/places", new { path = Path.Combine(_home, "Documents") });
        added.EnsureSuccessStatusCode();
        var place = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/imports/sources"))
            .GetProperty("places")[0].GetProperty("available").GetBoolean());

        var listed = await admin.GetFromJsonAsync<JsonElement[]>($"/api/imports/sources/{place}/files?path=");
        Assert.Equal("Letters", Assert.Single(listed!).GetProperty("name").GetString());
        var size = await admin.GetFromJsonAsync<JsonElement>($"/api/imports/sources/{place}/size?path=/Letters");
        Assert.Equal(1, size.GetProperty("files").GetInt32());

        var started = await admin.PostAsJsonAsync("/api/imports", new { remotePath = "/Letters", source = place });
        started.EnsureSuccessStatusCode();
        var job = await SettledAsync(admin);
        Assert.Equal("Done", job.GetProperty("stage").GetString());
        Assert.Equal("Dear Gran", await File.ReadAllTextAsync(Path.Combine(root, "Documents", "Letters", "to gran.txt")));

        // A path the bridge is handed is still held inside the place.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.GetAsync($"/api/imports/sources/{place}/files?path=/../../")).StatusCode);
    }

    [Fact]
    public async Task With_the_app_closed_the_Macs_folders_are_unavailable_and_everyone_is_told_why()
    {
        Directory.CreateDirectory(Path.Combine(_home, "Pictures"));
        Write(_home, "Pictures/cat.jpg", "a cat");
        await OpenAppAsync();
        using var app = OwnAccountHost();
        using var admin = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(admin, _host);
        var added = await admin.PostAsJsonAsync("/api/host/places", new { path = Path.Combine(_home, "Pictures") });
        added.EnsureSuccessStatusCode();
        var place = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        await CloseAppAsync();

        // Add files still opens — Dropbox has nothing to do with the Mac's folders — and says why
        // there's nothing to offer from this computer.
        var sources = await admin.GetFromJsonAsync<JsonElement>("/api/imports/sources");
        Assert.Equal(BridgedHostFolders.AppNotOpen, sources.GetProperty("computerUnavailable").GetString());
        Assert.Empty(sources.GetProperty("suggestions").EnumerateArray());
        Assert.False(sources.GetProperty("places")[0].GetProperty("available").GetBoolean());

        var browsing = await admin.GetAsync($"/api/imports/sources/{place}/files?path=");
        Assert.Equal(HttpStatusCode.Conflict, browsing.StatusCode);
        Assert.Equal(BridgedHostFolders.AppNotOpen,
            (await browsing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        // Opened again, it all comes back without anybody restarting anything.
        await OpenAppAsync();
        (await admin.GetAsync($"/api/imports/sources/{place}/files?path=")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_app_never_reads_Uncloud_s_own_folders_for_it()
    {
        Write(_config, "homebase.db", "everybody's passwords");
        await OpenAppAsync();
        using var folders = new BridgedHostFolders(Socket);
        var source = folders.Open(new ImportPlace("x", "Sneaky", _config, DateTimeOffset.UtcNow, "someone", false));

        var refusal = await Assert.ThrowsAsync<LibraryException>(() => source.ListFolderAsync("", CancellationToken.None));
        Assert.Equal("forbidden", refusal.Code);
        await Assert.ThrowsAsync<LibraryException>(() => source.OpenAsync("/homebase.db", CancellationToken.None));
    }

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

    public async ValueTask DisposeAsync()
    {
        await CloseAppAsync();
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
        try { Directory.Delete(_bridgeFolder, true); } catch (IOException) { }
    }
}
