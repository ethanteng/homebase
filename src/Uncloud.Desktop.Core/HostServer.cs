using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Uncloud.Desktop;

/// <summary>
/// On the host, the app runs the Uncloud server it carries — with its Syncthing and tunnel beside
/// it — as a child process, and stops it on the way out. Nobody opens a terminal.
/// </summary>
public sealed class HostServer(string serverDirectory, ILogger logger, HttpClient client) : IAsyncDisposable
{
    public const int Port = 5210;
    private Process? _process;

    public Uri Address { get; } = new($"http://127.0.0.1:{Port}");
    public string Executable => Path.Combine(serverDirectory, OperatingSystem.IsWindows() ? "Homebase.Server.exe" : "Homebase.Server");
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>What the server said last, for when it stops and somebody asks why.</summary>
    public string? LastError { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return;
        LastError = null;
        var start = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = serverDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["Homebase__Port"] = Port.ToString();
        // Whether this host can be reached from outside is the server's own setting, kept beside
        // its accounts and changeable from Settings while it runs. Naming a provider here
        // would take that switch away from everybody who is not sitting at this Mac.
        _process = Process.Start(start) ?? throw new IOException("Uncloud didn’t start.");
        // Drained, or a server that fills an unread pipe stops answering while looking alive.
        _process.OutputDataReceived += (_, line) => { if (line.Data is { Length: > 0 }) logger.LogInformation("server: {Line}", line.Data); };
        _process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is not { Length: > 0 }) return;
            LastError = line.Data;
            logger.LogWarning("server: {Line}", line.Data);
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        for (var attempt = 0; attempt < 120 && IsRunning; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(new Uri(Address, "/api/health"), cancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(500, cancellationToken);
        }
        throw new IOException(IsRunning
            ? "Uncloud started but isn’t answering."
            : $"Uncloud stopped as it started. {LastError}".Trim());
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
