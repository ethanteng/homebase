using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homebase.Tests;

/// <summary>
/// Syncing one account's folders with that person's own computers, through a Syncthing whose
/// configuration is shared by the whole host. Every test here is about one of two things: what
/// changes on a laptop reaches the host, and nobody reaches anybody else's.
/// </summary>
public sealed class SyncTests : IDisposable
{
    private const string Laptop = "ZZZZZZZ-YYYYYYY-XXXXXXX-WWWWWWW-VVVVVVV-UUUUUUU-TTTTTTT-SSSSSSS";
    private const string Desktop = "QQQQQQQ-RRRRRRR-SSSSSSS-TTTTTTT-UUUUUUU-VVVVVVV-WWWWWWW-XXXXXXX";

    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSyncthing _syncthing = new();
    private readonly UserStore _users;
    private readonly HostService _host;
    private readonly SyncOwnership _ownership;
    private readonly SyncService _sync;
    private readonly UserAccount _ada;
    private readonly UserAccount _bo;
    private readonly string _adaRoot;
    private readonly string _boRoot;

    static SyncTests() => PasswordHasher.Override = 1_000;

    public SyncTests()
    {
        Directory.CreateDirectory(_temporary);
        var database = new ControlDatabase(Path.Combine(_temporary, "Config"));
        _users = new UserStore(database);
        _host = new HostService(database);
        _host.SelectRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _ownership = new SyncOwnership(database);
        _sync = new SyncService(_syncthing, _ownership, _host, _users, NullLogger<SyncService>.Instance);
        _ada = _users.CreateFirstAdmin("ada", "Ada", TestHost.Password);
        _bo = _users.Create("bo", "Bo", TestHost.Password, false);
        _adaRoot = UserPaths.RootFor(_host.RequireRoot(), _ada.Id);
        _boRoot = UserPaths.RootFor(_host.RequireRoot(), _bo.Id);
        Directory.CreateDirectory(Path.Combine(_adaRoot, "Documents", "Taxes"));
        Directory.CreateDirectory(Path.Combine(_adaRoot, ".homebase"));
    }

    [Fact]
    public async Task Pairing_checks_the_device_id_and_accepts_it_however_it_was_copied()
    {
        foreach (var id in new[] { "", "not-a-device-id", "AAAAAAA-BBBBBBB", Laptop.Replace('Z', '1') })
            Assert.Equal("invalid_device", (await Assert.ThrowsAsync<LibraryException>(
                () => _sync.PairAsync(_ada.Id, id, null, CancellationToken.None))).Code);
        // The host's own ID is a mistake worth naming rather than a computer to pair with.
        Assert.Equal("invalid_device", (await Assert.ThrowsAsync<LibraryException>(
            () => _sync.PairAsync(_ada.Id, FakeSyncthing.Self, null, CancellationToken.None))).Code);

        await _sync.PairAsync(_ada.Id, Laptop.Replace("-", "").ToLowerInvariant(), " Ada’s laptop ", CancellationToken.None);

        Assert.Equal("Ada’s laptop", Assert.Single(_syncthing.Devices).Value.Name);
        Assert.True(_syncthing.Devices.ContainsKey(Laptop));
        Assert.Equal("conflict", (await Assert.ThrowsAsync<LibraryException>(
            () => _sync.PairAsync(_ada.Id, Laptop, null, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task A_computer_belongs_to_one_account_and_the_refusal_says_nothing_about_whose()
    {
        await _sync.PairAsync(_ada.Id, Laptop, "Ada’s laptop", CancellationToken.None);

        var refused = await Assert.ThrowsAsync<LibraryException>(
            () => _sync.PairAsync(_bo.Id, Laptop, "Mine now", CancellationToken.None));

        Assert.Equal("conflict", refused.Code);
        Assert.DoesNotContain("ada", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_ada.Id, _ownership.FindDevice(Laptop)!.UserId);
        Assert.Equal("Ada’s laptop", _syncthing.Devices[Laptop].Name);
    }

    [Fact]
    public async Task Each_account_sees_only_its_own_computers_folders_and_offers()
    {
        await _sync.PairAsync(_ada.Id, Laptop, "Ada’s laptop", CancellationToken.None);
        await _sync.ShareAsync(_ada.Id, _adaRoot, "Documents", null, CancellationToken.None);
        _syncthing.Offers.Add(new SyncthingOffer("ada-photos", "Photos", Laptop));
        await _sync.PairAsync(_bo.Id, Desktop, "Bo’s desktop", CancellationToken.None);

        var bo = await _sync.StatusAsync(_bo.Id, CancellationToken.None);
        var ada = await _sync.StatusAsync(_ada.Id, CancellationToken.None);

        Assert.Equal(Desktop, Assert.Single(bo.Devices).DeviceId);
        Assert.Empty(bo.Folders);
        Assert.Empty(bo.Offers);
        Assert.Equal(Laptop, Assert.Single(ada.Devices).DeviceId);
        Assert.Equal("Documents", Assert.Single(ada.Folders).Path);
        Assert.Equal("ada-photos", Assert.Single(ada.Offers).FolderId);
        Assert.Equal(FakeSyncthing.Self, bo.HostDeviceId);
    }

    [Fact]
    public async Task The_whole_account_folder_syncs_both_ways_without_ever_carrying_the_index()
    {
        await _sync.PairAsync(_ada.Id, Laptop, null, CancellationToken.None);

        var folder = await _sync.ShareAsync(_ada.Id, _adaRoot, "", null, CancellationToken.None);

        var configured = _syncthing.Folders[folder.Id];
        Assert.Equal(_adaRoot, configured.Path);
        Assert.Equal("Uncloud", configured.Label);
        Assert.Equal([Laptop], configured.DeviceIds);
        // The ignore file was already there when Syncthing first saw the folder, so there is no
        // first scan that could pick up a live SQLite database.
        Assert.Contains(".homebase", _syncthing.IgnoresWhenAdded[folder.Id]!.Split('\n').Select(line => line.Trim()));
        Assert.True(configured.Versioned);
    }

    [Fact]
    public async Task An_existing_ignore_file_is_added_to_rather_than_replaced()
    {
        await _sync.PairAsync(_ada.Id, Laptop, null, CancellationToken.None);
        var ignore = Path.Combine(_adaRoot, "Documents", SyncService.IgnoreFile);
        await File.WriteAllTextAsync(ignore, "*.tmp\n");

        await _sync.ShareAsync(_ada.Id, _adaRoot, "Documents", null, CancellationToken.None);
        SyncService.GuardMetadata(Path.Combine(_adaRoot, "Documents"));

        var lines = await File.ReadAllLinesAsync(ignore);
        Assert.Equal("*.tmp", lines[0]);
        Assert.Single(lines, line => line == ".homebase");
    }

    [Fact]
    public async Task Syncing_stays_inside_the_account_folder()
    {
        await _sync.PairAsync(_ada.Id, Laptop, null, CancellationToken.None);
        await _sync.PairAsync(_bo.Id, Desktop, null, CancellationToken.None);

        foreach (var path in new[] { "../outside", "Documents/../../outside", ".homebase", "Documents/.hidden", $"../{_bo.Id}", "../../users" })
            await Assert.ThrowsAsync<LibraryException>(() => _sync.ShareAsync(_ada.Id, _adaRoot, path, null, CancellationToken.None));
        Assert.Equal("not_found", (await Assert.ThrowsAsync<LibraryException>(
            () => _sync.ShareAsync(_ada.Id, _adaRoot, "Missing", null, CancellationToken.None))).Code);
        Assert.Empty(_syncthing.Folders);
    }

    [Fact]
    public async Task A_folder_can_only_go_to_your_own_computers()
    {
        Assert.Equal("no_devices", (await Assert.ThrowsAsync<LibraryException>(
            () => _sync.ShareAsync(_ada.Id, _adaRoot, "Documents", null, CancellationToken.None))).Code);
        await _sync.PairAsync(_bo.Id, Desktop, null, CancellationToken.None);

        // Naming somebody else's computer gets the same answer as naming one that doesn't exist.
        Assert.Equal("not_found", (await Assert.ThrowsAsync<LibraryException>(
            () => _sync.ShareAsync(_ada.Id, _adaRoot, "Documents", [Desktop], CancellationToken.None))).Code);
        Assert.Empty(_syncthing.Folders);
    }

    [Fact]
    public async Task Folders_inside_or_around_one_already_syncing_are_refused()
    {
        await _sync.PairAsync(_ada.Id, Laptop, null, CancellationToken.None);
        await _sync.ShareAsync(_ada.Id, _adaRoot, "Documents", null, CancellationToken.None);

        foreach (var path in new[] { "Documents", "Documents/Taxes", "" })
            Assert.Equal("conflict", (await Assert.ThrowsAsync<LibraryException>(
                () => _sync.ShareAsync(_ada.Id, _adaRoot, path, null, CancellationToken.None))).Code);
        Assert.Single(_syncthing.Folders);
    }

    [Fact]
    public void The_same_path_in_two_accounts_is_two_different_folders()
    {
        Assert.NotEqual(SyncService.FolderId(_ada.Id, "Documents"), SyncService.FolderId(_bo.Id, "Documents"));
        Assert.Equal(SyncService.FolderId(_ada.Id, "Documents"), SyncService.FolderId(_ada.Id, "documents"));
        Assert.StartsWith("uncloud-documents-", SyncService.FolderId(_ada.Id, "Documents"));
    }

    [Fact]
    public async Task A_folder_offered_by_your_computer_is_kept_where_you_say()
    {
        await _sync.PairAsync(_ada.Id, Laptop, null, CancellationToken.None);
        _syncthing.Offers.Add(new SyncthingOffer("laptop-desktop", "Desktop", Laptop));

        var folder = await _sync.AcceptAsync(_ada.Id, _adaRoot, "laptop-desktop", "From laptop/Desktop", CancellationToken.None);

        Assert.Equal("From laptop/Desktop", folder.Path);
        Assert.True(Directory.Exists(Path.Combine(_adaRoot, "From laptop", "Desktop")));
        Assert.Equal(Path.Combine(_adaRoot, "From laptop", "Desktop"), _syncthing.Folders["laptop-desktop"].Path);
        Assert.Equal([Laptop], _syncthing.Folders["laptop-desktop"].DeviceIds);
        Assert.NotNull(_syncthing.IgnoresWhenAdded["laptop-desktop"]);
    }

    [Fact]
    public async Task Another_accounts_offer_cannot_be_taken_up_and_an_offered_name_gets_the_same_rules()
    {
        await _sync.PairAsync(_ada.Id, Laptop, null, CancellationToken.None);
        await _sync.PairAsync(_bo.Id, Desktop, null, CancellationToken.None);
        _syncthing.Offers.Add(new SyncthingOffer("ada-photos", "Photos", Laptop));
        _syncthing.Offers.Add(new SyncthingOffer("sneaky", "../escape", Desktop));

        Assert.Equal("not_found", (await Assert.ThrowsAsync<LibraryException>(
            () => _sync.AcceptAsync(_bo.Id, _boRoot, "ada-photos", "Photos", CancellationToken.None))).Code);
        // The label comes from the other computer, so it is held to this account's rules too.
        await Assert.ThrowsAsync<LibraryException>(() => _sync.AcceptAsync(_bo.Id, _boRoot, "sneaky", null, CancellationToken.None));
        Assert.Equal("unsupported", (await Assert.ThrowsAsync<LibraryException>(
            () => _sync.AcceptAsync(_bo.Id, _boRoot, "sneaky", "/", CancellationToken.None))).Code);
        Assert.Empty(_syncthing.Folders);
        Assert.False(Directory.Exists(Path.Combine(_host.RequireRoot(), UserPaths.UsersDirectory, "escape")));
    }

    [Fact]
    public async Task Stopping_a_folder_leaves_its_files_and_only_its_owner_can()
    {
        await _sync.PairAsync(_ada.Id, Laptop, null, CancellationToken.None);
        var folder = await _sync.ShareAsync(_ada.Id, _adaRoot, "Documents", null, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_adaRoot, "Documents", "note.txt"), "keep me");

        Assert.Equal("not_found", (await Assert.ThrowsAsync<LibraryException>(
            () => _sync.StopAsync(_bo.Id, folder.Id, CancellationToken.None))).Code);
        Assert.Single(_syncthing.Folders);

        await _sync.StopAsync(_ada.Id, folder.Id, CancellationToken.None);

        Assert.Empty(_syncthing.Folders);
        Assert.Empty(_ownership.Folders(_ada.Id));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(_adaRoot, "Documents", "note.txt")));
    }

    [Fact]
    public async Task Unpairing_takes_the_computer_off_every_folder_and_only_its_owner_can()
    {
        await _sync.PairAsync(_ada.Id, Laptop, null, CancellationToken.None);
        await _sync.PairAsync(_ada.Id, Desktop, null, CancellationToken.None);
        var folder = await _sync.ShareAsync(_ada.Id, _adaRoot, "Documents", null, CancellationToken.None);

        Assert.Equal("not_found", (await Assert.ThrowsAsync<LibraryException>(
            () => _sync.UnpairAsync(_bo.Id, Laptop, CancellationToken.None))).Code);

        await _sync.UnpairAsync(_ada.Id, Laptop, CancellationToken.None);

        Assert.False(_syncthing.Devices.ContainsKey(Laptop));
        Assert.Equal([Desktop], _syncthing.Folders[folder.Id].DeviceIds);
        Assert.Null(_ownership.FindDevice(Laptop));
    }

    [Fact]
    public async Task A_disabled_accounts_computers_stop_syncing_until_it_is_enabled()
    {
        await _sync.PairAsync(_bo.Id, Desktop, null, CancellationToken.None);

        await _sync.SuspendAsync(_bo.Id, true, CancellationToken.None);
        Assert.True(_syncthing.Devices[Desktop].Paused);

        await _sync.SuspendAsync(_bo.Id, false, CancellationToken.None);
        Assert.False(_syncthing.Devices[Desktop].Paused);

        // Disabled while Syncthing wasn't listening: caught up as soon as it is.
        _users.SetDisabled(_bo.Id, true);
        await _sync.ReconcileAsync(CancellationToken.None);
        Assert.True(_syncthing.Devices[Desktop].Paused);
    }

    [Fact]
    public async Task Deleting_an_account_stops_everything_it_synced_but_not_while_syncthing_is_unreachable()
    {
        await _sync.PairAsync(_bo.Id, Desktop, null, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(_boRoot, "Notes"));
        await _sync.ShareAsync(_bo.Id, _boRoot, "Notes", null, CancellationToken.None);

        _syncthing.Down = "Syncthing isn’t running.";
        Assert.Equal("sync_unavailable", (await Assert.ThrowsAsync<LibraryException>(
            () => _sync.ForgetAsync(_bo.Id, () => _users.Delete(_bo.Id), CancellationToken.None))).Code);
        Assert.NotNull(_users.Find(_bo.Id));

        _syncthing.Down = null;
        await _sync.ForgetAsync(_bo.Id, () => _users.Delete(_bo.Id), CancellationToken.None);

        Assert.Null(_users.Find(_bo.Id));
        Assert.Empty(_syncthing.Folders);
        Assert.Empty(_syncthing.Devices);
        Assert.Empty(_ownership.Devices());
    }

    [Fact]
    public async Task An_account_that_cannot_be_deleted_keeps_syncing()
    {
        await _sync.PairAsync(_ada.Id, Laptop, null, CancellationToken.None);

        // Ada is the only administrator, so deleting her is refused, and her laptop stays paired.
        await Assert.ThrowsAsync<LibraryException>(
            () => _sync.ForgetAsync(_ada.Id, () => _users.Delete(_ada.Id), CancellationToken.None));

        Assert.True(_syncthing.Devices.ContainsKey(Laptop));
    }

    [Fact]
    public async Task Folders_from_before_syncing_was_per_account_go_to_the_account_they_are_in()
    {
        // What the administrator-only version left behind: in Syncthing, owned by nobody.
        Directory.CreateDirectory(Path.Combine(_boRoot, "Old share"));
        _syncthing.Devices[Desktop] = new SyncthingDevice(Desktop, "Bo’s desktop", false, null, false);
        _syncthing.Folders["homebase-old"] = new SyncthingFolder("homebase-old", "Old share",
            Path.Combine(_boRoot, "Old share"), [Desktop], "idle", null, 0, 0, false);
        _syncthing.Folders["elsewhere"] = new SyncthingFolder("elsewhere", "Elsewhere",
            _temporary, [], "idle", null, 0, 0, false);

        await _sync.ReconcileAsync(CancellationToken.None);

        var adopted = Assert.Single(_ownership.Folders());
        Assert.Equal((_bo.Id, "Old share"), (adopted.UserId, adopted.Path));
        Assert.Equal(_bo.Id, _ownership.FindDevice(Desktop)!.UserId);
        Assert.True(_syncthing.Folders["homebase-old"].Versioned);
        Assert.True(File.Exists(Path.Combine(_boRoot, "Old share", SyncService.IgnoreFile)));
        // A folder outside every account's space is left alone and shown to nobody.
        Assert.False(_syncthing.Folders["elsewhere"].Versioned);
        Assert.Empty((await _sync.StatusAsync(_ada.Id, CancellationToken.None)).Folders);
    }

    [Fact]
    public async Task Moving_the_hosts_folder_moves_where_synced_folders_point()
    {
        await _sync.PairAsync(_ada.Id, Laptop, null, CancellationToken.None);
        var folder = await _sync.ShareAsync(_ada.Id, _adaRoot, "Documents/Taxes", null, CancellationToken.None);

        var moved = _host.SelectRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Moved")).FullName);
        await _sync.ReconcileAsync(CancellationToken.None);

        Assert.Equal(Path.Combine(moved, UserPaths.UsersDirectory, _ada.Id, "Documents", "Taxes"),
            _syncthing.Folders[folder.Id].Path);
    }

    [Fact]
    public async Task Without_syncthing_the_panel_still_shows_what_you_set_up_and_changes_are_refused()
    {
        await _sync.PairAsync(_ada.Id, Laptop, "Ada’s laptop", CancellationToken.None);
        _syncthing.Down = "Syncthing isn’t installed.";

        var status = await _sync.StatusAsync(_ada.Id, CancellationToken.None);

        Assert.False(status.Available);
        Assert.Equal("Syncthing isn’t installed.", status.Detail);
        Assert.Equal("Ada’s laptop", Assert.Single(status.Devices).Name);
        Assert.Equal("sync_unavailable", (await Assert.ThrowsAsync<LibraryException>(
            () => _sync.PairAsync(_ada.Id, Desktop, null, CancellationToken.None))).Code);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }
}
