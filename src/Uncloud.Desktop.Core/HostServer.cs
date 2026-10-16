using System.Diagnostics;
using System.Text.RegularExpressions;
using Homebase.Core;
using Microsoft.Extensions.Logging;

namespace Uncloud.Desktop;

/// <summary>
/// On the host, the app runs the Uncloud server it carries — with its Syncthing and tunnel beside
/// it — as a child process, and stops it on the way out. Nobody opens a terminal.
/// </summary>
public sealed class HostServer(
    string serverDirectory, ILogger logger, HttpClient client, int port = HostServer.Port,
    IReadOnlyDictionary<string, string>? settings = null) : IAsyncDisposable
{
    public const int Port = 5210;
    // The server's program, beside the app's own.
    private const string ProcessName = "Homebase.Server";
    private Process? _process;
    // Set while it is being stopped on purpose, so that going is only called unexpected when it is.
    private bool _stopping;

    public Uri Address { get; } = new($"http://127.0.0.1:{port}");
    public string Executable => Path.Combine(serverDirectory, OperatingSystem.IsWindows() ? ProcessName + ".exe" : ProcessName);
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Why the server stopped, in its own words, for when somebody asks. The message rather than
    /// the last line it printed: a crash ends on the outermost stack frame, which names the line
    /// that fell over and says nothing about why.
    /// </summary>
    public string? LastError { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return;
        LastError = null;

        // Something already answering here is not this server, because this one hasn't started.
        // It is almost always an Uncloud left running by an earlier copy of the app that never got
        // to stop it. Starting beside it can only fail to bind — and the health check below would
        // have heard the old one answer and reported the new one fine while it lay dead.
        if (await AnswersAsync(cancellationToken))
        {
            var stopped = StopLeftovers();
            if (stopped > 0)
                logger.LogWarning("Stopped {Count} Uncloud left running by an earlier copy of the app", stopped);
            for (var attempt = 0; attempt < 20 && await AnswersAsync(cancellationToken); attempt++)
                await Task.Delay(250, cancellationToken);
            if (await AnswersAsync(cancellationToken))
                throw new IOException(LastError =
                    $"Something else on this Mac is already answering at port {port}, so Uncloud can’t start there. "
                    + "Quit whatever that is, or restart the Mac, then start Uncloud again.");
        }

        var start = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = serverDirectory,
            // Held open and never written to. When this app goes, however it goes — quit, crash,
            // force-quit, an update over the top of it — the other end closes and the server stops
            // with it rather than holding the port against the next copy.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["Homebase__Port"] = port.ToString();
        start.Environment["Homebase__StopWhenInputCloses"] = "true";
        foreach (var (name, value) in settings ?? new Dictionary<string, string>())
            start.Environment[name] = value;
        // Whether this host can be reached from outside is the server's own setting, kept beside
        // its accounts and changeable from Settings while it runs. Naming a provider here
        // would take that switch away from everybody who is not sitting at this Mac.
        _stopping = false;
        var process = Process.Start(start) ?? throw new IOException("Uncloud didn’t start.");
        _process = process;
        // Said in the log the moment it happens, with how it ended, so "it stopped" can be answered
        // from the log rather than guessed at.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (_stopping) return;
            int? code = null;
            try { code = process.ExitCode; }
            catch (InvalidOperationException) { }
            logger.LogWarning("Uncloud’s server stopped by itself, with exit code {Code}. {Why}", code, LastError);
        };
        // Drained, or a server that fills an unread pipe stops answering while looking alive.
        process.OutputDataReceived += (_, line) => { if (line.Data is { Length: > 0 }) logger.LogInformation("server: {Line}", line.Data); };
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is not { Length: > 0 }) return;
            logger.LogWarning("server: {Line}", line.Data);
            if (Explains(line.Data) is { } why) LastError = why;
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        for (var attempt = 0; attempt < 120 && IsRunning; attempt++)
        {
            // Only while this one is still running: an answer from anything else is not this
            // server starting.
            if (await AnswersAsync(cancellationToken) && IsRunning) return;
            await Task.Delay(500, cancellationToken);
        }
        if (IsRunning)
            throw new IOException("Uncloud started but isn’t answering.");
        // Its last words arrive after it has gone; this waits for them.
        await process.WaitForExitAsync(cancellationToken);
        throw new IOException($"Uncloud stopped as it started. {LastError}".Trim());
    }

    private async Task<bool> AnswersAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(new Uri(Address, "/api/health"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        // The client's own timeout, which is nothing answering rather than a reason to stop.
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    /// <summary>
    /// Stops the server holding this app's port, when it is one this app left behind — started
    /// from this same place, the way this app starts it, by a copy of the app that went without
    /// stopping it. Both have to be true. The same program answering on another port is
    /// somebody's Uncloud doing something else, and anything else on this port is not this app's
    /// to stop either.
    /// </summary>
    private int StopLeftovers()
    {
        var stopped = 0;
        foreach (var id in Leftovers(Running(), ListeningOnPort(), Executable))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                ProcessTree.Kill(process);
                process.WaitForExit(5000);
                stopped++;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                // Gone already, or not ours to stop.
            }
        }
        return stopped;
    }

    /// <summary>A process as ps tells of it.</summary>
    internal sealed record RunningProcess(int Id, string CommandLine);

    /// <summary>
    /// Which of <paramref name="running"/> are this app's server, left holding its port: listening
    /// there, with the command line this app starts it with — <paramref name="executable"/>, and
    /// nothing else. Not by where macOS says a process's program is: once an update has replaced
    /// the app a server came from, macOS names the folder the old app was moved to, or nothing at
    /// all — and that is how these get left behind.
    /// </summary>
    internal static IReadOnlyList<int> Leftovers(IEnumerable<RunningProcess> running, IReadOnlySet<int> listening, string executable)
    {
        string[] started = [executable, Path.GetFullPath(executable)];
        return running
            .Where(process => process.Id != Environment.ProcessId
                && listening.Contains(process.Id)
                && started.Contains(process.CommandLine, StringComparer.Ordinal))
            .Select(process => process.Id)
            .ToList();
    }

    private static readonly Regex Listed = new(@"^\s*(\d+)\s+(.+)$");

    /// <summary>
    /// Every process with its command line, as ps tells it — which every Mac has. Where there is
    /// no ps to ask, none are known, so none are stopped: the safe way to be wrong.
    /// </summary>
    private static List<RunningProcess> Running()
    {
        var running = new List<RunningProcess>();
        try
        {
            using var ps = Process.Start(new ProcessStartInfo("ps")
            {
                // -ww: whole command lines, however long, so a path deep in somebody's folders isn't cut short.
                ArgumentList = { "-A", "-ww", "-o", "pid=", "-o", "args=" },
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            if (ps is null) return running;
            var said = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(5000);
            foreach (var line in said.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (Listed.Match(line) is { Success: true } match && int.TryParse(match.Groups[1].Value, out var id))
                    running.Add(new RunningProcess(id, match.Groups[2].Value.TrimEnd()));
        }
        catch (System.ComponentModel.Win32Exception) { }
        return running;
    }

    /// <summary>
    /// The processes listening on this app's port, as lsof tells it — which every Mac has. Where
    /// there is no lsof to ask, nothing is known to be listening, so nothing is stopped: the person
    /// is told the port is taken instead, which is the safe way to be wrong.
    /// </summary>
    private HashSet<int> ListeningOnPort()
    {
        try
        {
            var start = new ProcessStartInfo("lsof")
            {
                ArgumentList = { "-t", "-nP", $"-iTCP:{port}", "-sTCP:LISTEN" },
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var lsof = Process.Start(start);
            if (lsof is null) return [];
            var said = lsof.StandardOutput.ReadToEnd();
            lsof.WaitForExit(5000);
            return said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => int.TryParse(line, out var id) ? id : -1)
                .Where(id => id > 0)
                .ToHashSet();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    // .NET's account of a crash: "Unhandled exception. System.IO.IOException: <why>", then frames.
    private static readonly Regex Thrown = new(@"^(?:Unhandled exception\.\s*)?(?:[\w.]+\.)?\w*(?:Exception|Error):\s*");

    /// <summary>The part of a line of standard error that says why, or null for a stack frame.</summary>
    internal static string? Explains(string line)
    {
        var said = line.Trim();
        if (said.Length == 0 || said.StartsWith("at ", StringComparison.Ordinal) || said.StartsWith("---", StringComparison.Ordinal))
            return null;
        return Thrown.Replace(said, "") is { Length: > 0 } why ? why : said;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        _stopping = true;
        try
        {
            // Asked first, the way it stops when this app goes: closing its standard input. It then
            // stops Syncthing and the tunnel itself, cleanly. Only one that hasn't gone in ten
            // seconds is killed — never with .NET's own tree kill, whose way of freezing each process
            // first gets this app hung up along with the server (see ProcessTree).
            try { _process!.StandardInput.Close(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _process!.WaitForExitAsync(patience.Token); }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Uncloud didn’t stop when asked, so it was stopped");
                ProcessTree.Kill(_process!);
                await _process!.WaitForExitAsync();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or SystemException)
        {
            logger.LogWarning(error, "Uncloud didn’t stop cleanly");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _process?.Dispose();
    }
}
