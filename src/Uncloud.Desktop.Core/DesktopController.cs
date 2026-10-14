using System.Diagnostics;
using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Core.Providers;
using Homebase.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Uncloud.Desktop;

/// <summary>
/// Everything the app does, apart from drawing it: on the host, see that Uncloud runs; on
/// somebody's computer, run its own Syncthing and keep ~/Uncloud in step with the host it paired
/// with. The menu bar and the setup window only ever call this.
///
/// On a Mac host, Uncloud runs in an account of its own as a background service, which this app
/// sets up once and then only watches over — handing it the Mac's folders, which that account
/// can't read. A host set up before that runs Uncloud as before, as this app's child, until
/// somebody here agrees to move it; so does a build run from source, which has no service to set up.
/// </summary>
/// <param name="hostConfig">
/// Where an Uncloud run as the person signed in here keeps its settings; replaced in tests.
/// </param>
/// <param name="readPower">How this Mac's power settings are read; replaced in tests.</param>
public sealed class DesktopController(
    DesktopPaths paths, string serverDirectory, ILoggerFactory loggers, HttpClient http,
    IHostInstall? install = null, string? hostConfig = null, Func<PowerState?>? readPower = null) : IAsyncDisposable
{
    /// <summary>Off the server's 8390, so a host that is also somebody's computer can't collide.</summary>
    public const int ComputerSyncthingPort = 8391;

    /// <summary>Where Uncloud answers on the host, whichever way it runs.</summary>
    public static readonly Uri HostAddress = new($"http://127.0.0.1:{HostServer.Port}");

    private readonly ILogger _logger = loggers.CreateLogger("Uncloud.Desktop");
    private readonly IHostInstall _install = install ?? new HostInstall(serverDirectory, loggers.CreateLogger("Uncloud.Install"));
    private readonly string _hostConfig = hostConfig ?? HostPaths.DefaultConfigDirectory;
    private SyncthingProcess? _syncthing;
    private ComputerSync? _computer;
    private HostServer? _server;
    private HostFoldersBridge? _bridge;
    private readonly Func<PowerState?> _readPower = readPower ?? (() => HostPower.Read());
    private PowerState? _power;
    private bool _powerRead;
    private DateTimeOffset _powerCheckedAt = DateTimeOffset.MinValue;

    public DesktopSettings Settings { get; private set; } = DesktopSettings.Load(paths);
    public DesktopPaths Paths => paths;
    public string ServerDirectory => serverDirectory;
    public HostServer? Server => _server;

    /// <summary>Whether this copy of the app sets a host up in an account of its own, rather than running it as this person.</summary>
    public bool CanSetUpPrivately => _install.Packaged;

    /// <summary>Why this copy can't set Uncloud up in an account of its own from where it is, or null when it can.</summary>
    public string? CannotSetUpPrivately => _install.Packaged ? _install.CannotInstall : null;

    /// <summary>Whether Uncloud on this host runs in an account of its own, as a background service.</summary>
    public bool RunsInOwnAccount => Settings.Mode is DesktopMode.Host && _install.IsInstalled;

    /// <summary>
    /// Whether this host still runs Uncloud as the person signed in here — where they could look
    /// through everybody's files — and this copy of the app could move it into an account of its own.
    /// </summary>
    public bool CanKeepPrivate => Settings.Mode is DesktopMode.Host && !_install.IsInstalled && _install.Packaged;

    /// <summary>
    /// Whether becoming a host would ask where everyone's files go: only when there is no Uncloud on
    /// this Mac already — one run as this person, or one turned off with everything kept — whose
    /// files stay where they are.
    /// </summary>
    public bool AsksWhereFilesGo => _install.Packaged && !HasKeptHost
        && !File.Exists(Path.Combine(_hostConfig, "homebase.db"));

    /// <summary>Whether Uncloud's own account already keeps everybody's files here, from before it was turned off.</summary>
    public bool HasKeptHost => _install.HasKeptHost;

    /// <summary>Whether Uncloud keeps this host awake, and starts it again after a power cut where it can.</summary>
    public bool KeepsAwake => RunsInOwnAccount && _install.KeepsAwake;

    /// <summary>
    /// Whether to offer keeping this Mac awake where a password is asked anyway — on updating, say —
    /// because it doesn't yet and nobody here has said not to.
    /// </summary>
    public bool OffersToKeepAwake => _install.Packaged && !_install.KeepsAwake && !Settings.AskedToKeepAwake;

    /// <summary>This Mac's power settings, read the first time they're asked about; null where they can't be.</summary>
    public PowerState? Power
    {
        get
        {
            if (!_powerRead)
            {
                _power = _readPower();
                _powerRead = true;
            }
            return _power;
        }
    }

    /// <summary>
    /// On the host, what could stop it staying on for everybody, worst first, as last checked — all
    /// but FileVault, which is on for most Macs and nothing to put right. A line in the menu that
    /// never goes away soon stops being read, so that one is said in Settings only.
    /// </summary>
    public IReadOnlyList<PowerFinding> PowerFindings { get; private set; } = [];

    /// <summary>
    /// Reads this host's power settings again, at most once a minute — the menu asks every few
    /// seconds — or straight away after anything here changed them.
    /// </summary>
    public async Task CheckPowerAsync(CancellationToken cancellationToken)
    {
        if (Settings.Mode is not DesktopMode.Host)
        {
            PowerFindings = [];
            return;
        }
        if (DateTimeOffset.UtcNow - _powerCheckedAt < TimeSpan.FromMinutes(1)) return;
        _powerCheckedAt = DateTimeOffset.UtcNow;
        _power = await Task.Run(_readPower, cancellationToken);
        _powerRead = true;
        PowerFindings = _power is null
            ? []
            : HostPower.Findings(_power, _install.KeepsAwake, RunsInOwnAccount)
                .Where(finding => finding.Code != "filevault").ToList();
    }

    /// <summary>Has the next check read the power settings again, because something here just changed them.</summary>
    private void PowerChanged() => _powerCheckedAt = DateTimeOffset.MinValue;

    /// <summary>Picks up where the app left off: Uncloud on the host, Syncthing on a computer.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        switch (Settings.Mode)
        {
            case DesktopMode.Host when _install.IsInstalled:
                await StartBridgeAsync(cancellationToken);
                break;
            case DesktopMode.Host:
                await StartServerAsync(cancellationToken);
                break;
            case DesktopMode.Computer:
                await StartSyncthingAsync(cancellationToken);
                break;
        }
    }

    /// <summary>Makes this computer the host: the Uncloud server runs here from now on.</summary>
    public Task BecomeHostAsync(CancellationToken cancellationToken) => BecomeHostAsync(null, cancellationToken);

    /// <summary>
    /// Makes this computer the host. Packaged, it runs Uncloud in an account of its own, with
    /// everyone's files in <paramref name="root"/>, or in a folder on this Mac when that is null.
    /// </summary>
    /// <param name="inTerminal">Whether to ask for the password in Terminal rather than in macOS's prompt.</param>
    /// <param name="awake">Whether to keep this Mac awake as well; null leaves it as it is.</param>
    public async Task BecomeHostAsync(string? root, CancellationToken cancellationToken, bool inTerminal = false,
        bool? awake = null)
    {
        if (Settings.IsPaired)
            throw new InvalidOperationException("This computer already syncs with an Uncloud. Disconnect it first.");
        if (_install.Packaged)
        {
            await KeepPrivateAsync(root, cancellationToken, inTerminal, awake);
            Save(Settings with { Mode = DesktopMode.Host });
            return;
        }
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

    /// <summary>
    /// Moves Uncloud into an account of its own, as a background service, taking along everything a
    /// host that ran as this person already had — or, for one already there, brings it up to date.
    /// Asks for an administrator's password. Until it succeeds nothing changes, and a host that was
    /// running carries on as it was.
    /// </summary>
    /// <param name="awake">Whether to keep this Mac awake as well; null leaves it as it is.</param>
    public async Task KeepPrivateAsync(string? root, CancellationToken cancellationToken, bool inTerminal = false,
        bool? awake = null)
    {
        if (_install.CannotInstall is { } why) throw new InvalidOperationException(why);
        var legacy = _hostConfig;
        var hadServer = _server is { IsRunning: true };
        // Written while the settings are still this person's to write: once they move, they aren't.
        CarryOverReachFromAnywhere();
        // Stopped first, because its files are about to change hands and its port is the service's.
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
        try
        {
            await _install.InstallAsync(root, File.Exists(Path.Combine(legacy, "homebase.db")) ? legacy : null,
                cancellationToken, inTerminal, awake);
        }
        catch (Exception failure)
        {
            if (failure is OperationCanceledException)
                _logger.LogInformation("Setting Uncloud up in its own account was cancelled; nothing changed");
            else
                _logger.LogWarning(failure, "Setting Uncloud up in its own account didn’t finish");
            // Nobody in the household is left without Uncloud because a password wasn't typed.
            if (hadServer)
                try { await StartServerAsync(CancellationToken.None); }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _logger.LogWarning(error, "Uncloud didn’t start again as it was");
                }
            throw;
        }
        if (awake is not null) Save(Settings with { AskedToKeepAwake = true });
        PowerChanged();
        await StartBridgeAsync(cancellationToken);
    }

    /// <summary>
    /// Keeps this Mac from going to sleep while it is the host, and has it start again by itself
    /// after a power cut where it can — or lets it sleep as it was set to again. Asks for an
    /// administrator's password.
    /// </summary>
    public async Task KeepAwakeAsync(bool awake, CancellationToken cancellationToken, bool inTerminal = false)
    {
        if (!RunsInOwnAccount)
            throw new InvalidOperationException("Uncloud keeps this Mac awake once it runs in an account of its own.");
        await _install.KeepAwakeAsync(awake, cancellationToken, inTerminal);
        Save(Settings with { AskedToKeepAwake = true });
        PowerChanged();
    }

    /// <summary>
    /// Opens a folder or drive to Uncloud's account, which asks for an administrator's password,
    /// and says which folder to choose in Settings to keep everyone's files there.
    /// </summary>
    public Task<string> PrepareFolderAsync(string folder, CancellationToken cancellationToken, bool inTerminal = false) =>
        _install.PrepareAsync(folder, cancellationToken, inTerminal);

    /// <summary>
    /// Stops Uncloud on this Mac, which asks for an administrator's password. Everybody's files and
    /// accounts stay, still locked to Uncloud's account, and making this Mac the host again picks
    /// them up where they were.
    /// </summary>
    public async Task TurnOffAsync(CancellationToken cancellationToken, bool inTerminal = false)
    {
        await _install.UninstallAsync(cancellationToken, inTerminal);
        if (_bridge is not null)
        {
            await _bridge.DisposeAsync();
            _bridge = null;
        }
        Save(new DesktopSettings());
    }

    /// <summary>How Uncloud on this host is doing, in a line for the menu.</summary>
    public async Task<string> HostStatusAsync(CancellationToken cancellationToken)
    {
        if (!_install.IsInstalled)
            return _server is { IsRunning: true }
                ? "Uncloud is running"
                : $"Uncloud has stopped. {_server?.LastError}".Trim();
        if (_install.InstalledServer is { } program && !File.Exists(program))
            return "Uncloud on this Mac is missing a part. Choose Update Uncloud to put it back.";
        // Asked every few seconds by the menu, so it is not kept waiting long by a server that
        // hasn't come up yet.
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        patience.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var answer = await http.GetAsync(new Uri(HostAddress, "/api/health"), patience.Token);
            if (answer.IsSuccessStatusCode)
                return NeedsUpdate
                    ? "Uncloud is running the version before this app. Choose Update Uncloud."
                    : "Uncloud is running";
        }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return NeedsUpdate
            ? "Uncloud isn’t answering, and needs updating to this app. Choose Update Uncloud."
            : "Uncloud isn’t answering. It starts again by itself; if it doesn’t, restart this Mac.";
    }

    /// <summary>Remembers that this host has been asked once to keep everyone's files private.</summary>
    public void AskedToKeepPrivate() => Save(Settings with { AskedToKeepPrivate = true });

    /// <summary>
    /// Whether the service's own copy of Uncloud is behind this app, or gone, so that setting it up
    /// again — which asks for an administrator's password — would bring it up to date. It runs from a
    /// copy only an administrator can change, so updating the app alone doesn't reach it.
    /// </summary>
    public bool NeedsUpdate => _install.IsInstalled
        && (_install.InstalledServer is { } program && !File.Exists(program)
            || _install.Version is { } mine && _install.InstalledVersion != mine);

    /// <summary>
    /// Hands Uncloud this Mac's folders, read as the person signed in here. Not being able to is no
    /// reason for the app not to open: bringing files in from this Mac's folders waits until it can.
    /// </summary>
    private async Task StartBridgeAsync(CancellationToken cancellationToken)
    {
        if (_bridge is not null) return;
        try
        {
            _bridge = await HostFoldersBridge.StartAsync(_install.BridgeSocket, new LocalHostFolders(),
                _install.Private, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "Couldn’t hand this Mac’s folders to Uncloud");
        }
    }

    /// <summary>
    /// The same switch Settings has, for somebody who is at this Mac rather than in a
    /// browser. It writes what the server reads, so the two can never disagree — and restarts the
    /// server, because from out here there is no signed-in way to ask it to do this where it
    /// stands, which is what the panel does.
    /// </summary>
    public async Task SetReachFromAnywhereAsync(bool reach, CancellationToken cancellationToken)
    {
        // In an account of its own, its settings are its own too, and changed in Settings.
        if (_install.IsInstalled)
            throw new InvalidOperationException("Change this in Uncloud’s Settings, in your browser.");
        Reachability.SetSetting(HostPaths.RemoteAccessSetting, reach ? "builtin" : "none");
        if (_server is null) return;
        await _server.StopAsync();
        await StartServerAsync(cancellationToken);
    }

    public Task RestartServerAsync(CancellationToken cancellationToken) => StartServerAsync(cancellationToken);

    private DateTimeOffset _restartedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Starts Uncloud again if it stopped by itself, on a host that runs it as this app's child —
    /// rather than leaving the household without it until somebody here notices and clicks. Not
    /// more than twice a minute, so a server that can't stay up isn't started over and over. In an
    /// account of its own, launchd does this instead.
    /// </summary>
    public async Task KeepServerRunningAsync(CancellationToken cancellationToken)
    {
        if (Settings.Mode is not DesktopMode.Host || _install.IsInstalled || _server is not { IsRunning: false }) return;
        if (DateTimeOffset.UtcNow - _restartedAt < TimeSpan.FromSeconds(30)) return;
        _restartedAt = DateTimeOffset.UtcNow;
        _logger.LogWarning("Starting Uncloud again after it stopped by itself");
        try { await StartServerAsync(cancellationToken); }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(error, "Uncloud didn’t start again");
        }
    }

    /// <summary>
    /// Pairs this computer with an Uncloud and sets up everything it syncs under ~/Uncloud. The
    /// code is sent once and never stored.
    /// </summary>
    public async Task<PairingResult> PairAsync(PairingLink link, string computerName, CancellationToken cancellationToken)
    {
        var deviceId = await ReadyToPairAsync(cancellationToken);
        var result = await new PairingClient(http).PairAsync(link, deviceId, computerName, cancellationToken);
        await FinishPairingAsync(link.Address, result, cancellationToken);
        return result;
    }

    /// <summary>
    /// Every Uncloud announcing itself on this network, so the person doesn't have to know the
    /// address. Usually one; none away from home, or before the person lets Uncloud look.
    /// </summary>
    public Task<IReadOnlyList<FoundHost>> FindAsync(CancellationToken cancellationToken) =>
        Bonjour.FindAsync(TimeSpan.FromSeconds(2), cancellationToken);

    /// <summary>Asks the Uncloud at this address to add this computer, for its owner to approve.</summary>
    public async Task<PairingRequest> RequestPairingAsync(Uri address, string computerName, CancellationToken cancellationToken)
    {
        var deviceId = await ReadyToPairAsync(cancellationToken);
        return await new PairingClient(http).RequestAsync(address, deviceId, computerName, cancellationToken);
    }

    /// <summary>
    /// Finishes pairing if somebody has approved the request, and answers null while nobody has.
    /// Everything is set up under ~/Uncloud before this returns, as it is for a code.
    /// </summary>
    public async Task<PairingResult?> CollectPairingAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        if (await new PairingClient(http).AnswerAsync(request, cancellationToken) is not { } result) return null;
        await StartSyncthingAsync(cancellationToken);
        await FinishPairingAsync(request.Address, result, cancellationToken);
        return result;
    }

    /// <summary>
    /// What this computer is called where people see it — “Ada’s MacBook Air”, as Sharing in
    /// System Settings has it — rather than its network name, Adas-MacBook-Air. It is what the
    /// person approving on their phone is asked to recognise.
    /// </summary>
    public static string ComputerName()
    {
        if (OperatingSystem.IsMacOS())
            try
            {
                using var scutil = Process.Start(new ProcessStartInfo("/usr/sbin/scutil", "--get ComputerName")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                });
                if (scutil is not null)
                {
                    var name = scutil.StandardOutput.ReadToEnd().Trim();
                    scutil.WaitForExit(2000);
                    if (scutil.HasExited && scutil.ExitCode == 0 && name.Length > 0) return name;
                }
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return Environment.MachineName;
    }

    /// <summary>This computer's own Syncthing, running, and its device ID — once pairing is allowed at all.</summary>
    private async Task<string> ReadyToPairAsync(CancellationToken cancellationToken)
    {
        if (Settings.Mode is DesktopMode.Host)
            throw new InvalidOperationException("This computer is the Uncloud host; its files are already here.");
        // One Uncloud per computer: a second would sync into the same ~/Uncloud and mix two
        // accounts' files together.
        if (Settings.IsPaired)
            throw new InvalidOperationException(
                $"This computer already syncs with {Settings.AccountName ?? "an Uncloud"} at {Settings.Address}. Disconnect it first.");
        var syncthing = await StartSyncthingAsync(cancellationToken);
        return await syncthing.DeviceIdAsync(cancellationToken);
    }

    private async Task FinishPairingAsync(Uri address, PairingResult result, CancellationToken cancellationToken)
    {
        await _computer!.ApplyAsync(result, cancellationToken);
        Save(Settings with
        {
            Mode = DesktopMode.Computer,
            Address = address.ToString().TrimEnd('/'),
            AccountName = result.AccountName,
            HostDeviceId = result.HostDeviceId
        });
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
        CarryOverReachFromAnywhere();
        _server ??= new HostServer(serverDirectory, loggers.CreateLogger("Uncloud.Server"), http);
        await _server.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Carried across once, for a host that was reached from anywhere before the server kept that
    /// setting itself. Otherwise an upgrade would quietly shut the door.
    /// </summary>
    private void CarryOverReachFromAnywhere()
    {
        if (!Settings.ReachFromAnywhere) return;
        if (Reachability.Setting(HostPaths.RemoteAccessSetting) is null)
            Reachability.SetSetting(HostPaths.RemoteAccessSetting, "builtin");
        Save(Settings with { ReachFromAnywhere = false });
    }

    /// <summary>The server's own settings, which it reads at startup and writes while running.</summary>
    private ControlDatabase Reachability => new(_hostConfig);

    /// <summary>
    /// Whether this host opens a way in from outside, as the server has it. Not known out here when
    /// it runs in an account of its own, whose settings only it can read: false, and Settings says.
    /// </summary>
    public bool ReachFromAnywhere =>
        !_install.IsInstalled
        && Reachability.Setting(HostPaths.RemoteAccessSetting) is { } kept
        && !kept.Equals("none", StringComparison.OrdinalIgnoreCase);

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
        if (_bridge is not null) await _bridge.DisposeAsync();
        if (_syncthing is not null)
        {
            await _syncthing.StopAsync(CancellationToken.None);
            _syncthing.Dispose();
        }
    }
}
