using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Sync;
using Uncloud.Desktop;

namespace Homebase.Tests;

/// <summary>
/// The Uncloud app on somebody's computer: a code and an address in, a ~/Uncloud kept in step
/// with the host out, and nothing for the person to install, copy or accept along the way.
/// </summary>
public sealed class DesktopTests : IDisposable
{
    private const string Laptop = "ZZZZZZZ-YYYYYYY-XXXXXXX-WWWWWWW-VVVVVVV-UUUUUUU-TTTTTTT-SSSSSSS";

    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSyncthing _host = new();
    private readonly FakeSyncthing _computer = new();
    private readonly DesktopPaths _paths;

    public DesktopTests()
    {
        Directory.CreateDirectory(_temporary);
        _paths = new DesktopPaths(Path.Combine(_temporary, "AppData"), Path.Combine(_temporary, "Home", "Uncloud"));
    }

    [Fact]
    public void A_pairing_link_carries_the_address_and_the_code()
    {
        var link = PairingLink.Parse("uncloud://pair?address=https%3A%2F%2Fhome.example.ts.net&code=AB12C-DE34F");
        Assert.Equal(new Uri("https://home.example.ts.net"), link.Address);
        Assert.Equal("AB12C-DE34F", link.Code);
        Assert.Equal(link, PairingLink.Parse(link.ToLink()));

        // Typed by hand, an address without a scheme is taken as https, and a path is dropped.
        Assert.Equal(new Uri("https://uncloud.local:5210"), PairingLink.From(" uncloud.local:5210/files ", "x").Address);
        Assert.Equal(new Uri("http://192.168.1.10:5210"), PairingLink.From("http://192.168.1.10:5210", "x").Address);

        foreach (var bad in new[] { "", "https://example.com/pair?code=x", "uncloud://other?address=a&code=b", "uncloud://pair?address=ftp%3A%2F%2Fa&code=b", "uncloud://pair?address=a" })
            Assert.Throws<FormatException>(() => PairingLink.Parse(bad));
    }

    [Fact]
    public async Task A_code_from_the_web_pairs_the_app_and_it_syncs_the_whole_folder_into_home()
    {
        using var app = new TestHost(Path.Combine(_temporary, "Config"), syncthing: _host);
        using var owner = await app.SignUpAsync("ada");
        await TestHost.SetHostRootAsync(owner, PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName));
        var code = (await (await owner.PostAsync("/api/sync/pairing-codes", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString()!;

        // The app talks to the same address the person opened Uncloud at, with no session.
        using var anonymous = app.CreateClient();
        var result = await new PairingClient(anonymous)
            .PairAsync(PairingLink.From(anonymous.BaseAddress!.ToString(), code), Laptop, "Ada’s MacBook", CancellationToken.None);

        Assert.Equal(FakeSyncthing.Self, result.HostDeviceId);
        Assert.Equal("ada", result.AccountName);
        var shared = Assert.Single(result.Folders);
        Assert.Equal([Laptop], _host.Folders[shared.Id].DeviceIds);
        Assert.Equal("Ada’s MacBook", _host.Devices[Laptop].Name);

        var computer = new ComputerSync(_computer, _paths);
        await computer.ApplyAsync(result, CancellationToken.None);

        Assert.Equal([FakeSyncthing.Self], _computer.Devices.Keys);
        Assert.Contains(FakeSyncthing.Self, _computer.AutoAccepting);
        Assert.Equal(_paths.Files, _computer.DefaultFolderPath);
        var kept = _computer.Folders[shared.Id];
        Assert.Equal(_paths.Files, kept.Path);
        Assert.Equal([FakeSyncthing.Self], kept.DeviceIds);
        Assert.True(Directory.Exists(_paths.Files));

        // Doing it again — the app restarting mid-setup, say — changes nothing.
        await computer.ApplyAsync(result, CancellationToken.None);
        Assert.Single(_computer.Folders);
        Assert.Single(_computer.Devices);
    }

    [Fact]
    public async Task A_refusal_from_Uncloud_reaches_the_person_in_its_own_words()
    {
        using var app = new TestHost(Path.Combine(_temporary, "Config"), syncthing: _host);
        using var owner = await app.SignUpAsync("ada");
        using var anonymous = app.CreateClient();

        var refused = await Assert.ThrowsAsync<PairingException>(() => new PairingClient(anonymous)
            .PairAsync(PairingLink.From(anonymous.BaseAddress!.ToString(), "AAAAA-AAAAA"), Laptop, "Laptop", CancellationToken.None));

        Assert.Contains("pairing code isn’t valid", refused.Message);
        Assert.Empty(_host.Devices);
    }

    [Fact]
    public async Task A_synced_folder_lands_where_it_is_on_the_host_so_two_of_the_same_name_stay_apart()
    {
        var computer = new ComputerSync(_computer, _paths);
        await computer.ApplyAsync(new PairingResult(FakeSyncthing.Self, "Ada",
        [
            new PairedFolder("uncloud-documents-1", "Documents", "Work/Documents"),
            new PairedFolder("uncloud-documents-2", "Documents", "Personal/Documents"),
            new PairedFolder("uncloud-taxes-3", "Taxes", "Taxes")
        ]), CancellationToken.None);

        Assert.Equal(Path.Combine(_paths.Files, "Work", "Documents"), _computer.Folders["uncloud-documents-1"].Path);
        Assert.Equal(Path.Combine(_paths.Files, "Personal", "Documents"), _computer.Folders["uncloud-documents-2"].Path);
        Assert.Equal(Path.Combine(_paths.Files, "Taxes"), _computer.Folders["uncloud-taxes-3"].Path);
    }

    [Fact]
    public async Task A_folder_the_host_names_outside_home_is_refused()
    {
        var computer = new ComputerSync(_computer, _paths);
        foreach (var path in new[] { "../Desktop", "Work/../../.ssh", "./x" })
            await Assert.ThrowsAsync<InvalidDataException>(() => computer.ApplyAsync(
                new PairingResult(FakeSyncthing.Self, "Ada", [new PairedFolder("evil", "Evil", path)]), CancellationToken.None));
        Assert.Empty(_computer.Folders);
    }

    [Fact]
    public async Task A_computer_already_syncing_with_one_Uncloud_refuses_to_pair_with_another()
    {
        new DesktopSettings { Mode = DesktopMode.Computer, Address = "https://first.example", AccountName = "Ada", HostDeviceId = FakeSyncthing.Self }.Save(_paths);
        await using var controller = new DesktopController(_paths, _temporary, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, new HttpClient());

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.PairAsync(PairingLink.From("https://second.example", "AB12C-DE34F"), "Laptop", CancellationToken.None));

        Assert.Contains("https://first.example", refused.Message);
        Assert.Equal("https://first.example", DesktopSettings.Load(_paths).Address);
    }

    [Fact]
    public async Task A_host_that_cannot_start_is_not_remembered_as_one()
    {
        // No server where the app looks for one: starting fails, as a taken port would.
        await using var controller = new DesktopController(_paths, Path.Combine(_temporary, "no-server-here"),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, new HttpClient());

        await Assert.ThrowsAnyAsync<Exception>(() => controller.BecomeHostAsync(CancellationToken.None));

        Assert.Equal(DesktopMode.Unset, controller.Settings.Mode);
        Assert.Equal(DesktopMode.Unset, DesktopSettings.Load(_paths).Mode);
        Assert.Null(controller.Server);
    }

    [Fact]
    public async Task The_menu_bar_says_how_things_stand()
    {
        var computer = new ComputerSync(_computer, _paths);
        Assert.Equal("Not paired with an Uncloud", (await computer.StatusAsync(FakeSyncthing.Self, CancellationToken.None)).Summary);

        await computer.ApplyAsync(new PairingResult(FakeSyncthing.Self, "Ada", [new PairedFolder("root", "Uncloud", "")]), CancellationToken.None);
        Assert.Equal("Can’t reach your Uncloud right now", (await computer.StatusAsync(FakeSyncthing.Self, CancellationToken.None)).Summary);

        _computer.Devices[FakeSyncthing.Self] = _computer.Devices[FakeSyncthing.Self] with { Connected = true };
        Assert.Equal("Up to date", (await computer.StatusAsync(FakeSyncthing.Self, CancellationToken.None)).Summary);

        _computer.Folders["root"] = _computer.Folders["root"] with { State = "syncing" };
        var syncing = await computer.StatusAsync(FakeSyncthing.Self, CancellationToken.None);
        Assert.Equal(("Syncing…", true), (syncing.Summary, syncing.Busy));

        _computer.Folders["root"] = _computer.Folders["root"] with { State = "error", Error = "folder path missing" };
        Assert.Equal("Problem with Uncloud: folder path missing", (await computer.StatusAsync(FakeSyncthing.Self, CancellationToken.None)).Summary);

        await computer.PauseAsync(FakeSyncthing.Self, true, CancellationToken.None);
        Assert.Equal("Paused", (await computer.StatusAsync(FakeSyncthing.Self, CancellationToken.None)).Summary);
    }

    [Fact]
    public async Task Disconnecting_stops_syncing_and_leaves_the_files()
    {
        var computer = new ComputerSync(_computer, _paths);
        await computer.ApplyAsync(new PairingResult(FakeSyncthing.Self, "Ada", [new PairedFolder("root", "Uncloud", "")]), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_paths.Files, "note.txt"), "mine");

        await computer.ForgetAsync(FakeSyncthing.Self, CancellationToken.None);

        Assert.Empty(_computer.Folders);
        Assert.Empty(_computer.Devices);
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(_paths.Files, "note.txt")));
    }

    [Fact]
    public void Settings_survive_a_restart_and_a_broken_file_starts_over()
    {
        new DesktopSettings { Mode = DesktopMode.Computer, Address = "https://home.example", HostDeviceId = FakeSyncthing.Self, AccountName = "Ada" }.Save(_paths);

        var loaded = DesktopSettings.Load(_paths);
        Assert.True(loaded.IsPaired);
        Assert.Equal(("https://home.example", "Ada"), (loaded.Address, loaded.AccountName));

        File.WriteAllText(_paths.SettingsFile, "{ not json");
        Assert.Equal(DesktopMode.Unset, DesktopSettings.Load(_paths).Mode);
    }

    [Fact]
    public void Opening_at_login_is_a_launch_agent_that_starts_the_app()
    {
        var item = new LoginItem(Path.Combine(_temporary, "LaunchAgents"), "/Applications/Uncloud & Co.app/Contents/MacOS/Uncloud");
        Assert.False(item.IsEnabled);

        item.Set(true);
        var plist = File.ReadAllText(item.PlistPath);
        Assert.Contains("<string>life.uncloud.app</string>", plist);
        // The path is escaped, so an app somewhere with an ampersand in its name still starts.
        Assert.Contains("/Applications/Uncloud &amp; Co.app/Contents/MacOS/Uncloud", plist);
        Assert.Contains("<key>RunAtLoad</key><true/>", plist);
        System.Xml.Linq.XDocument.Parse(plist);

        item.Set(false);
        Assert.False(item.IsEnabled);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }
}
