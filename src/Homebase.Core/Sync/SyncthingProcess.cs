using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Sync;

/// <summary>
/// One Syncthing, run as a child process with its own home directory and a loopback-only API on
/// a port of its own: generated on first use, configured so only this process can drive it,
/// never upgrading itself, and stopped with its parent. The Uncloud server runs one for the
/// host, and the Uncloud app runs one for the computer it is on.
/// </summary>
public sealed class SyncthingProcess(string home, string binary, int port, ILogger logger, HttpClient client)
    : ISyncthingEndpoint, IDisposable
{
    private readonly string _home = home;
    private readonly string _binary = binary;
    private readonly int _port = port;
    private Process? _process;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            _process = Process.Start(serve) ?? throw new IOException("Syncthing did not start.");
            logger.LogInformation("Starting Syncthing from {Binary}", _binary);
            // Redirected pipes must be drained. A long-running Syncthing that fills an unread
            // buffer blocks on its next write and stops syncing while still looking healthy.
            _process.OutputDataReceived += (_, line) =>
            {
                if (line.Data is { Length: > 0 }) logger.LogDebug("syncthing: {Line}", line.Data);
            };
            _process.ErrorDataReceived += (_, line) =>
            {
                if (line.Data is { Length: > 0 }) logger.LogWarning("syncthing: {Line}", line.Data);
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            // Readiness is awaited in the background: a slow Syncthing must not hold up the app.
            _ = WaitForReadyAsync();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Unavailable = _binary == "syncthing"
                ? "This build of Uncloud doesn’t include Syncthing, and there isn’t one installed. Run scripts/fetch-syncthing.sh, or install Syncthing."
                : $"Uncloud couldn’t start Syncthing ({_binary}).";
            logger.LogWarning(error, "Syncthing unavailable");
        }
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

    private async Task WaitForReadyAsync()
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (_process?.HasExited == true) break;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseAddress!, "/rest/system/ping"));
                request.Headers.Add("X-API-Key", ApiKey ?? "");
                using var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    IsReady = true;
                    Unavailable = null;
                    _ready.TrySetResult();
                    logger.LogInformation("Syncthing is ready on {Address}", BaseAddress);
                    return;
                }
            }
            catch (HttpRequestException) { }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        Unavailable = "Syncthing didn’t come up. Check that it is installed and can run.";
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
        IsReady = false;
        if (_process is null || _process.HasExited) return;
        try
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or SystemException)
        {
            logger.LogWarning(error, "Syncthing did not stop cleanly");
        }
    }

    public void Dispose() => _process?.Dispose();
}
