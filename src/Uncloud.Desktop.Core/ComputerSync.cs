using Homebase.Core.Sync;

namespace Uncloud.Desktop;

/// <summary>How this computer's copy stands, in words for the menu bar.</summary>
public sealed record ComputerStatus(bool Connected, bool Busy, string Summary, IReadOnlyList<SyncthingFolder> Folders);

/// <summary>
/// This computer's side of syncing: its own Syncthing, set up to keep ~/Uncloud in step with the
/// one host it paired with and nobody else.
/// </summary>
public sealed class ComputerSync(ISyncthingApi syncthing, DesktopPaths paths)
{
    /// <summary>
    /// Sets this computer up for what pairing returned: the host as the only device it talks to,
    /// the host's folders under ~/Uncloud, and anything the host shares later accepted into the
    /// same place without asking. Running it again changes nothing that is already right.
    /// </summary>
    public async Task ApplyAsync(PairingResult pairing, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Files);
        if ((await syncthing.DevicesAsync(cancellationToken)).All(device => device.DeviceId != pairing.HostDeviceId))
            await syncthing.AddDeviceAsync(pairing.HostDeviceId, "Uncloud", cancellationToken);
        // The host is the only device this Syncthing ever knows, so accepting its folders without
        // asking accepts nobody else's. A folder synced later from Uncloud simply appears.
        await syncthing.AutoAcceptFromAsync(pairing.HostDeviceId, true, cancellationToken);
        await syncthing.SetDefaultFolderPathAsync(paths.Files, cancellationToken);

        var existing = (await syncthing.FoldersAsync(cancellationToken)).Select(folder => folder.Id).ToHashSet();
        foreach (var folder in pairing.Folders)
        {
            if (existing.Contains(folder.Id)) continue;
            var path = FolderPath(folder);
            Directory.CreateDirectory(path);
            SyncService.GuardMetadata(path);
            await syncthing.AddFolderAsync(folder.Id, folder.Label, path, [pairing.HostDeviceId], cancellationToken);
        }
    }

    /// <summary>All of the account at ~/Uncloud itself; a single folder of it beside the rest.</summary>
    public string FolderPath(PairedFolder folder) =>
        folder.Path.Length == 0 ? paths.Files : Path.Combine(paths.Files, folder.Label);

    public async Task<ComputerStatus> StatusAsync(string hostDeviceId, CancellationToken cancellationToken)
    {
        if (!syncthing.IsAvailable)
            return new ComputerStatus(false, false, syncthing.Unavailable ?? "Starting…", []);
        var host = (await syncthing.DevicesAsync(cancellationToken)).FirstOrDefault(device => device.DeviceId == hostDeviceId);
        var folders = await syncthing.FoldersAsync(cancellationToken);
        var connected = host is { Connected: true };

        if (host is null) return new ComputerStatus(false, false, "Not paired with an Uncloud", folders);
        if (host.Paused) return new ComputerStatus(false, false, "Paused", folders);
        if (folders.FirstOrDefault(folder => folder.Error is not null) is { } broken)
            return new ComputerStatus(connected, false, $"Problem with {broken.Label}: {broken.Error}", folders);
        if (!connected) return new ComputerStatus(false, false, "Can’t reach your Uncloud right now", folders);
        if (folders.Count == 0) return new ComputerStatus(true, false, "Connected — nothing syncing yet", folders);
        return folders.Any(folder => folder.State is not ("idle" or null))
            ? new ComputerStatus(true, true, "Syncing…", folders)
            : new ComputerStatus(true, false, "Up to date", folders);
    }

    public async Task PauseAsync(string hostDeviceId, bool paused, CancellationToken cancellationToken)
    {
        if ((await syncthing.DevicesAsync(cancellationToken)).Any(device => device.DeviceId == hostDeviceId))
            await syncthing.PauseDeviceAsync(hostDeviceId, paused, cancellationToken);
    }

    /// <summary>Stops syncing with the host. Everything in ~/Uncloud stays where it is.</summary>
    public async Task ForgetAsync(string hostDeviceId, CancellationToken cancellationToken)
    {
        foreach (var folder in await syncthing.FoldersAsync(cancellationToken))
            await syncthing.RemoveFolderAsync(folder.Id, cancellationToken);
        if ((await syncthing.DevicesAsync(cancellationToken)).Any(device => device.DeviceId == hostDeviceId))
            await syncthing.RemoveDeviceAsync(hostDeviceId, cancellationToken);
    }
}
