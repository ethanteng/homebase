using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Sync;

/// <summary>
/// One Syncthing, run as a child process with its own home directory and a loopback-only API on
/// a port of its own: generated on first use, configured so only this process can drive it,
/// never upgrading itself, started again if it stops by itself, and stopped with its parent. One
/// an earlier copy left running on the same home is stopped before another is started. The
/// Uncloud server runs one for the host, and the Uncloud app runs one for the computer it is on.
/// </summary>
public sealed class SyncthingProcess(string home, string binary, int port, ILogger logger, HttpClient client)
    : ISyncthingEndpoint, IDisposable
{
    private readonly string _home = home;
    private readonly string _binary = binary;
    private readonly int _port = port;
    private Process? _process;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Held while a Syncthing is started or stopped, so one is never started after stopping began.
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private bool _stopped;
    private DateTimeOffset _startedAt;
    // How many times in a row it has stopped soon after starting.
    private int _failures;

    /// <summary>
    /// How long to wait before starting Syncthing again: longer each time it stops again soon
    /// after starting, so one that can't run isn't started over and over, then every few minutes,
    /// in case whatever stopped it has gone.
    /// </summary>
    internal IReadOnlyList<TimeSpan> RestartAfter { get; init; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    /// <summary>How long one has to have run for its stopping to count as a first time again.</summary>
    internal TimeSpan SettledAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How many of the last lines Syncthing printed are logged when it stops by itself.</summary>
    internal const int SaidKept = 20;

    /// <summary>Where this Syncthing's identity, pairings and database are kept.</summary>
    public string Home => _home;

    /// <summary>Which Syncthing is run.</summary>
    public string Executable => _binary;

    /// <summary>
    /// A path somebody configured, then the copy Uncloud ships beside itself, then whatever is on
    /// the PATH — so a build that carries Syncthing needs nothing installed, and one that doesn't
    /// still works for a host that has it.
    /// </summary>
    public static string Binary(string? configured, string applicationDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var bundled = Path.Combine(applicationDirectory, OperatingSystem.IsWindows() ? "syncthing.exe" : "syncthing");
        return File.Exists(bundled) ? bundled : "syncthing";
    }

    public bool IsReady { get; private set; }
    public string? Unavailable { get; set; } = "Uncloud is still starting Syncthing.";
    public Uri? BaseAddress { get; private set; }
    public string? ApiKey { get; private set; }
    /// <summary>Completes once Syncthing answers, and never if it doesn't.</summary>
    public Task Ready => _ready.Task;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_home);
            if (!File.Exists(Path.Combine(_home, "config.xml")))
            {
                // Syncthing 1.x makes a default folder in the home directory unless told not to;
                // 2.x makes none and refuses the flag that says so.
                try { await RunAsync(["generate", "--home", _home, "--no-default-folder"], cancellationToken); }
                catch (IOException error) when (error.Message.Contains("unknown flag", StringComparison.Ordinal))
                {
                    await RunAsync(["generate", "--home", _home], cancellationToken);
                }
            }

            ApiKey = Configure();
            BaseAddress = new Uri($"http://127.0.0.1:{_port}");
            Serve();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Unavailable = _binary == "syncthing"
                ? "This build of Uncloud doesn’t include Syncthing, and there isn’t one installed. Run scripts/fetch-syncthing.sh, or install Syncthing."
                : $"Uncloud couldn’t start Syncthing ({_binary}).";
            logger.LogWarning(error, "Syncthing unavailable");
        }
    }

    /// <summary>Runs Syncthing itself, once it is set up, unless it is being stopped.</summary>
    private void Serve()
    {
        // One being stopped starts nothing, so whatever is on its home now is not its to stop: a
        // restart that was due as the app quit must not take the next copy's Syncthing with it.
        lock (_gate)
        {
            if (_stopped) return;
        }
        StopLeftovers();
        var serve = new ProcessStartInfo(_binary)
        {
            ArgumentList = { "serve", "--home", _home, "--no-browser" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Uncloud decides which Syncthing it runs. A release build otherwise replaces its own
        // binary, which would put an unpinned, unchecked version beside the application and
        // break the signature of a signed build.
        serve.Environment["STNOUPGRADE"] = "1";
        var process = new Process { StartInfo = serve, EnableRaisingEvents = true };
        // Syncthing says what it is doing, and why it is giving up, on standard output. Too much
        // of it to log as it comes, so the last of it is kept to log when it stops by itself.
        var said = new LastLines(SaidKept);
        // Redirected pipes must be drained. A long-running Syncthing that fills an unread
        // buffer blocks on its next write and stops syncing while still looking healthy.
        process.OutputDataReceived += (_, line) =>
        {
            if (line.Data is not { Length: > 0 }) return;
            logger.LogDebug("syncthing: {Line}", line.Data);
            said.Add(line.Data);
        };
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { Length: > 0 }) logger.LogWarning("syncthing: {Line}", line.Data);
        };
        process.Exited += (_, _) => Stopped(process, said);
        DateTimeOffset started;
        lock (_gate)
        {
            if (_stopped)
            {
                process.Dispose();
                return;
            }
            started = DateTimeOffset.UtcNow;
            try { process.Start(); }
            catch
            {
                process.Dispose();
                throw;
            }
            _process?.Dispose();
            _process = process;
            _startedAt = started;
        }
        logger.LogInformation("Starting Syncthing from {Binary}", _binary);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // Readiness is awaited in the background: a slow Syncthing must not hold up the app.
        _ = WaitForReadyAsync(process, started);
    }

    /// <summary>
    /// Syncthing went. Unless it was stopped on purpose, nothing syncs until it's back, so it is
    /// started again — and until then, nothing takes it for running.
    /// </summary>
    private void Stopped(Process process, LastLines said)
    {
        int failures;
        lock (_gate)
        {
            if (_stopped || !ReferenceEquals(process, _process)) return;
            _failures = DateTimeOffset.UtcNow - _startedAt >= SettledAfter ? 1 : _failures + 1;
            failures = _failures;
        }
        IsReady = false;
        int? code = null;
        try { code = process.ExitCode; }
        catch (InvalidOperationException) { }
        Unavailable = failures == 1
            ? "Syncthing stopped, and Uncloud is starting it again."
            : "Syncthing keeps stopping. Uncloud keeps starting it again, and what it said is in Uncloud’s log.";
        _ = Task.Run(async () =>
        {
            // Its last words, which say why, can arrive after it has gone. They are waited for a
            // moment and no more: a child of its own that outlived it would hold them open.
            using (var patience = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try { await process.WaitForExitAsync(patience.Token); }
                catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { }
            }
            var last = said.ToArray();
            logger.LogWarning("Syncthing stopped by itself, with exit code {Code}. Starting it again in {Wait}. {Said}",
                code, RestartWait(failures), last.Length == 0
                    ? "It said nothing."
                    : "The last it said:" + string.Concat(last.Select(line => Environment.NewLine + "  " + line)));
            // One that doesn't start never stops either, so nothing else would notice and try
            // again: that is up to this, for as long as it takes.
            for (var attempt = failures; ; attempt++)
            {
                try { await Task.Delay(RestartWait(attempt), _stopping.Token); }
                catch (OperationCanceledException) { return; }
                try
                {
                    Serve();
                    return;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException
                    or UnauthorizedAccessException or InvalidOperationException)
                {
                    Unavailable = $"Uncloud couldn’t start Syncthing again ({_binary}), and keeps trying.";
                    logger.LogWarning(error, "Syncthing didn’t start again. Trying again in {Wait}", RestartWait(attempt + 1));
                }
            }
        });
    }

    private TimeSpan RestartWait(int failures) => RestartAfter[Math.Min(failures, RestartAfter.Count) - 1];

    /// <summary>
    /// Stops a Syncthing serving this home that is still running from before: one an earlier copy
    /// of the app started and went without stopping — replaced by an update, say, or force-quit.
    /// Nothing stops a process's children when it goes, so that one carries on holding the home's
    /// lock and the API's port, every one started after it gives up within seconds, and its
    /// answering makes each look ready as it does.
    /// </summary>
    private void StopLeftovers()
    {
        if (OperatingSystem.IsWindows()) return;
        var leftovers = Leftovers(Running(), _binary, _home);
        if (leftovers.Count == 0) return;
        var ids = leftovers.Select(process => process.Id).ToHashSet();
        // Syncthing runs as a monitor that starts Syncthing proper and starts it again when it
        // goes, so the monitor is stopped, and what it started with it, before it can.
        foreach (var leftover in leftovers.Where(process => !ids.Contains(process.Parent)))
        {
            try
            {
                using var process = Process.GetProcessById(leftover.Id);
                ProcessTree.Kill(process);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
        }
        // Until they have gone, the home is still locked against the one about to start.
        foreach (var id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.WaitForExit(5000);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception) { }
        }
        logger.LogWarning("Stopped a Syncthing an earlier copy of Uncloud left running (process {Ids})",
            string.Join(", ", ids));
    }

    /// <summary>A process as ps tells of it.</summary>
    internal sealed record RunningProcess(int Id, int Parent, string CommandLine);

    /// <summary>
    /// The processes that are <paramref name="binary"/> serving <paramref name="home"/>, by their
    /// command lines: exactly what this would have started, and nothing else. A Syncthing on a
    /// home of its own — the host's, or one somebody runs themselves — is left alone, and so is
    /// a different Syncthing on this one. Not by where macOS says a process's program is, which
    /// it stops knowing once the app it came from has been replaced: how these get left behind.
    /// </summary>
    internal static IReadOnlyList<RunningProcess> Leftovers(IEnumerable<RunningProcess> running, string binary, string home)
    {
        var names = Names(binary);
        var serving = $" serve --home {home}";
        return running.Where(process =>
        {
            if (process.Id == Environment.ProcessId) return false;
            var at = process.CommandLine.IndexOf(serving, StringComparison.Ordinal);
            if (at <= 0) return false;
            // A home whose name only begins with this one's is another home.
            var after = at + serving.Length;
            if (after < process.CommandLine.Length && process.CommandLine[after] != ' ') return false;
            // Run directly, or — for a script — by the interpreter it names.
            var program = process.CommandLine[..at];
            return names.Any(name => program == name || program.EndsWith(" " + name, StringComparison.Ordinal));
        }).ToList();
    }

    /// <summary>
    /// What a Syncthing run from <paramref name="binary"/> can be called in its command line. This
    /// starts it by the path it was given; its monitor starts Syncthing proper by the path that
    /// leads to, found on the PATH for a bare name, and through a link on Linux.
    /// </summary>
    private static HashSet<string> Names(string binary)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { binary };
        try
        {
            var found = binary.Contains(Path.DirectorySeparatorChar)
                ? Path.GetFullPath(binary)
                : (Environment.GetEnvironmentVariable("PATH") ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(directory => Path.Combine(directory, binary))
                    .FirstOrDefault(File.Exists);
            if (found is null) return names;
            names.Add(found);
            if (new FileInfo(found).ResolveLinkTarget(returnFinalTarget: true) is { } target) names.Add(target.FullName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        return names;
    }

    private static readonly Regex Listed = new(@"^\s*(\d+)\s+(\d+)\s+(.+)$");

    /// <summary>
    /// Every process with its command line, as ps tells it — which every Mac and Linux has. Where
    /// there is no ps to ask, none are known, so none are stopped: the safe way to be wrong.
    /// </summary>
    private static List<RunningProcess> Running()
    {
        var running = new List<RunningProcess>();
        try
        {
            using var ps = Process.Start(new ProcessStartInfo("ps")
            {
                // -ww: whole command lines, however long, which a home in Application Support makes them.
                ArgumentList = { "-A", "-ww", "-o", "pid=", "-o", "ppid=", "-o", "args=" },
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            if (ps is null) return running;
            var said = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(5000);
            foreach (var line in said.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (Listed.Match(line) is { Success: true } match
                    && int.TryParse(match.Groups[1].Value, out var id)
                    && int.TryParse(match.Groups[2].Value, out var parent))
                    running.Add(new RunningProcess(id, parent, match.Groups[3].Value.TrimEnd()));
        }
        catch (System.ComponentModel.Win32Exception) { }
        return running;
    }

    /// <summary>Pins the GUI to loopback on our own port and returns the API key Syncthing generated.</summary>
    private string Configure()
    {
        var path = Path.Combine(_home, "config.xml");
        var document = XDocument.Load(path);
        var gui = document.Root!.Element("gui") ?? throw new IOException("Syncthing config has no gui section.");
        var address = gui.Element("address") ?? new XElement("address");
        address.Value = $"127.0.0.1:{_port}";
        if (address.Parent is null) gui.Add(address);
        var key = gui.Element("apikey")?.Value;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            gui.Add(new XElement("apikey", key));
        }
        // Nothing about this host goes to Syncthing's usage statistics unless somebody opts in there.
        // 0 is undecided, which makes Syncthing ask; a positive answer somebody gave is kept.
        if (document.Root!.Element("options") is { } options)
        {
            var reporting = options.Element("urAccepted");
            if (reporting is null) options.Add(new XElement("urAccepted", "-1"));
            else if (reporting.Value == "0") reporting.Value = "-1";
            // Belt and braces with STNOUPGRADE: the version is Uncloud's to choose.
            var upgrades = options.Element("autoUpgradeIntervalH");
            if (upgrades is null) options.Add(new XElement("autoUpgradeIntervalH", "0"));
            else upgrades.Value = "0";
        }
        document.Save(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    private async Task WaitForReadyAsync(Process process, DateTimeOffset started)
    {
        var told = false;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            // What it going means is said where that is noticed.
            if (HasExited(process)) return;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseAddress!, "/rest/system/status"));
                request.Headers.Add("X-API-Key", ApiKey ?? "");
                using var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode && !HasExited(process))
                {
                    // Anything else answering with this home's key is a Syncthing on this home that
                    // was running before this one started — which this one can't run beside, and
                    // will give up on. Syncthing gives the time it started to the second.
                    if (await StartTimeAsync(response) is { } since && since < started - TimeSpan.FromSeconds(2))
                    {
                        if (!told)
                            logger.LogWarning("A Syncthing that started at {Since}, before this one, is answering on {Address}",
                                since, BaseAddress);
                        told = true;
                    }
                    else
                    {
                        IsReady = true;
                        Unavailable = null;
                        _ready.TrySetResult();
                        logger.LogInformation("Syncthing is ready on {Address}", BaseAddress);
                        return;
                    }
                }
            }
            catch (HttpRequestException) { }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        Unavailable = "Syncthing didn’t come up. Check that it is installed and can run.";
    }

    /// <summary>When the Syncthing that answered started, if it said.</summary>
    private static async Task<DateTimeOffset?> StartTimeAsync(HttpResponseMessage response)
    {
        try
        {
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return status.RootElement.ValueKind == JsonValueKind.Object
                && status.RootElement.TryGetProperty("startTime", out var startTime)
                && startTime.ValueKind == JsonValueKind.String
                && startTime.TryGetDateTimeOffset(out var since)
                    ? since
                    : null;
        }
        catch (JsonException) { return null; }
    }

    // One that has been replaced since is as good as gone.
    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private async Task RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_binary) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Syncthing did not start.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new IOException($"Syncthing setup failed: {await process.StandardError.ReadToEndAsync(cancellationToken)}");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var process = Stop();
        IsReady = false;
        if (process is null || process.HasExited) return;
        try
        {
            // Not .NET's own tree kill, which can hang the Mac app up along with it: see ProcessTree.
            ProcessTree.Kill(process);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or SystemException)
        {
            logger.LogWarning(error, "Syncthing did not stop cleanly");
        }
    }

    /// <summary>Settles that nothing starts Syncthing again from now on, and says which one is running.</summary>
    private Process? Stop()
    {
        Process? process;
        lock (_gate)
        {
            _stopped = true;
            process = _process;
        }
        // Outside the lock: whatever was waiting to start it again finds out here, and must not
        // find the lock held.
        _stopping.Cancel();
        return process;
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
    }

    /// <summary>The last few lines of something, as they come.</summary>
    private sealed class LastLines(int count)
    {
        private readonly Queue<string> _lines = new();

        public void Add(string line)
        {
            lock (_lines)
            {
                _lines.Enqueue(line);
                if (_lines.Count > count) _lines.Dequeue();
            }
        }

        public string[] ToArray()
        {
            lock (_lines) return [.. _lines];
        }
    }
}
