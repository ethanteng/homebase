using System.Diagnostics;
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

    [Fact]
    public async Task A_server_that_falls_over_is_explained_by_why_not_by_where()
    {
        // Windows has no shell to stand in for the server; CI runs this on Linux and macOS.
        if (!File.Exists("/bin/sh")) return;
        // What .NET prints when Kestrel can't have its port, down to the frame the menu used to show.
        var server = FakeServer(Path.Combine(_temporary, "crashes"), """
            #!/bin/sh
            echo 'Unhandled exception. System.IO.IOException: Failed to bind to address http://127.0.0.1:5210: address already in use.' >&2
            echo ' ---> Microsoft.AspNetCore.Connections.AddressInUseException: Address already in use' >&2
            echo '   at Program.<Main>$(String[] args) in /Users/runner/work/homebase/src/Homebase.Server/Program.cs:line 986' >&2
            exit 134
            """);
        await using var host = new HostServer(server, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            new HttpClient(), FreePort());

        var failure = await Assert.ThrowsAsync<IOException>(() => host.StartAsync(CancellationToken.None));

        Assert.Contains("address already in use", failure.Message);
        Assert.Equal("Failed to bind to address http://127.0.0.1:5210: address already in use.", host.LastError);
        Assert.DoesNotContain("Program.cs", host.LastError);
    }

    [Fact]
    public async Task Something_already_answering_is_not_taken_for_the_server_starting()
    {
        if (!File.Exists("/bin/sh")) return;
        // Something that isn't this app's server is already answering at its port — here, a
        // listener standing in for an Uncloud run from somewhere else. It isn't this app's to stop.
        var port = FreePort();
        using var other = Answer(port);
        var launched = Path.Combine(_temporary, "launched");
        var server = FakeServer(Path.Combine(_temporary, "never-started"), $"""
            #!/bin/sh
            touch '{launched}'
            sleep 30
            """);
        await using var host = new HostServer(server, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            new HttpClient(), port);

        // Launching beside it would only fail to bind, while the answer from the other one said
        // the new one was fine. It says what is wrong instead, before starting anything.
        var failure = await Assert.ThrowsAsync<IOException>(() => host.StartAsync(CancellationToken.None));

        Assert.Contains($"already answering at port {port}", failure.Message);
        Assert.Equal(failure.Message, host.LastError);
        Assert.False(host.IsRunning);
        Assert.False(File.Exists(launched));
        Assert.True(other.IsListening);
    }

    [Fact]
    public async Task A_server_an_earlier_copy_of_the_app_left_running_is_stopped_so_this_one_can_start()
    {
        // The case this is for, with the real server: one started the way the app starts it, whose
        // app went without stopping it, still holding the port.
        if (OperatingSystem.IsWindows() || !HasLsof) return;
        var port = FreePort();
        var settings = new Dictionary<string, string>
        {
            ["Homebase__ConfigDirectory"] = Path.Combine(_temporary, "HostConfig"),
            ["Homebase__Syncthing__Enabled"] = "false"
        };
        var servers = AppContext.BaseDirectory;
        using var leftover = StartServer(servers, port, settings);
        using var http = new HttpClient();
        await Answering(http, port);

        await using var host = new HostServer(servers, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            http, port, settings);
        await host.StartAsync(CancellationToken.None);

        Assert.True(leftover.WaitForExit(10_000));
        Assert.True(host.IsRunning);
        (await http.GetAsync($"http://127.0.0.1:{port}/api/health")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_same_program_on_another_port_is_not_taken_for_the_one_in_the_way()
    {
        // What stops a leftover is its holding this app's port, not only its being the same
        // program: an Uncloud from this same place, doing something else on another port, is left
        // alone even while something else is in the way here.
        if (OperatingSystem.IsWindows() || !HasLsof) return;
        var sleep = new[] { "/bin/sleep", "/usr/bin/sleep" }.FirstOrDefault(File.Exists);
        if (sleep is null) return;
        var directory = Directory.CreateDirectory(Path.Combine(_temporary, "same-program")).FullName;
        File.Copy(sleep, Path.Combine(directory, "Homebase.Server"));
        using var elsewhere = Process.Start(new ProcessStartInfo(Path.Combine(directory, "Homebase.Server"), "60")
        {
            UseShellExecute = false
        })!;
        var port = FreePort();
        using var other = Answer(port);

        try
        {
            await using var host = new HostServer(directory, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                new HttpClient(), port);
            await Assert.ThrowsAsync<IOException>(() => host.StartAsync(CancellationToken.None));

            Assert.False(elsewhere.HasExited);
        }
        finally
        {
            if (!elsewhere.HasExited) elsewhere.Kill();
        }
    }

    [Fact]
    public async Task The_server_stops_when_whoever_started_it_goes()
    {
        // The app holds one end of a pipe and never writes to it; the server reads the other. The
        // app going away, however it goes, is that end closing.
        using var app = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        using var server = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, app.ClientSafePipeHandle);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Homebase.Server.ParentWatch.StopWhenClosed(new StreamReader(server), () => stopped.TrySetResult());

        // Still there: nothing to do.
        await Task.Delay(300);
        Assert.False(stopped.Task.IsCompleted);

        app.Dispose();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static bool HasLsof => File.Exists("/usr/sbin/lsof") || File.Exists("/usr/bin/lsof");

    private static Process StartServer(string directory, int port, Dictionary<string, string> settings)
    {
        var start = new ProcessStartInfo(Path.Combine(directory, "Homebase.Server"))
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["Homebase__Port"] = port.ToString();
        foreach (var (name, value) in settings) start.Environment[name] = value;
        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task Answering(HttpClient http, int port)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if ((await http.GetAsync($"http://127.0.0.1:{port}/api/health")).IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(200);
        }
        throw new TimeoutException($"Nothing answered at port {port}.");
    }

    /// <summary>Something that isn't this app's server, answering at its port.</summary>
    private static System.Net.HttpListener Answer(int port)
    {
        var listener = new System.Net.HttpListener { Prefixes = { $"http://127.0.0.1:{port}/" } };
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try { var context = await listener.GetContextAsync(); context.Response.StatusCode = 200; context.Response.Close(); }
                catch (Exception) { return; }
            }
        });
        return listener;
    }

    private static string FakeServer(string directory, string script)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Homebase.Server");
        File.WriteAllText(path, script.Replace("\r\n", "\n") + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }
}
