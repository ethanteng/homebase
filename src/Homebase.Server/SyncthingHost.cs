using Homebase.Core.Sync;

namespace Homebase.Server;

/// <summary>
/// The host's Syncthing, started when Uncloud starts and stopped when it stops. Syncthing missing
/// is not an error — the rest of Uncloud works, and the My computers panel says what's wrong.
/// </summary>
/// <remarks>
/// A build made by scripts/publish-macos.sh (or scripts/run.sh) carries its own Syncthing beside
/// the application, which is used ahead of anything on the PATH, so nobody has to install it.
/// </remarks>
public sealed class SyncthingHost(
    string configDirectory, IConfiguration configuration, ILogger<SyncthingHost> logger, HttpClient client)
    : IHostedService, ISyncthingEndpoint, IDisposable
{
    // Syncthing's device identity and pairings live here. It must be the same durable directory
    // the rest of Uncloud uses: a temporary one loses every pairing when it is cleaned.
    private readonly SyncthingProcess _syncthing = new(
        Path.Combine(configDirectory, "syncthing"),
        SyncthingProcess.Binary(configuration["Homebase:Syncthing:Path"], AppContext.BaseDirectory),
        configuration.GetValue("Homebase:Syncthing:GuiPort", 8390),
        logger, client);

    /// <summary>Where Syncthing's device identity and pairings are kept.</summary>
    public string Home => _syncthing.Home;
    /// <summary>Which Syncthing is run.</summary>
    public string Executable => _syncthing.Executable;
    public bool IsReady => _syncthing.IsReady;
    public string? Unavailable => _syncthing.Unavailable;
    public Uri? BaseAddress => _syncthing.BaseAddress;
    public string? ApiKey => _syncthing.ApiKey;
    /// <summary>Completes once Syncthing answers, and never if it doesn't.</summary>
    public Task Ready => _syncthing.Ready;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration.GetValue("Homebase:Syncthing:Enabled", true))
            return _syncthing.StartAsync(cancellationToken);
        _syncthing.Unavailable = "Syncing with other computers is switched off.";
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _syncthing.StopAsync(cancellationToken);

    public void Dispose() => _syncthing.Dispose();
}
