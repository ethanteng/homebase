using Homebase.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Uncloud.Desktop;

/// <summary>
/// Everything the app does, apart from drawing it: on the host, run Uncloud; on somebody's
/// computer, run its own Syncthing and keep ~/Uncloud in step with the host it paired with.
/// The menu bar and the setup window only ever call this.
/// </summary>
public sealed class DesktopController(
    DesktopPaths paths, string serverDirectory, ILoggerFactory loggers, HttpClient http) : IAsyncDisposable
{
    /// <summary>Off the server's 8390, so a host that is also somebody's computer can't collide.</summary>
    public const int ComputerSyncthingPort = 8391;

    private readonly ILogger _logger = loggers.CreateLogger("Uncloud.Desktop");
    private SyncthingProcess? _syncthing;
    private ComputerSync? _computer;
    private HostServer? _server;

    public DesktopSettings Settings { get; private set; } = DesktopSettings.Load(paths);
    public DesktopPaths Paths => paths;
    public string ServerDirectory => serverDirectory;
    public HostServer? Server => _server;

    /// <summary>Picks up where the app left off: the server on the host, Syncthing on a computer.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        switch (Settings.Mode)
        {
            case DesktopMode.Host:
                await StartServerAsync(cancellationToken);
                break;
            case DesktopMode.Computer:
                await StartSyncthingAsync(cancellationToken);
                break;
        }
    }

    /// <summary>Makes this computer the host: the Uncloud server runs here from now on.</summary>
    public async Task BecomeHostAsync(CancellationToken cancellationToken)
    {
        if (Settings.IsPaired)
            throw new InvalidOperationException("This computer already syncs with an Uncloud. Disconnect it first.");
        // Remembered only once it works: a host that can't start (its port taken, say) mustn't
        // become what the app insists on being every time it opens.
        try
        {
            await StartServerAsync(cancellationToken);
        }
        catch
        {
            if (_server is not null)
            {
                await _server.DisposeAsync();
                _server = null;
            }
            throw;
        }
        Save(Settings with { Mode = DesktopMode.Host });
    }

    public async Task SetReachFromAnywhereAsync(bool reach, CancellationToken cancellationToken)
    {
        Save(Settings with { ReachFromAnywhere = reach });
        if (_server is null) return;
        await _server.StopAsync();
        await StartServerAsync(cancellationToken);
    }

    public Task RestartServerAsync(CancellationToken cancellationToken) => StartServerAsync(cancellationToken);

    /// <summary>
    /// Pairs this computer with an Uncloud and sets up everything it syncs under ~/Uncloud. The
    /// code is sent once and never stored.
    /// </summary>
    public async Task<PairingResult> PairAsync(PairingLink link, string computerName, CancellationToken cancellationToken)
    {
        if (Settings.Mode is DesktopMode.Host)
            throw new InvalidOperationException("This computer is the Uncloud host; its files are already here.");
        // One Uncloud per computer: a second would sync into the same ~/Uncloud and mix two
        // accounts' files together.
        if (Settings.IsPaired)
            throw new InvalidOperationException(
                $"This computer already syncs with {Settings.AccountName ?? "an Uncloud"} at {Settings.Address}. Disconnect it first.");
        var syncthing = await StartSyncthingAsync(cancellationToken);
        var deviceId = await syncthing.DeviceIdAsync(cancellationToken);
        var result = await new PairingClient(http).PairAsync(link, deviceId, computerName, cancellationToken);
        await _computer!.ApplyAsync(result, cancellationToken);
        Save(Settings with
        {
            Mode = DesktopMode.Computer,
            Address = link.Address.ToString().TrimEnd('/'),
            AccountName = result.AccountName,
            HostDeviceId = result.HostDeviceId
        });
        return result;
    }

    public async Task<ComputerStatus> StatusAsync(CancellationToken cancellationToken)
    {
        if (_computer is null || Settings.HostDeviceId is not { } host)
            return new ComputerStatus(false, false, "Not paired with an Uncloud", []);
        try { return await _computer.StatusAsync(host, cancellationToken); }
        catch (Exception error) when (error is Homebase.Core.LibraryException or HttpRequestException)
        {
            return new ComputerStatus(false, false, error.Message, []);
        }
    }

    public Task PauseAsync(bool paused, CancellationToken cancellationToken) =>
        _computer is not null && Settings.HostDeviceId is { } host
            ? _computer.PauseAsync(host, paused, cancellationToken)
            : Task.CompletedTask;

    /// <summary>Stops syncing with the host and forgets it. Everything in ~/Uncloud stays.</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (_computer is not null && Settings.HostDeviceId is { } host)
            await _computer.ForgetAsync(host, cancellationToken);
        Save(new DesktopSettings());
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
        _server ??= new HostServer(serverDirectory, loggers.CreateLogger("Uncloud.Server"), http);
        await _server.StartAsync(Settings.ReachFromAnywhere, cancellationToken);
    }

    private async Task<ISyncthingApi> StartSyncthingAsync(CancellationToken cancellationToken)
    {
        if (_syncthing is null)
        {
            _syncthing = new SyncthingProcess(paths.Syncthing,
                SyncthingProcess.Binary(null, serverDirectory), ComputerSyncthingPort,
                loggers.CreateLogger("Uncloud.Syncthing"), http);
            await _syncthing.StartAsync(cancellationToken);
            _computer = new ComputerSync(new SyncthingApi(http, _syncthing), paths);
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(60));
        try { await _syncthing.Ready.WaitAsync(wait.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new Homebase.Core.LibraryException(_syncthing.Unavailable ?? "Syncthing didn’t start.", "sync_unavailable");
        }
        return new SyncthingApi(http, _syncthing);
    }

    private void Save(DesktopSettings settings)
    {
        settings.Save(paths);
        Settings = settings;
        _logger.LogInformation("Now {Mode}", settings.Mode);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        if (_syncthing is not null)
        {
            await _syncthing.StopAsync(CancellationToken.None);
            _syncthing.Dispose();
        }
    }
}
