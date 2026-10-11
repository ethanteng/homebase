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

    private DesktopController Controller()
    {
        var controller = new DesktopController(_paths, Path.Combine(_temporary, "no-server-here"),
            NullLoggerFactory.Instance, new HttpClient(), _install, _hostConfig);
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
        Assert.Equal("/Applications/Tom & Ada/Uncloud.app/Contents/MacOS/Homebase.Server", HostInstall.ProgramIn(plist));
        Assert.Null(HostInstall.ProgramIn(Path.Combine(_temporary, "missing.plist")));
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
    public async Task A_service_set_up_from_a_copy_that_has_gone_asks_to_be_repaired()
    {
        new DesktopSettings { Mode = DesktopMode.Host }.Save(_paths);
        _install.Installed = true;
        _install.InstalledServer = Path.Combine(_temporary, "Gone.app", "Contents", "MacOS", "Homebase.Server");
        var controller = Controller();

        Assert.True(controller.NeedsRepair);
        Assert.Contains("Repair", await controller.HostStatusAsync(CancellationToken.None));
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
        public string? InstalledServer { get; set; }
        // Somewhere nothing is listening, rather than the real one on a Mac that is a host.
        public string BridgeSocket { get; } = Path.Combine(Path.GetTempPath(), "uc-" + Guid.NewGuid().ToString("N")[..8], "b.sock");
        public IReadOnlyList<string> Private { get; } = [];
        public Exception? Refusal { get; set; }
        public List<(string? Root, string? Legacy)> Installs { get; } = [];
        public int Uninstalls { get; private set; }

        public Task InstallAsync(string? root, string? legacyConfig, CancellationToken cancellationToken)
        {
            if (Refusal is not null) throw Refusal;
            Installs.Add((root, legacyConfig));
            Installed = true;
            return Task.CompletedTask;
        }

        public Task<string> PrepareAsync(string folder, CancellationToken cancellationToken) =>
            Task.FromResult(Path.Combine(folder, "Uncloud"));

        public Task UninstallAsync(CancellationToken cancellationToken)
        {
            Uninstalls++;
            Installed = false;
            return Task.CompletedTask;
        }
    }
}
