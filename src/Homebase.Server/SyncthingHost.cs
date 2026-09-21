using System.Diagnostics;
using System.Xml.Linq;
using Homebase.Core.Nodes;

namespace Homebase.Server;

/// <summary>
/// Runs Syncthing as a child of Homebase: its own home directory, its own loopback-only GUI port,
/// started when Homebase starts and stopped when it stops. Syncthing missing is not an error —
/// the rest of Homebase works, and the Nodes panel explains what to install.
/// </summary>
public sealed class SyncthingHost(IConfiguration configuration, ILogger<SyncthingHost> logger, HttpClient client)
    : IHostedService, ISyncthingEndpoint, IDisposable
{
    private readonly string _home = Path.Combine(
        configuration["Homebase:ConfigDirectory"] ?? Path.GetTempPath(), "syncthing");
    private readonly string _binary = configuration["Homebase:Syncthing:Path"] ?? "syncthing";
    private readonly int _port = configuration.GetValue("Homebase:Syncthing:GuiPort", 8390);
    private Process? _process;

    public bool IsReady { get; private set; }
    public string? Unavailable { get; private set; } = "Homebase is still starting Syncthing.";
    public Uri? BaseAddress { get; private set; }
    public string? ApiKey { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Homebase:Syncthing:Enabled", true))
        {
            Unavailable = "Syncing with other computers is switched off.";
            return;
        }
        try
        {
            Directory.CreateDirectory(_home);
            if (!File.Exists(Path.Combine(_home, "config.xml")))
                await RunAsync(["generate", "--home", _home, "--no-default-folder"], cancellationToken);

            ApiKey = Configure();
            BaseAddress = new Uri($"http://127.0.0.1:{_port}");
            _process = Process.Start(new ProcessStartInfo(_binary)
            {
                ArgumentList = { "serve", "--home", _home, "--no-browser" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }) ?? throw new IOException("Syncthing did not start.");
            // Readiness is awaited in the background: a slow Syncthing must not hold up the app.
            _ = WaitForReadyAsync();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Unavailable = $"Homebase couldn’t start Syncthing ({_binary}). Install it, or set Homebase__Syncthing__Path.";
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
