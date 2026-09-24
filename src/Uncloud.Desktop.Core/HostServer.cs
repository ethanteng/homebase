using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Uncloud.Desktop;

/// <summary>
/// On the host, the app runs the Uncloud server it carries — with its Syncthing and tunnel beside
/// it — as a child process, and stops it on the way out. Nobody opens a terminal.
/// </summary>
public sealed class HostServer(string serverDirectory, ILogger logger, HttpClient client, int port = HostServer.Port)
    : IAsyncDisposable
{
    public const int Port = 5210;
    private Process? _process;

    public Uri Address { get; } = new($"http://127.0.0.1:{port}");
    public string Executable => Path.Combine(serverDirectory, OperatingSystem.IsWindows() ? "Homebase.Server.exe" : "Homebase.Server");
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
        // Whether this host can be reached from outside is the server's own setting, kept beside
        // its accounts and changeable from Settings while it runs. Naming a provider here
        // would take that switch away from everybody who is not sitting at this Mac.
        var process = Process.Start(start) ?? throw new IOException("Uncloud didn’t start.");
        _process = process;
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
    /// Stops servers run from this same place that this app is not holding — left behind by a copy
    /// of the app that went without stopping them. Only this exact program: an Uncloud somebody is
    /// running from a checkout, or anything else that happens to be listening, is not this app's to
    /// stop.
    /// </summary>
    private int StopLeftovers()
    {
        var stopped = 0;
        var mine = Path.GetFullPath(Executable);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable)))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is not { } path
                        || !string.Equals(Path.GetFullPath(path), mine, StringComparison.Ordinal))
                        continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    stopped++;
                }
                catch (Exception error) when (error is InvalidOperationException or NotSupportedException
                    or System.ComponentModel.Win32Exception)
                {
                    // Gone already, or not ours to look at.
                }
            }
        }
        return stopped;
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
        try
        {
            _process!.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
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
