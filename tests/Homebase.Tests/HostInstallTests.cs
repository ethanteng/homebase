using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Uncloud.Desktop;

namespace Homebase.Tests;

/// <summary>
/// The Mac app setting the host up to run Uncloud in an account of its own: what it asks the
/// script to do, how it carries a folder's name there and back intact, and what it does — and
/// doesn't — run itself once the service is there. The script itself needs an administrator, so
/// it is run for real in CI rather than here.
/// </summary>
public sealed class HostInstallTests : IAsyncDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly DesktopPaths _paths;
    private readonly string _hostConfig;
    private readonly FakeInstall _install = new();
    private readonly List<DesktopController> _controllers = [];

    public HostInstallTests()
    {
        Directory.CreateDirectory(_temporary);
        _paths = new DesktopPaths(Path.Combine(_temporary, "AppData"), Path.Combine(_temporary, "Home", "Uncloud"));
        _hostConfig = Path.Combine(_temporary, "Homebase");
    }

    private DesktopController Controller(Func<Homebase.Core.PowerState?>? power = null)
    {
        var controller = new DesktopController(_paths, Path.Combine(_temporary, "no-server-here"),
            NullLoggerFactory.Instance, new HttpClient(), _install, _hostConfig, power ?? (() => null));
        _controllers.Add(controller);
        return controller;
    }

    // Everything a folder's name might hold that means something to a shell or to AppleScript.
    private const string Awkward = "/Volumes/Ada’s \"Drive\"/it's \\ $HOME `here` & <there> 写真";

    [Fact]
    public async Task A_folder_name_reaches_the_script_exactly_as_it_was_chosen()
    {
        // A stand-in for the script that says back what it was given, one argument a line.
        var script = Path.Combine(_temporary, "says back.sh");
        await File.WriteAllTextAsync(script, "printf '%s\\n' \"$@\"\n");
        var command = HostInstall.ShellCommand(script, "prepare", [("--folder", Awkward)]);

        var said = await RunAsync("/bin/sh", "-c", command);
        Assert.Equal(["prepare", "--folder", Awkward], said.TrimEnd('\n').Split('\n'));

        // And through AppleScript, as the app runs it, minus the administrator.
        if (!OperatingSystem.IsMacOS()) return;
        var apple = HostInstall.AppleScript(command, "Uncloud wants to “check” \\ this")
            .Replace(" with prompt \"Uncloud wants to “check” \\\\ this\" with administrator privileges", "");
        said = await RunAsync("/usr/bin/osascript", "-e", apple);
        Assert.Equal(["prepare", "--folder", Awkward], said.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public async Task In_Terminal_the_script_gets_the_same_folder_name_and_the_app_hears_how_it_went()
    {
        // Run as Terminal would run it, minus sudo, which needs a person to type a password.
        var script = Path.Combine(_temporary, "says back.sh");
        await File.WriteAllTextAsync(script, "printf '%s\\n' \"$@\"; echo 'and a complaint' >&2; exit \"${EXIT:-0}\"\n");
        var command = HostInstall.ShellCommand(script, "prepare", [("--folder", Awkward)]);
        var result = Path.Combine(_temporary, "result");
        var error = Path.Combine(_temporary, "error");
        var status = Path.Combine(_temporary, "status");
        var decision = Path.Combine(_temporary, "decision");
        var terminal = Path.Combine(_temporary, "Uncloud.command");
        await File.WriteAllTextAsync(terminal, HostInstall.TerminalScript(command, result, error, status, decision)
            .Replace("sudo -v", "true").Replace("sudo -n ", "").Replace("clear\n", ""));

        await RunAsync("/bin/bash", terminal);
        Assert.Equal("0", (await File.ReadAllTextAsync(status)).Trim());
        Assert.Equal(["prepare", "--folder", Awkward], (await File.ReadAllTextAsync(result)).TrimEnd('\n').Split('\n'));
        Assert.Equal("and a complaint", (await File.ReadAllTextAsync(error)).Trim());
        // It went ahead, and said so first, so a cancel arriving now can't pretend it didn't.
        Assert.False(HostInstall.Decide(decision));

        // A failure is told apart, with what the script said.
        File.Delete(status);
        File.Delete(decision);
        await File.WriteAllTextAsync(terminal, "export EXIT=3\n" + await File.ReadAllTextAsync(terminal));
        await RunAsync("/bin/bash", terminal);
        Assert.Equal("3", (await File.ReadAllTextAsync(status)).Trim());
    }

    [Fact]
    public async Task Cancelling_before_Terminal_begins_means_it_never_does()
    {
        // The app cancelled while Terminal was still waiting for a password. Typing it afterwards
        // changes nothing: the script finds the decision already made, and doesn't run.
        var ran = Path.Combine(_temporary, "ran");
        var script = Path.Combine(_temporary, "would change things.sh");
        await File.WriteAllTextAsync(script, $"touch {HostInstall.Quote(ran)}\n");
        var status = Path.Combine(_temporary, "status");
        var decision = Path.Combine(_temporary, "decision");
        var terminal = Path.Combine(_temporary, "Uncloud.command");
        await File.WriteAllTextAsync(terminal, HostInstall.TerminalScript(
                HostInstall.ShellCommand(script, "install", []), Path.Combine(_temporary, "result"),
                Path.Combine(_temporary, "error"), status, decision)
            .Replace("sudo -v", "true").Replace("sudo -n ", "").Replace("clear\n", ""));

        Assert.True(HostInstall.Decide(decision));
        await RunAsync("/bin/bash", terminal);
        Assert.Equal("cancelled", (await File.ReadAllTextAsync(status)).Trim());
        Assert.False(File.Exists(ran));
    }

    [Fact]
    public async Task Each_step_can_ask_for_the_password_in_Terminal_instead()
    {
        var controller = Controller();
        await controller.BecomeHostAsync(null, CancellationToken.None, inTerminal: true);
        Assert.True(_install.LastInTerminal);
        await controller.KeepPrivateAsync(null, CancellationToken.None);
        Assert.False(_install.LastInTerminal);
        await controller.PrepareFolderAsync("/Volumes/Big", CancellationToken.None, inTerminal: true);
        Assert.True(_install.LastInTerminal);
        await controller.KeepAwakeAsync(true, CancellationToken.None, inTerminal: true);
        Assert.True(_install.LastInTerminal);
        await controller.TurnOffAsync(CancellationToken.None, inTerminal: true);
        Assert.True(_install.LastInTerminal);
    }

    [Fact]
    public void What_the_script_says_reaches_the_person_and_cancelling_is_not_an_error()
    {
        var failed = HostInstall.Failure(
            "0:301: execution error: “Big” is formatted as ExFAT, which can’t keep files private to one account. (1)\n");
        Assert.IsType<InvalidOperationException>(failed);
        Assert.Equal("“Big” is formatted as ExFAT, which can’t keep files private to one account.", failed.Message);

        Assert.IsType<OperationCanceledException>(HostInstall.Failure("0:301: execution error: User canceled. (-128)"));
        Assert.Equal("Something unexpected", HostInstall.Failure("Something unexpected\n").Message);
    }

    [Fact]
    public async Task The_program_a_service_runs_is_read_from_its_description()
    {
        var plist = Path.Combine(_temporary, "service.plist");
        await File.WriteAllTextAsync(plist, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>life.uncloud.host</string>
              <key>ProgramArguments</key><array><string>/Applications/Tom &amp; Ada/Uncloud.app/Contents/MacOS/Homebase.Server</string></array>
            </dict>
            </plist>
            """);
        Assert.Equal("/Applications/Tom & Ada/Uncloud.app/Contents/MacOS/Homebase.Server", HostInstall.ValueIn(plist, "ProgramArguments"));
        Assert.Equal("life.uncloud.host", HostInstall.ValueIn(plist, "Label"));
        Assert.Null(HostInstall.ValueIn(plist, "CFBundleVersion"));
        Assert.Null(HostInstall.ValueIn(Path.Combine(_temporary, "missing.plist"), "ProgramArguments"));
    }

    [Fact]
    public void Only_a_copy_in_Applications_sets_the_service_up()
    {
        // Run from the disk image or Downloads, the service would start Uncloud from somewhere gone
        // by tomorrow.
        var install = new HostInstall("/Volumes/Uncloud/Uncloud.app/Contents/MacOS");
        Assert.Equal("/Volumes/Uncloud/Uncloud.app", install.App);
        Assert.False(install.Packaged);
        Assert.NotNull(install.CannotInstall);
        Assert.False(install.IsInstalled);
    }

    [Fact]
    public async Task A_new_host_is_set_up_in_its_own_account_and_the_app_runs_no_server_of_its_own()
    {
        var controller = Controller();
        Assert.True(controller.AsksWhereFilesGo);

        await controller.BecomeHostAsync("/Volumes/Big", CancellationToken.None);

        var installed = Assert.Single(_install.Installs);
        Assert.Equal(("/Volumes/Big", (string?)null), installed);
        Assert.Equal(DesktopMode.Host, DesktopSettings.Load(_paths).Mode);
        Assert.True(controller.RunsInOwnAccount);
        Assert.Null(controller.Server);
        Assert.False(controller.ReachFromAnywhere);
        // Its settings aren't this person's to change from out here any more.
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SetReachFromAnywhereAsync(true, CancellationToken.None));

        // Opened again, it leaves Uncloud to the service.
        var reopened = Controller();
        await reopened.StartAsync(CancellationToken.None);
        Assert.Null(reopened.Server);
        Assert.True(reopened.RunsInOwnAccount);
        Assert.False(reopened.CanKeepPrivate);
    }

    [Fact]
    public async Task A_host_that_ran_as_its_person_takes_its_settings_with_it()
    {
        Directory.CreateDirectory(_hostConfig);
        await File.WriteAllTextAsync(Path.Combine(_hostConfig, "homebase.db"), "");
        new DesktopSettings { Mode = DesktopMode.Host }.Save(_paths);
        _install.Installed = false;
        var controller = Controller();

        // Its files stay where they are, so nobody is asked where they go.
        Assert.False(controller.AsksWhereFilesGo);
        Assert.True(controller.CanKeepPrivate);

        await controller.KeepPrivateAsync(null, CancellationToken.None);
        Assert.Equal((null, _hostConfig), Assert.Single(_install.Installs));
        Assert.True(controller.RunsInOwnAccount);
        Assert.False(controller.CanKeepPrivate);
    }

    [Fact]
    public async Task Not_typing_the_password_changes_nothing()
    {
        _install.Refusal = new OperationCanceledException();
        var controller = Controller();

        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.BecomeHostAsync(null, CancellationToken.None));
        Assert.Equal(DesktopMode.Unset, DesktopSettings.Load(_paths).Mode);
        Assert.False(controller.RunsInOwnAccount);
    }

    [Fact]
    public async Task A_copy_that_cant_set_the_service_up_says_why_before_asking_for_anything()
    {
        _install.CannotInstall = "Drag Uncloud into your Applications folder first.";
        var controller = Controller();
        Assert.Equal(_install.CannotInstall, controller.CannotSetUpPrivately);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.BecomeHostAsync(null, CancellationToken.None));
        Assert.Equal(_install.CannotInstall, refused.Message);
        Assert.Empty(_install.Installs);
    }

    [Fact]
    public async Task An_updated_app_asks_to_bring_Uncloud_itself_up_to_date()
    {
        // Uncloud runs from a copy only an administrator can change, so the app being updated
        // doesn't reach it until somebody here gives the password.
        new DesktopSettings { Mode = DesktopMode.Host }.Save(_paths);
        _install.Installed = true;
        _install.Version = "v2";
        _install.InstalledVersion = "v2";
        var controller = Controller();
        Assert.False(controller.NeedsUpdate);

        _install.InstalledVersion = "v1";
        Assert.True(controller.NeedsUpdate);
        Assert.Contains("Update Uncloud", await controller.HostStatusAsync(CancellationToken.None));

        // So does a copy that has gone missing.
        _install.InstalledVersion = "v2";
        _install.InstalledServer = Path.Combine(_temporary, "Server", "Homebase.Server");
        Assert.True(controller.NeedsUpdate);
        Assert.Contains("Update Uncloud", await controller.HostStatusAsync(CancellationToken.None));

        await controller.KeepPrivateAsync(null, CancellationToken.None);
        Assert.Single(_install.Installs);
    }

    [Fact]
    public void A_host_turned_off_with_everything_kept_is_picked_up_rather_than_asked_about_again()
    {
        // Its files and settings are still there, locked to Uncloud's account. Asking where files go
        // would hand over a folder that is never used, while the old one carried on.
        _install.HasKeptHost = true;
        var controller = Controller();
        Assert.True(controller.HasKeptHost);
        Assert.False(controller.AsksWhereFilesGo);
    }

    [Fact]
    public async Task Turning_it_off_forgets_this_Mac_was_the_host()
    {
        new DesktopSettings { Mode = DesktopMode.Host }.Save(_paths);
        _install.Installed = true;
        var controller = Controller();

        await controller.TurnOffAsync(CancellationToken.None);
        Assert.Equal(1, _install.Uninstalls);
        Assert.Equal(DesktopMode.Unset, DesktopSettings.Load(_paths).Mode);
    }

    [Fact]
    public async Task A_new_host_kept_awake_is_never_asked_about_it_again()
    {
        var controller = Controller();
        Assert.True(controller.OffersToKeepAwake);

        await controller.BecomeHostAsync(null, CancellationToken.None, awake: true);

        Assert.Equal(true, Assert.Single(_install.AwakeAsked));
        Assert.True(controller.KeepsAwake);
        Assert.False(controller.OffersToKeepAwake);
        Assert.True(DesktopSettings.Load(_paths).AskedToKeepAwake);
    }

    [Fact]
    public async Task A_host_from_before_is_offered_it_once_and_a_no_is_remembered()
    {
        // Set up before Uncloud could keep a Mac awake. The next update asks, because the password
        // is being asked for anyway.
        new DesktopSettings { Mode = DesktopMode.Host }.Save(_paths);
        _install.Installed = true;
        var controller = Controller();
        Assert.True(controller.OffersToKeepAwake);
        Assert.False(controller.KeepsAwake);

        // An update that wasn't asked about it leaves the Mac as it is, and still offers next time.
        await controller.KeepPrivateAsync(null, CancellationToken.None);
        Assert.Null(_install.AwakeAsked[^1]);
        Assert.True(controller.OffersToKeepAwake);

        // Unticked: it stays as it was, and isn't offered again on the next update.
        await controller.KeepPrivateAsync(null, CancellationToken.None, awake: false);
        Assert.Equal(false, _install.AwakeAsked[^1]);
        Assert.False(controller.KeepsAwake);
        Assert.False(Controller().OffersToKeepAwake);
    }

    [Fact]
    public async Task The_menu_keeps_it_awake_and_lets_it_sleep_again()
    {
        new DesktopSettings { Mode = DesktopMode.Host }.Save(_paths);
        _install.Installed = true;
        var controller = Controller();

        await controller.KeepAwakeAsync(true, CancellationToken.None);
        Assert.True(controller.KeepsAwake);
        Assert.False(controller.OffersToKeepAwake);

        await controller.KeepAwakeAsync(false, CancellationToken.None);
        Assert.False(controller.KeepsAwake);
        // Said no to, from the menu: not offered again on updating.
        Assert.False(controller.OffersToKeepAwake);
    }

    [Fact]
    public async Task Only_a_host_in_its_own_account_is_kept_awake()
    {
        // One that runs as the person signed in here has nothing set up for it to run as.
        new DesktopSettings { Mode = DesktopMode.Host }.Save(_paths);
        _install.Installed = false;
        var controller = Controller();

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.KeepAwakeAsync(true, CancellationToken.None));
        Assert.False(_install.KeepsAwake);
    }

    [Fact]
    public async Task Turning_it_off_lets_the_Mac_sleep_again()
    {
        new DesktopSettings { Mode = DesktopMode.Host, AskedToKeepAwake = true }.Save(_paths);
        _install.Installed = true;
        _install.KeepsAwake = true;
        var controller = Controller();
        Assert.True(controller.KeepsAwake);

        await controller.TurnOffAsync(CancellationToken.None);

        Assert.False(controller.KeepsAwake);
        // Turned on again, it is asked afresh.
        Assert.True(controller.OffersToKeepAwake);
    }

    [Fact]
    public void The_script_is_told_to_keep_the_Mac_awake_only_when_somebody_chose()
    {
        Assert.Contains(("--awake", "on"), HostInstall.InstallOptions("/Applications/Uncloud.app", "ada", null, null, true));
        Assert.Contains(("--awake", "off"), HostInstall.InstallOptions("/Applications/Uncloud.app", "ada", null, null, false));
        Assert.DoesNotContain(HostInstall.InstallOptions("/Applications/Uncloud.app", "ada", "/Volumes/Big", null, null),
            option => option.Flag == "--awake");
    }

    [Fact]
    public async Task Terminal_says_what_the_password_is_for()
    {
        var terminal = Path.Combine(_temporary, "Uncloud.command");
        await File.WriteAllTextAsync(terminal, HostInstall.TerminalScript(
                "true", Path.Combine(_temporary, "result"), Path.Combine(_temporary, "error"),
                Path.Combine(_temporary, "status"), Path.Combine(_temporary, "decision"),
                "Uncloud wants to keep this Mac awake, so everyone’s “files” can't be $LOST.")
            .Replace("sudo -v", "true").Replace("sudo -n ", "").Replace("clear\n", ""));

        var output = await RunAsync("/bin/bash", terminal);

        Assert.StartsWith("Uncloud wants to keep this Mac awake, so everyone’s “files” can't be $LOST. It needs an administrator’s password.\n", output);
    }

    [Fact]
    public async Task The_menu_says_what_could_stop_the_host_and_looks_again_once_that_changes()
    {
        // A desktop as it comes: it would go to sleep after ten minutes.
        var asleep = new Homebase.Core.PowerState(10, false, false, false, false, true, true, []);
        var reads = 0;
        new DesktopSettings { Mode = DesktopMode.Host }.Save(_paths);
        _install.Installed = true;
        var controller = Controller(() => { reads++; return asleep; });

        await controller.CheckPowerAsync(CancellationToken.None);
        var sleeps = Assert.Single(controller.PowerFindings);
        Assert.Equal("sleeps", sleeps.Code);
        Assert.True(sleeps.InMenu);

        // Asked every few seconds by the menu, it reads the Mac again only now and then.
        await controller.CheckPowerAsync(CancellationToken.None);
        Assert.Equal(1, reads);

        // Kept awake from here, it looks again straight away, and there's nothing left to say.
        await controller.KeepAwakeAsync(true, CancellationToken.None);
        await controller.CheckPowerAsync(CancellationToken.None);
        Assert.Equal(2, reads);
        Assert.Empty(controller.PowerFindings);
    }

    [Fact]
    public async Task Only_the_host_is_told_about_staying_on()
    {
        // Somebody's own laptop needn't stay on for anybody.
        var laptop = new Homebase.Core.PowerState(10, false, true, true, false, false, null, []);
        var controller = Controller(() => laptop);

        await controller.CheckPowerAsync(CancellationToken.None);
        Assert.Empty(controller.PowerFindings);
        // Though setting it up as the host is told it's a laptop, to say what keeping it awake can't do.
        Assert.True(controller.Power?.HasBattery);
    }

    private static async Task<string> RunAsync(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var controller in _controllers) await controller.DisposeAsync();
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }

    private sealed class FakeInstall : IHostInstall
    {
        public bool Packaged => true;
        public string? CannotInstall { get; set; }
        public bool Installed { get; set; }
        public bool IsInstalled => Installed;
        public bool HasKeptHost { get; set; }
        public string? InstalledServer { get; set; }
        public string? InstalledVersion { get; set; }
        public string? Version { get; set; }
        // Somewhere nothing is listening, rather than the real one on a Mac that is a host.
        public string BridgeSocket { get; } = Path.Combine(Path.GetTempPath(), "uc-" + Guid.NewGuid().ToString("N")[..8], "b.sock");
        public IReadOnlyList<string> Private { get; } = [];
        public Exception? Refusal { get; set; }
        public bool LastInTerminal { get; private set; }
        public List<(string? Root, string? Legacy)> Installs { get; } = [];
        /// <summary>What each install was told about keeping the Mac awake, in order.</summary>
        public List<bool?> AwakeAsked { get; } = [];
        public int Uninstalls { get; private set; }
        public bool KeepsAwake { get; set; }

        public Task InstallAsync(string? root, string? legacyConfig, CancellationToken cancellationToken, bool inTerminal = false,
            bool? awake = null)
        {
            LastInTerminal = inTerminal;
            if (Refusal is not null) throw Refusal;
            Installs.Add((root, legacyConfig));
            AwakeAsked.Add(awake);
            if (awake is { } keep) KeepsAwake = keep;
            Installed = true;
            return Task.CompletedTask;
        }

        public Task KeepAwakeAsync(bool awake, CancellationToken cancellationToken, bool inTerminal = false)
        {
            LastInTerminal = inTerminal;
            if (Refusal is not null) throw Refusal;
            KeepsAwake = awake;
            return Task.CompletedTask;
        }

        public Task<string> PrepareAsync(string folder, CancellationToken cancellationToken, bool inTerminal = false)
        {
            LastInTerminal = inTerminal;
            return Task.FromResult(Path.Combine(folder, "Uncloud"));
        }

        public Task UninstallAsync(CancellationToken cancellationToken, bool inTerminal = false)
        {
            LastInTerminal = inTerminal;
            Uninstalls++;
            Installed = false;
            KeepsAwake = false;
            return Task.CompletedTask;
        }
    }
}
