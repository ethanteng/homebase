namespace Homebase.Core.Sync;

/// <summary>A computer as Syncthing sees it.</summary>
public sealed record SyncthingDevice(string DeviceId, string Name, bool Connected, string? Address, bool Paused);

/// <summary>A folder as Syncthing sees it. The device list never includes this host.</summary>
public sealed record SyncthingFolder(
    string Id,
    string Label,
    string Path,
    IReadOnlyList<string> DeviceIds,
    string? State,
    string? Error,
    int Files,
    long Bytes,
    bool Versioned);

/// <summary>A folder a paired computer has offered, which nobody on this host has taken up yet.</summary>
public sealed record SyncthingOffer(string FolderId, string Label, string DeviceId);

/// <summary>One of your own computers, as the panel shows it.</summary>
public sealed record SyncDevice(string DeviceId, string Name, bool Connected, string? Address);

/// <summary>One of your folders that is kept the same on your computers.</summary>
public sealed record SyncFolder(
    string Id,
    string Path,
    string Label,
    IReadOnlyList<string> DeviceIds,
    string? State,
    string? Error,
    int Files,
    long Bytes);

/// <summary>A folder one of your computers wants to send here.</summary>
public sealed record SyncOffer(string FolderId, string Label, string DeviceId, string DeviceName);

public sealed record SyncStatus(
    bool Available,
    string? Detail,
    string? HostDeviceId,
    IReadOnlyList<SyncDevice> Devices,
    IReadOnlyList<SyncFolder> Folders,
    IReadOnlyList<SyncOffer> Offers);

/// <summary>
/// The seam between Uncloud and the one Syncthing instance it supervises, so the ownership rules
/// are testable without the binary running. Syncthing's configuration belongs to the whole host
/// and knows nothing about accounts; everything account-shaped lives in <see cref="SyncService"/>.
/// </summary>
public interface ISyncthingApi
{
    bool IsAvailable { get; }
    string? Unavailable { get; }
    Task<string> DeviceIdAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SyncthingDevice>> DevicesAsync(CancellationToken cancellationToken);
    Task AddDeviceAsync(string deviceId, string name, CancellationToken cancellationToken);
    Task RemoveDeviceAsync(string deviceId, CancellationToken cancellationToken);
    Task PauseDeviceAsync(string deviceId, bool paused, CancellationToken cancellationToken);
    Task<IReadOnlyList<SyncthingFolder>> FoldersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SyncthingOffer>> OffersAsync(CancellationToken cancellationToken);
    /// <summary>Adds a two-way folder that keeps what the other side deletes or replaces.</summary>
    Task AddFolderAsync(string id, string label, string path, IReadOnlyList<string> deviceIds, CancellationToken cancellationToken);
    Task RemoveFolderAsync(string id, CancellationToken cancellationToken);
    Task SetFolderDevicesAsync(string id, IReadOnlyList<string> deviceIds, CancellationToken cancellationToken);
    Task SetFolderPathAsync(string id, string path, CancellationToken cancellationToken);
    Task KeepVersionsAsync(string id, CancellationToken cancellationToken);
}
