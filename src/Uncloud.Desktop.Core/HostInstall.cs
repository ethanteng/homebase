using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Uncloud.Desktop;

/// <summary>
/// Setting Uncloud up on the host to run in a macOS account of its own, as a background service,
/// rather than as whoever is signed in at the Mac. That account owns everybody's files, so nobody
/// signed in here can look through them, in Finder or in Terminal; only an administrator using sudo
/// and their password can. It also means Uncloud runs from the moment the Mac starts.
///
/// The work is done by a script the app carries, run as root through macOS's own password prompt.
/// </summary>
public interface IHostInstall
{
    /// <summary>
    /// Whether this copy of the app carries the script at all. A build run from source doesn't, and
    /// runs Uncloud as the person running it, as it always has.
    /// </summary>
    bool Packaged { get; }

    /// <summary>Why this copy can't set the service up from where it is, or null when it can.</summary>
    string? CannotInstall { get; }

    /// <summary>Whether Uncloud is set up on this Mac to run in its own account.</summary>
    bool IsInstalled { get; }

    /// <summary>The Uncloud the service runs, as it was set up, or null when there isn't one.</summary>
    string? InstalledServer { get; }

    /// <summary>
    /// Which version of the app the service's copy of Uncloud came from. It runs from a copy only an
    /// administrator can change, so updating the app reaches it only when it is set up again.
    /// </summary>
    string? InstalledVersion { get; }

    /// <summary>This copy of the app's version, to compare against.</summary>
    string? Version { get; }

    /// <summary>Where this app hands the service the Mac's folders, which its account can't read.</summary>
    string BridgeSocket { get; }

    /// <summary>The service's own folders, which are never read for it from here.</summary>
    IReadOnlyList<string> Private { get; }

    /// <summary>
    /// Sets the service up, or repairs it. <paramref name="root"/> is where everyone's files go on a
    /// new host — null for a folder on this Mac. <paramref name="legacyConfig"/> is the settings of
    /// an Uncloud that ran as the person signed in here until now, which are moved in with its files.
    /// </summary>
    Task InstallAsync(string? root, string? legacyConfig, CancellationToken cancellationToken);

    /// <summary>Opens a folder or drive to Uncloud's account, and says which folder to choose in Settings.</summary>
    Task<string> PrepareAsync(string folder, CancellationToken cancellationToken);

    /// <summary>Stops the service. Everybody's files and accounts stay, still locked to Uncloud's account.</summary>
    Task UninstallAsync(CancellationToken cancellationToken);
}

/// <summary>The one a Mac has: the script in the app, run with an administrator's password.</summary>
public sealed class HostInstall(string serverDirectory) : IHostInstall
{
    public const string Label = "life.uncloud.host";
    /// <summary>Where the service keeps everything. Only Uncloud's account can look inside.</summary>
    public const string Base = "/Library/Application Support/Uncloud";
    public static readonly string Plist = $"/Library/LaunchDaemons/{Label}.plist";
    /// <summary>Where this app hands Uncloud the Mac's folders, in a folder only it and Uncloud can open.</summary>
    public string BridgeSocket { get; } = Path.Combine(Base, "Bridge", "host-folders.sock");

    public IReadOnlyList<string> Private { get; } = [Base, "/Library/Logs/Uncloud"];

    private const string Prompt = "Uncloud wants to keep everyone’s files private on this Mac.";

    /// <summary>Uncloud.app, which the server sits inside, in Contents/MacOS.</summary>
    public string App => Path.GetFullPath(Path.Combine(serverDirectory, "..", ".."));
    public string Server => Path.Combine(serverDirectory, "Homebase.Server");
    public string Script => Path.GetFullPath(Path.Combine(serverDirectory, "..", "Resources", "uncloud-host.sh"));

    public bool Packaged => OperatingSystem.IsMacOS() && File.Exists(Script);

    public string? CannotInstall =>
        !Packaged ? "This copy of Uncloud can’t set itself up as the host. Download it again, and open it from Applications."
        // The service starts Uncloud from where it was set up, so that has to be somewhere it stays:
        // a copy opened from Downloads, or from the disk image, is moved or gone by tomorrow.
        : !App.StartsWith("/Applications/", StringComparison.Ordinal)
            ? "Drag Uncloud into your Applications folder, then open it from there and try again."
            : null;

    /// <summary>
    /// Only for a packaged copy: a build run from source runs its own Uncloud, as it always has,
    /// whatever else this Mac is set up to run.
    /// </summary>
    public bool IsInstalled => Packaged && File.Exists(Plist);

    public string? InstalledServer => IsInstalled ? ValueIn(Plist, "ProgramArguments") : null;

    public string? InstalledVersion
    {
        get
        {
            try { return IsInstalled ? File.ReadAllText(Path.Combine(Base, "Server", ".uncloud-version")).Trim() : null; }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    public string? Version => ValueIn(Path.Combine(App, "Contents", "Info.plist"), "CFBundleVersion");

    public async Task InstallAsync(string? root, string? legacyConfig, CancellationToken cancellationToken)
    {
        var options = new List<(string, string)> { ("--app", App), ("--owner", Environment.UserName) };
        if (root is not null) options.Add(("--root", root));
        if (legacyConfig is not null) options.Add(("--legacy-config", legacyConfig));
        await RunAsync("install", options, cancellationToken);
    }

    public async Task<string> PrepareAsync(string folder, CancellationToken cancellationToken) =>
        (await RunAsync("prepare", [("--folder", folder)], cancellationToken)).Trim();

    public Task UninstallAsync(CancellationToken cancellationToken) => RunAsync("uninstall", [], cancellationToken);

    private async Task<string> RunAsync(string verb, IEnumerable<(string Flag, string Value)> options, CancellationToken cancellationToken)
    {
        if (!Packaged) throw new InvalidOperationException(CannotInstall);
        var start = new ProcessStartInfo("/usr/bin/osascript")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(AppleScript(ShellCommand(Script, verb, options), Prompt));
        using var process = Process.Start(start) ?? throw new IOException("macOS didn’t ask for a password.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        // Not timed: somebody may take a while to type a password, and giving a large library to
        // Uncloud's account takes as long as there are files.
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode == 0) return await output;
        throw Failure(await error);
    }

    /// <summary>A command for /bin/sh, every value in single quotes, which leave everything alone but a quote.</summary>
    internal static string ShellCommand(string script, string verb, IEnumerable<(string Flag, string Value)> options) =>
        string.Join(' ', new[] { "/bin/bash", Quote(script), verb }
            .Concat(options.SelectMany(option => new[] { option.Flag, Quote(option.Value) })));

    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// The script, as AppleScript asks an administrator to run it. Its output is left as it was
    /// written, rather than having its line endings changed, so a folder name comes back intact.
    /// </summary>
    internal static string AppleScript(string command, string prompt) =>
        $"do shell script \"{Escape(command)}\" with prompt \"{Escape(prompt)}\" with administrator privileges without altering line endings";

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // osascript's account of a script that failed: "0:123: execution error: <what it said> (1)".
    private static readonly Regex Said = new(@"execution error: (?<said>.*) \((?<code>-?\d+)\)\s*$", RegexOptions.Singleline);

    /// <summary>What went wrong, in the script's own words. Cancelling the password prompt isn't an error.</summary>
    internal static Exception Failure(string error)
    {
        var match = Said.Match(error);
        if (match.Success && match.Groups["code"].Value == "-128") return new OperationCanceledException("Cancelled.");
        var said = (match.Success ? match.Groups["said"].Value : error).Trim();
        return new InvalidOperationException(said.Length > 0 ? said : "Uncloud couldn’t be set up on this Mac.");
    }

    /// <summary>
    /// A string a property list keeps under <paramref name="key"/>, or the first of a list of them —
    /// the program a launchd job starts, say — read without running anything.
    /// </summary>
    internal static string? ValueIn(string plist, string key)
    {
        try
        {
            // Its DOCTYPE names Apple's DTD, which is neither fetched nor needed to read it.
            using var reader = System.Xml.XmlReader.Create(plist,
                new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null });
            var keys = XDocument.Load(reader).Root?.Element("dict")?.Elements().ToList() ?? [];
            var at = keys.FindIndex(element => element.Name == "key" && element.Value == key);
            if (at < 0 || at + 1 >= keys.Count) return null;
            var value = keys[at + 1];
            return value.Name == "string" ? value.Value : value.Elements("string").FirstOrDefault()?.Value;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }
}
