using Homebase.Core.Sync;

namespace Homebase.Tests;

/// <summary>
/// Syncthing's configuration as an in-memory host-wide list, which is exactly what makes it
/// dangerous: it knows nothing about accounts, so every rule about who sees what is Uncloud's.
/// </summary>
public sealed class FakeSyncthing : ISyncthingApi
{
    public const string Self = "AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH";

    public Dictionary<string, SyncthingDevice> Devices { get; } = [];
    public Dictionary<string, SyncthingFolder> Folders { get; } = [];
    public List<SyncthingOffer> Offers { get; } = [];
    /// <summary>What the folder's ignore file said at the moment the folder was added.</summary>
    public Dictionary<string, string?> IgnoresWhenAdded { get; } = [];
    public string? Down { get; set; }
    /// <summary>Makes the next device removal fail, as a Syncthing that stops answering would.</summary>
    public bool FailNextDeviceRemoval { get; set; }

    public bool IsAvailable => Down is null;
    public string? Unavailable => Down;

    public Task<string> DeviceIdAsync(CancellationToken cancellationToken) => Task.FromResult(Self);

    public Task<IReadOnlyList<SyncthingDevice>> DevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SyncthingDevice>>(Devices.Values.ToArray());

    /// <summary>Holds every device add until it completes, as a slow Syncthing would.</summary>
    public Task? HoldDeviceAdds { get; set; }

    public async Task AddDeviceAsync(string deviceId, string name, CancellationToken cancellationToken)
    {
        if (HoldDeviceAdds is { } hold) await hold.WaitAsync(cancellationToken);
        if (Devices.ContainsKey(deviceId)) throw new InvalidOperationException($"{deviceId} added twice");
        Devices[deviceId] = new SyncthingDevice(deviceId, name, false, null, false);
    }

    public Task RemoveDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (FailNextDeviceRemoval)
        {
            FailNextDeviceRemoval = false;
            throw new Homebase.Core.LibraryException("Uncloud couldn’t reach Syncthing.", "sync_unavailable");
        }
        Devices.Remove(deviceId);
        foreach (var (id, folder) in Folders.ToArray())
            Folders[id] = folder with { DeviceIds = folder.DeviceIds.Where(device => device != deviceId).ToArray() };
        return Task.CompletedTask;
    }

    public Task PauseDeviceAsync(string deviceId, bool paused, CancellationToken cancellationToken)
    {
        Devices[deviceId] = Devices[deviceId] with { Paused = paused };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SyncthingFolder>> FoldersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SyncthingFolder>>(Folders.Values.ToArray());

    public Task<IReadOnlyList<SyncthingOffer>> OffersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SyncthingOffer>>(
            Offers.Where(offer => !Folders.ContainsKey(offer.FolderId)).ToArray());

    /// <summary>
    /// How far each device has got with each folder. Anything not set here is a device the folder is
    /// shared with having all of it, as a quiet Syncthing would say.
    /// </summary>
    public Dictionary<(string Folder, string Device), SyncthingProgress> Progress { get; } = [];

    public Task<SyncthingProgress?> ProgressAsync(string folderId, string deviceId, CancellationToken cancellationToken) =>
        Task.FromResult(Progress.TryGetValue((folderId, deviceId), out var progress)
            ? progress
            : Folders.TryGetValue(folderId, out var folder) && folder.DeviceIds.Contains(deviceId)
                ? new SyncthingProgress(SyncthingProgress.Valid, 100, 0, 0)
                : null);

    public HashSet<string> PausedFolders { get; } = [];

    public Task PauseFolderAsync(string id, bool paused, CancellationToken cancellationToken)
    {
        if (!Folders.ContainsKey(id)) throw new InvalidOperationException($"{id} isn’t a folder");
        if (paused) PausedFolders.Add(id); else PausedFolders.Remove(id);
        return Task.CompletedTask;
    }

    public Task AddFolderAsync(string id, string label, string path, IReadOnlyList<string> deviceIds, long? keepFree, CancellationToken cancellationToken)
    {
        if (Folders.ContainsKey(id)) throw new InvalidOperationException($"{id} added twice");
        var ignore = Path.Combine(path, SyncService.IgnoreFile);
        IgnoresWhenAdded[id] = File.Exists(ignore) ? File.ReadAllText(ignore) : null;
        Folders[id] = new SyncthingFolder(id, label, path, deviceIds, "idle", null, 0, 0, true, keepFree);
        return Task.CompletedTask;
    }

    /// <summary>How many times a folder's free space has been changed, since changing it restarts the folder.</summary>
    public int KeepFreeChanges { get; private set; }

    public Task SetKeepFreeAsync(string id, long? keepFree, CancellationToken cancellationToken)
    {
        Folders[id] = Folders[id] with { KeepFree = keepFree };
        KeepFreeChanges++;
        return Task.CompletedTask;
    }

    public Task RemoveFolderAsync(string id, CancellationToken cancellationToken)
    {
        Folders.Remove(id);
        PausedFolders.Remove(id);
        return Task.CompletedTask;
    }

    public Task SetFolderDevicesAsync(string id, IReadOnlyList<string> deviceIds, CancellationToken cancellationToken)
    {
        Folders[id] = Folders[id] with { DeviceIds = deviceIds };
        return Task.CompletedTask;
    }

    public Task SetFolderPathAsync(string id, string path, CancellationToken cancellationToken)
    {
        Folders[id] = Folders[id] with { Path = path };
        return Task.CompletedTask;
    }

    public HashSet<string> AutoAccepting { get; } = [];

    public Task AutoAcceptFromAsync(string deviceId, bool accept, CancellationToken cancellationToken)
    {
        if (accept) AutoAccepting.Add(deviceId); else AutoAccepting.Remove(deviceId);
        return Task.CompletedTask;
    }

    public Task KeepVersionsAsync(string id, CancellationToken cancellationToken)
    {
        Folders[id] = Folders[id] with { Versioned = true };
        return Task.CompletedTask;
    }
}
