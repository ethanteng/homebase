using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;

namespace Homebase.Tests;

/// <summary>
/// What a Mac host's power settings mean for everybody reaching their files: read from what pmset
/// and fdesetup print, as they print it, and said for whoever looks after the host.
/// </summary>
public sealed class HostPowerTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));

    // A Mac mini as it comes: asleep after a minute with nobody at it, off after a power cut.
    private const string DesktopSettings = """
        System-wide power settings:
         SleepDisabled		0
        Currently in use:
         standby              0
         Sleep On Power Button 1
         autorestart          0
         powernap             1
         networkoversleep     0
         disksleep            10
         sleep                1 (sleep prevented by sharingd, powerd)
         ttyskeepawake        1
         displaysleep         10
         tcpkeepalive         1
         womp                 1
        """;

    private const string DesktopCapabilities = """
        Capabilities for AC Power:
         displaysleep
         disksleep
         sleep
         womp
         autorestart
         standby
         powernap
         ttyskeepawake
         tcpkeepalive
        """;

    private const string LaptopSettings = """
        System-wide power settings:
        Currently in use:
         standby              1
         Sleep On Power Button 1
         hibernatefile        /var/vm/sleepimage
         powernap             1
         networkoversleep     0
         disksleep            10
         sleep                10
         hibernatemode        3
         ttyskeepawake        1
         displaysleep         2
         tcpkeepalive         1
         lowpowermode         0
         womp                 0
        """;

    private const string LaptopCapabilities = """
        Capabilities for Battery Power:
         displaysleep
         disksleep
         sleep
         standby
         powernap
         ttyskeepawake
         tcpkeepalive
         lowpowermode
        """;

    private const string OnMains = "Now drawing from 'AC Power'\n";
    private const string LaptopOnMains = "Now drawing from 'AC Power'\n -InternalBattery-0 (id=4653155)\t100%; charged; 0:00 remaining present: true\n";
    private const string LaptopOnBattery = "Now drawing from 'Battery Power'\n -InternalBattery-0 (id=4653155)\t87%; discharging; 5:12 remaining present: true\n";

    private const string Schedule = """
        Repeating power events:
          wakepoweron at 7:00AM every day
          shutdown at 11:00PM every day
        Scheduled power events:
         [0]  wake at 10/15/2026 07:00:00 by 'com.apple.alarm.user-invisible-com.apple.acmd.alarm'
        """;

    /// <summary>A Mac that prints what it's given for each command, and nothing for anything else.</summary>
    private static Func<string, string[], string?> Mac(
        string settings, string battery, string capabilities, string schedule = "", string? fileVault = "FileVault is Off.\n") =>
        (program, arguments) => (program, string.Join(' ', arguments)) switch
        {
            ("/usr/bin/pmset", "-g") => settings,
            ("/usr/bin/pmset", "-g batt") => battery,
            ("/usr/bin/pmset", "-g cap") => capabilities,
            ("/usr/bin/pmset", "-g sched") => schedule,
            ("/usr/bin/fdesetup", "status") => fileVault,
            _ => null
        };

    private static string[] Codes(IEnumerable<PowerFinding> findings) => findings.Select(finding => finding.Code).ToArray();

    [Fact]
    public void A_desktop_as_it_comes_sleeps_and_stays_off_after_a_power_cut()
    {
        var state = HostPower.Read(Mac(DesktopSettings, OnMains, DesktopCapabilities, fileVault: "FileVault is On.\n"))!;

        Assert.Equal(1, state.SleepMinutes);
        Assert.False(state.SleepDisabled);
        Assert.False(state.HasBattery);
        Assert.False(state.OnBattery);
        Assert.True(state.CanAutoRestart);
        Assert.False(state.AutoRestart);
        Assert.True(state.FileVault);
        Assert.Empty(state.Scheduled);

        var findings = HostPower.Findings(state, keptAwake: false, ownAccount: true);
        Assert.Equal(["sleeps", "no_autorestart", "filevault"], Codes(findings));
        var sleeps = findings[0];
        Assert.Equal("Goes to sleep after a minute", sleeps.Title);
        Assert.Contains("Keep This Mac Awake", sleeps.Message);
        Assert.True(sleeps.InMenu);
        Assert.True(findings[1].InMenu);
        // Nothing Uncloud should do anything about: FileVault is what keeps the files safe.
        Assert.False(findings[2].InMenu);
        Assert.Contains("Leave FileVault on", findings[2].Message);
    }

    [Fact]
    public void A_desktop_kept_awake_that_starts_after_a_power_cut_has_nothing_to_say()
    {
        var settings = DesktopSettings.Replace("autorestart          0", "autorestart          1");
        var state = HostPower.Read(Mac(settings, OnMains, DesktopCapabilities))!;

        Assert.True(state.AutoRestart);
        Assert.False(state.FileVault);
        Assert.Empty(HostPower.Findings(state, keptAwake: true, ownAccount: true));
    }

    [Fact]
    public void A_laptop_is_told_about_its_lid_and_its_battery_and_not_about_power_cuts()
    {
        var state = HostPower.Read(Mac(LaptopSettings, LaptopOnBattery, LaptopCapabilities))!;

        Assert.Equal(10, state.SleepMinutes);
        Assert.True(state.HasBattery);
        Assert.True(state.OnBattery);
        Assert.False(state.CanAutoRestart);
        Assert.Null(state.AutoRestart);
        Assert.Equal(["on_battery", "laptop"], Codes(HostPower.Findings(state, keptAwake: true, ownAccount: true)));

        var plugged = HostPower.Read(Mac(LaptopSettings, LaptopOnMains, LaptopCapabilities))!;
        Assert.False(plugged.OnBattery);
        Assert.Equal(["sleeps", "laptop"], Codes(HostPower.Findings(plugged, keptAwake: false, ownAccount: true)));
        Assert.Equal("Goes to sleep after 10 minutes", HostPower.Findings(plugged, false, true)[0].Title);
    }

    [Fact]
    public void Sleep_switched_off_altogether_keeps_even_a_closed_laptop_awake()
    {
        var settings = LaptopSettings.Replace("System-wide power settings:", "System-wide power settings:\n SleepDisabled\t\t1");
        var state = HostPower.Read(Mac(settings, LaptopOnMains, LaptopCapabilities))!;

        Assert.True(state.SleepDisabled);
        Assert.Empty(HostPower.Findings(state, keptAwake: false, ownAccount: true));
    }

    [Fact]
    public void A_shutdown_somebody_scheduled_is_said_and_waking_up_is_not()
    {
        Assert.Equal(["shutdown at 11:00PM every day"], HostPower.Repeating(Schedule));
        Assert.Empty(HostPower.Repeating("No scheduled events.\n"));

        var state = HostPower.Read(Mac(DesktopSettings, OnMains, DesktopCapabilities, Schedule))!;
        var scheduled = Assert.Single(HostPower.Findings(state, keptAwake: true, ownAccount: true), finding => finding.Code == "scheduled");
        Assert.Equal("Set to shut down at 11:00PM every day", scheduled.Title);
        Assert.Contains("sudo pmset repeat cancel", scheduled.Message);
        Assert.Equal("go to sleep at 2:00AM every weekday", HostPower.Scheduled("sleep at 2:00AM every weekday"));
        Assert.Equal("restart at 3:00AM every day", HostPower.Scheduled("restart at 3:00AM every day"));
    }

    [Fact]
    public void Run_some_other_way_it_points_to_System_Settings_instead_of_the_menu()
    {
        // From Terminal, or a host still running as the person signed in: the menu can't keep it awake.
        var state = HostPower.Read(Mac(DesktopSettings, OnMains, DesktopCapabilities))!;
        var findings = HostPower.Findings(state, keptAwake: false, ownAccount: false);

        Assert.All(findings, finding => Assert.False(finding.InMenu));
        Assert.All(findings, finding => Assert.DoesNotContain("Keep This Mac Awake", finding.Message));
        Assert.All(findings, finding => Assert.Contains("System Settings", finding.Message));
    }

    [Fact]
    public void What_cant_be_read_isnt_guessed_at()
    {
        // No pmset to ask: nothing is known, so nothing is said.
        Assert.Null(HostPower.Read((_, _) => null));

        // FileVault that fdesetup won't say is neither on nor off.
        var state = HostPower.Read(Mac(DesktopSettings, OnMains, DesktopCapabilities, fileVault: null))!;
        Assert.Null(state.FileVault);
        Assert.DoesNotContain("filevault", Codes(HostPower.Findings(state, false, true)));
        Assert.True(HostPower.FileVaultOn("FileVault is On, but needs to be restarted to finish."));
        Assert.Null(HostPower.FileVaultOn("Encryption in progress: Percent completed = 40"));
    }

    [Fact]
    public async Task Only_an_administrator_asks_and_only_a_Mac_has_anything_to_say()
    {
        using var app = new TestHost(Path.Combine(_temporary, "Config"));
        using var admin = await app.SignUpAsync("ada");
        using var member = await app.AddUserAsync(admin, "bo");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/host/power")).StatusCode);

        var report = await admin.GetFromJsonAsync<JsonElement>("/api/host/power");
        Assert.Equal(OperatingSystem.IsMacOS(), report.GetProperty("checked").GetBoolean());
        Assert.Equal(JsonValueKind.Array, report.GetProperty("findings").ValueKind);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }
}
