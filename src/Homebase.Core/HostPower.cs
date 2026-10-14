using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Homebase.Core;

/// <summary>What a Mac's power settings come to, read without an administrator.</summary>
/// <param name="SleepMinutes">How long it waits with nobody at it before it sleeps; 0 for never, null when unknown.</param>
/// <param name="SleepDisabled">Whether sleep is switched off altogether, lid and all.</param>
/// <param name="HasBattery">Whether it is a laptop.</param>
/// <param name="OnBattery">Whether it is running on that battery now.</param>
/// <param name="FileVault">Whether its disk is locked until somebody signs in after a restart; null when unknown.</param>
/// <param name="CanAutoRestart">Whether it can start up by itself when the power comes back.</param>
/// <param name="AutoRestart">Whether it will; null when unknown.</param>
/// <param name="Scheduled">Sleeps, shutdowns and restarts it is set to repeat, as pmset says them.</param>
public sealed record PowerState(
    int? SleepMinutes,
    bool SleepDisabled,
    bool HasBattery,
    bool OnBattery,
    bool? FileVault,
    bool CanAutoRestart,
    bool? AutoRestart,
    IReadOnlyList<string> Scheduled);

/// <summary>Something that could leave everybody without their files, said for whoever looks after the host.</summary>
/// <param name="Code">Which it is, for anything that needs to tell them apart.</param>
/// <param name="Title">A few words, for the menu bar.</param>
/// <param name="Message">What it means and what to do about it.</param>
/// <param name="InMenu">Whether Keep This Mac Awake, in the Uncloud menu on the host, puts it right.</param>
public sealed record PowerFinding(string Code, string Title, string Message, bool InMenu);

/// <summary>
/// Whether a Mac host will stay on and awake for everybody, and what could stop it: going to sleep,
/// a laptop's lid, its battery, staying off after a power cut, FileVault after a restart, or a
/// shutdown somebody scheduled. Everything is read with commands any account can run, so Uncloud's
/// own account can ask as well as the app.
/// </summary>
public static class HostPower
{
    /// <summary>The service the Mac app sets up to keep a host awake, which is there for as long as it does.</summary>
    public const string AwakeService = "/Library/LaunchDaemons/life.uncloud.awake.plist";

    /// <summary>
    /// How this Mac stands, or null anywhere that isn't a Mac, or where pmset can't be asked.
    /// <paramref name="run"/> runs a program with its arguments and gives back what it printed, or
    /// null when it couldn't; replaced in tests.
    /// </summary>
    public static PowerState? Read(Func<string, string[], string?>? run = null)
    {
        if (run is null && !OperatingSystem.IsMacOS()) return null;
        run ??= Run;
        if (run("/usr/bin/pmset", ["-g"]) is not { } settings) return null;
        var battery = run("/usr/bin/pmset", ["-g", "batt"]) ?? "";
        var capabilities = run("/usr/bin/pmset", ["-g", "cap"]) ?? "";
        var schedule = run("/usr/bin/pmset", ["-g", "sched"]) ?? "";
        var fileVault = run("/usr/bin/fdesetup", ["status"]);
        var canRestart = Listed(capabilities, "autorestart");
        return new PowerState(
            Setting(settings, "sleep"),
            Setting(settings, "SleepDisabled") is > 0,
            battery.Contains("InternalBattery", StringComparison.Ordinal),
            battery.Contains("'Battery Power'", StringComparison.Ordinal),
            FileVaultOn(fileVault),
            canRestart,
            canRestart && Setting(settings, "autorestart") is { } restart ? restart > 0 : null,
            Repeating(schedule));
    }

    /// <summary>
    /// What stands between this host and staying on, worst first.
    /// </summary>
    /// <param name="keptAwake">Whether the Mac app's service is keeping it awake.</param>
    /// <param name="ownAccount">
    /// Whether Uncloud runs in an account of its own, set up by the Mac app, whose menu can keep it
    /// awake. Otherwise that's done in System Settings.
    /// </param>
    public static IReadOnlyList<PowerFinding> Findings(PowerState state, bool keptAwake, bool ownAccount)
    {
        const string Menu = " On the host, choose Keep This Mac Awake under the U in the menu bar.";
        var findings = new List<PowerFinding>();
        if (!keptAwake && !state.SleepDisabled && state.SleepMinutes is int minutes && minutes > 0)
        {
            var after = minutes == 1 ? "a minute" : $"{minutes} minutes";
            findings.Add(new PowerFinding("sleeps", $"Goes to sleep after {after}",
                $"This Mac goes to sleep after {after} with nobody at it, and then nobody can reach their files."
                + (ownAccount ? Menu : " Turn that off in System Settings."),
                ownAccount));
        }
        if (state.OnBattery)
            findings.Add(new PowerFinding("on_battery", "Running on its battery",
                "This Mac is running on its battery, and when that runs out it shuts down. Plug it in.", false));
        if (state.HasBattery && !state.SleepDisabled)
            findings.Add(new PowerFinding("laptop", "Sleeps when its lid is closed",
                "This Mac is a laptop: closing its lid puts it to sleep whatever Uncloud does, and then nobody can reach their files. Keep its lid open while it’s the host.",
                false));
        foreach (var scheduled in state.Scheduled)
        {
            var said = Scheduled(scheduled);
            findings.Add(new PowerFinding("scheduled", $"Set to {said}",
                $"This Mac is set to {said}, and nobody can reach their files until it’s back. An administrator can stop that in Terminal with: sudo pmset repeat cancel",
                false));
        }
        if (state.CanAutoRestart && state.AutoRestart == false)
            findings.Add(new PowerFinding("no_autorestart", "Stays off after a power cut",
                "After a power cut this Mac stays off until somebody turns it on."
                + (ownAccount
                    ? " On the host, choose Keep This Mac Awake under the U in the menu bar, which also has it start up again by itself."
                    : " Turn on starting up automatically after a power failure in System Settings."),
                ownAccount));
        if (state.FileVault == true)
            findings.Add(new PowerFinding("filevault", "After a restart, somebody must sign in",
                "FileVault is on, so after this Mac restarts — after a power cut, say — Uncloud can’t start until somebody signs in at it once. Leave FileVault on all the same: it’s what keeps everyone’s files safe if the Mac is stolen.",
                false));
        return findings;
    }

    /// <summary>A number pmset lists against <paramref name="name"/>, the first time it does.</summary>
    internal static int? Setting(string settings, string name)
    {
        foreach (var line in Lines(settings))
        {
            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && fields[0] == name && int.TryParse(fields[1], out var value)) return value;
        }
        return null;
    }

    /// <summary>Whether a capability is one pmset lists, one to a line.</summary>
    internal static bool Listed(string capabilities, string name) =>
        Lines(capabilities).Any(line => line == name);

    /// <summary>"FileVault is On." or "Off.", sometimes with more after; anything else isn't known.</summary>
    internal static bool? FileVaultOn(string? status) =>
        status?.Trim() switch
        {
            { } said when said.StartsWith("FileVault is On", StringComparison.Ordinal) => true,
            { } said when said.StartsWith("FileVault is Off", StringComparison.Ordinal) => false,
            _ => null
        };

    private static readonly Regex Event = new(@"^(sleep|shutdown|restart) at \S", RegexOptions.CultureInvariant);

    /// <summary>
    /// The sleeps, shutdowns and restarts pmset repeats, as it says them: "shutdown at 11:00PM every
    /// day". Waking and powering on are left out, since they never leave anybody without their files.
    /// </summary>
    internal static IReadOnlyList<string> Repeating(string schedule)
    {
        var events = new List<string>();
        var repeating = false;
        foreach (var line in Lines(schedule))
        {
            if (line.EndsWith(':')) repeating = line.StartsWith("Repeating", StringComparison.Ordinal);
            else if (repeating && Event.IsMatch(line)) events.Add(line);
        }
        return events;
    }

    /// <summary>"shutdown at 11:00PM every day" as somebody would say it: "shut down at 11:00PM every day".</summary>
    internal static string Scheduled(string scheduled)
    {
        var at = scheduled.IndexOf(" at ", StringComparison.Ordinal);
        var what = scheduled[..at] switch
        {
            "sleep" => "go to sleep",
            "shutdown" => "shut down",
            _ => "restart"
        };
        return what + scheduled[at..];
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Run(string program, string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(program)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                return null;
            }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
