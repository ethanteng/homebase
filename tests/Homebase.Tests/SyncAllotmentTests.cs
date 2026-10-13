using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homebase.Tests;

/// <summary>
/// What computers send is held to the storage allotment too. Syncthing writes without asking
/// Uncloud first, so it is told how much of the drive to leave free instead, and these are about it
/// being told — when a folder is made, when the allotment changes, and not for every few megabytes
/// something else on the computer writes — and about saying so when files are held back.
/// </summary>
public sealed class SyncAllotmentTests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;
    private const string Laptop = "ZZZZZZZ-YYYYYYY-XXXXXXX-WWWWWWW-VVVVVVV-UUUUUUU-TTTTTTT-SSSSSSS";

    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSyncthing _syncthing = new();
    private readonly ControlDatabase _database;
    private readonly UserStore _users;
    private readonly SyncOwnership _ownership;
    private readonly HostService _host;
    private readonly StorageAllotment _allotment;
    private readonly SyncService _sync;
    private readonly UserAccount _ada;
    private readonly string _adaRoot;
    private long _held = 20 * GB;

    static SyncAllotmentTests() => PasswordHasher.Override = 1_000;

    public SyncAllotmentTests()
    {
        Directory.CreateDirectory(_temporary);
        _database = new ControlDatabase(Path.Combine(_temporary, "Config"));
        _users = new UserStore(_database);
        _ownership = new SyncOwnership(_database);
        _host = new HostService(_database);
        _host.SelectRoot(PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName));
        // 500 GB free and 20 GB of everybody's files on a 1 TB drive.
        _allotment = Allotment();
        _sync = Sync(_allotment);
        _ada = _users.CreateFirstAdmin("ada", "Ada", TestHost.Password);
        _adaRoot = UserPaths.RootFor(_host.RequireRoot(), _ada.Id);
        Directory.CreateDirectory(Path.Combine(_adaRoot, "Documents"));
    }

    private StorageAllotment Allotment(TimeSpan? freshness = null) => new(_database, _host)
    {
        Drive = _ => new StorageReport(500 * GB, 1000 * GB),
        Holding = _ => _held,
        Freshness = freshness ?? TimeSpan.FromMinutes(5)
    };

    private SyncService Sync(StorageAllotment allotment) =>
        new(_syncthing, _ownership, _host, _users, NullLogger<SyncService>.Instance, allotment);

    private async Task<SyncthingFolder> ShareAsync(SyncService? sync = null)
    {
        sync ??= _sync;
        await sync.PairAsync(_ada.Id, Laptop, "Ada’s laptop", CancellationToken.None);
        var shared = await sync.ShareAsync(_ada.Id, _adaRoot, "Documents", null, CancellationToken.None);
        return _syncthing.Folders[shared.Id];
    }

    [Fact]
    public async Task A_folder_made_while_an_allotment_is_set_is_held_to_it_from_the_start()
    {
        _allotment.Set(new Allotment(AllotmentUnit.Bytes, 100 * GB));

        // 520 GB Uncloud could have, less 100: the drive keeps 420 GB free.
        Assert.Equal(420 * GB, (await ShareAsync()).KeepFree);
    }

    [Fact]
    public async Task Changing_the_allotment_or_taking_it_away_reaches_every_folder()
    {
        var folder = (await ShareAsync()).Id;
        Assert.Null(_syncthing.Folders[folder].KeepFree);

        _allotment.Set(new Allotment(AllotmentUnit.Bytes, 100 * GB));
        await _sync.HoldToAllotmentAsync(CancellationToken.None);
        Assert.Equal(420 * GB, _syncthing.Folders[folder].KeepFree);

        _allotment.Set(new Allotment(AllotmentUnit.Percent, 50));
        await _sync.HoldToAllotmentAsync(CancellationToken.None);
        Assert.Equal(260 * GB, _syncthing.Folders[folder].KeepFree);

        // Back to the hundredth of the drive Syncthing keeps by itself.
        _allotment.Set(null);
        await _sync.HoldToAllotmentAsync(CancellationToken.None);
        Assert.Null(_syncthing.Folders[folder].KeepFree);
    }

    [Fact]
    public async Task A_few_megabytes_written_elsewhere_doesnt_restart_a_folder()
    {
        _allotment.Set(new Allotment(AllotmentUnit.Bytes, 100 * GB));
        var folder = (await ShareAsync()).Id;
        await _sync.HoldToAllotmentAsync(CancellationToken.None);
        Assert.Equal(0, _syncthing.KeepFreeChanges);

        // Walked again and found a little more than before: close enough to leave alone.
        _held += 100 * 1024 * 1024;
        _allotment.Invalidate();
        await _sync.HoldToAllotmentAsync(CancellationToken.None);
        Assert.Equal(0, _syncthing.KeepFreeChanges);

        // A gigabyte is not.
        _held += GB;
        _allotment.Invalidate();
        await _sync.HoldToAllotmentAsync(CancellationToken.None);
        Assert.Equal(1, _syncthing.KeepFreeChanges);
        Assert.Equal(_held + 500 * GB - 100 * GB, _syncthing.Folders[folder].KeepFree);
    }

    [Fact]
    public async Task A_folder_in_Syncthing_that_isnt_Uncloud_s_is_left_as_it_is()
    {
        var ours = (await ShareAsync()).Id;
        // Added in Syncthing's own interface, somewhere else entirely, and set to what its owner chose.
        _syncthing.Folders["elsewhere"] = new SyncthingFolder("elsewhere", "Elsewhere",
            Path.Combine(_temporary, "Elsewhere"), [], "idle", null, 0, 0, true, KeepFree: 5 * GB);
        _allotment.Set(new Allotment(AllotmentUnit.Bytes, 100 * GB));

        await _sync.HoldToAllotmentAsync(CancellationToken.None);
        await _sync.ReconcileAsync(CancellationToken.None);
        Assert.Equal(420 * GB, _syncthing.Folders[ours].KeepFree);
        Assert.Equal(5 * GB, _syncthing.Folders["elsewhere"].KeepFree);

        _allotment.Set(null);
        await _sync.HoldToAllotmentAsync(CancellationToken.None);
        Assert.Null(_syncthing.Folders[ours].KeepFree);
        Assert.Equal(5 * GB, _syncthing.Folders["elsewhere"].KeepFree);
    }

    [Fact]
    public async Task Checking_on_a_stale_walk_walks_again_and_acts_on_what_it_finds_at_once()
    {
        // Every walk stale as soon as it's made, as one is ten minutes later.
        var allotment = Allotment(TimeSpan.Zero);
        var sync = Sync(allotment);
        allotment.Set(new Allotment(AllotmentUnit.Bytes, 100 * GB));
        var folder = (await ShareAsync(sync)).Id;
        Assert.Equal(420 * GB, _syncthing.Folders[folder].KeepFree);

        // 50 GB put there by hand: the drive can't say it was Uncloud's, only a walk can.
        _held += 50 * GB;
        await sync.HoldToAllotmentAsync(CancellationToken.None);

        Assert.Equal(470 * GB, _syncthing.Folders[folder].KeepFree);
    }

    [Fact]
    public async Task Starting_up_holds_folders_made_before_there_was_an_allotment()
    {
        var folder = (await ShareAsync()).Id;
        _allotment.Set(new Allotment(AllotmentUnit.Bytes, 100 * GB));

        await _sync.ReconcileAsync(CancellationToken.None);

        Assert.Equal(420 * GB, _syncthing.Folders[folder].KeepFree);
    }

    [Fact]
    public async Task Files_held_back_for_room_are_counted_where_the_folder_is_shown()
    {
        var folder = (await ShareAsync()).Id;
        _syncthing.Folders[folder] = _syncthing.Folders[folder] with { WaitingForRoom = 3 };

        var status = await _sync.StatusAsync(_ada.Id, CancellationToken.None);

        Assert.Equal(3, Assert.Single(status.Folders).WaitingForRoom);
    }

    [Fact]
    public async Task Syncthing_is_told_in_its_own_units_and_read_back_the_same_way()
    {
        var syncthing = new ScriptedSyncthing();
        var api = new SyncthingApi(new HttpClient(syncthing), syncthing);

        await api.SetKeepFreeAsync("docs", 420 * GB, CancellationToken.None);
        await api.SetKeepFreeAsync("docs", null, CancellationToken.None);
        await api.AddFolderAsync("new", "New", "/somewhere", [Laptop], 2 * GB, CancellationToken.None);

        // Millions of bytes, rounded up so it never leaves less than asked.
        Assert.Equal("""{"minDiskFree":{"value":450972,"unit":"MB"}}""", syncthing.Sent[0]);
        Assert.Equal("""{"minDiskFree":{"value":1,"unit":"%"}}""", syncthing.Sent[1]);
        using (var added = JsonDocument.Parse(syncthing.Sent[2]))
            Assert.Equal(2148, added.RootElement.GetProperty("minDiskFree").GetProperty("value").GetDouble());

        var folders = await api.FoldersAsync(CancellationToken.None);
        var held = folders.Single(folder => folder.Id == "docs");
        Assert.Equal(420_000L * 1_000_000, held.KeepFree);
        // Two of its three errors are for want of room; the third is something else.
        Assert.Equal(2, held.WaitingForRoom);
        var other = folders.Single(folder => folder.Id == "photos");
        Assert.Null(other.KeepFree);
        Assert.Equal(0, other.WaitingForRoom);
        // A folder with no errors isn't asked about them.
        Assert.DoesNotContain("/rest/folder/errors?folder=photos", syncthing.Asked);
    }

    [Fact]
    public async Task Saving_an_allotment_tells_Syncthing_at_once()
    {
        var config = Path.Combine(_temporary, "AppConfig");
        var syncthing = new FakeSyncthing();
        using var app = new TestHost(config, syncthing: syncthing);
        using var admin = await app.SignUpAsync("ada");
        await TestHost.SetHostRootAsync(admin, _host.RequireRoot());
        (await admin.PostAsJsonAsync("/api/sync/devices", new { deviceId = Laptop, name = "Laptop" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/sync/folders", new { path = "" })).EnsureSuccessStatusCode();
        var folder = Assert.Single(syncthing.Folders).Key;
        Assert.Null(syncthing.Folders[folder].KeepFree);

        (await admin.PutAsJsonAsync("/api/host/storage", new { bytes = 1024 * 1024 })).EnsureSuccessStatusCode();
        Assert.NotNull(syncthing.Folders[folder].KeepFree);

        (await admin.PutAsJsonAsync("/api/host/storage", new { })).EnsureSuccessStatusCode();
        Assert.Null(syncthing.Folders[folder].KeepFree);
    }

    /// <summary>Syncthing's REST API, as far as these need it, keeping what was sent to it.</summary>
    private sealed class ScriptedSyncthing : HttpMessageHandler, ISyncthingEndpoint
    {
        public List<string> Sent { get; } = [];
        public List<string> Asked { get; } = [];

        public bool IsReady => true;
        public string? Unavailable => null;
        public Uri? BaseAddress => new("http://127.0.0.1:8390/");
        public string? ApiKey => "key";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Asked.Add(path);
            if (request.Content is not null) Sent.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            var body = path switch
            {
                "/rest/system/status" => """{"myID":"SELF"}""",
                "/rest/config/folders" when request.Method == HttpMethod.Get => """
                    [
                      {"id":"docs","label":"Docs","path":"/d","devices":[],"versioning":{"type":"staggered"},
                       "minDiskFree":{"value":420000,"unit":"MB"}},
                      {"id":"photos","label":"Photos","path":"/p","devices":[],"versioning":{"type":"staggered"},
                       "minDiskFree":{"value":1,"unit":"%"}}
                    ]
                    """,
                "/rest/db/status?folder=docs" => """{"state":"idle","errors":3,"localFiles":4,"localBytes":10}""",
                "/rest/db/status?folder=photos" => """{"state":"idle","errors":0,"localFiles":1,"localBytes":1}""",
                "/rest/folder/errors?folder=docs" => """
                    {"folder":"docs","errors":[
                      {"path":"a.mov","error":"insufficient space in folder \"Docs\" (/d): current 1.0 GB < required 420000 MB"},
                      {"path":"b.mov","error":"insufficient space in folder \"Docs\" (/d): current 1.0 GB < required 420000 MB"},
                      {"path":"c.txt","error":"permission denied"}],"page":1,"perpage":65536}
                    """,
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }
}
