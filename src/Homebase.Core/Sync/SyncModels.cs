namespace Homebase.Core.Sync;

/// <summary>A computer as Syncthing sees it.</summary>
public sealed record SyncthingDevice(string DeviceId, string Name, bool Connected, string? Address, bool Paused);

/// <summary>A folder as Syncthing sees it. The device list never includes this host.</summary>
/// <param name="KeepFree">
/// The free space, in bytes, Syncthing leaves on the folder's drive before it stops bringing files
/// in, or null for a share of the drive, which is how Syncthing sets it by itself.
/// </param>
/// <param name="WaitingForRoom">Files a computer sent that Syncthing is holding back for want of room.</param>
public sealed record SyncthingFolder(
    string Id,
    string Label,
    string Path,
    IReadOnlyList<string> DeviceIds,
    string? State,
    string? Error,
    int Files,
    long Bytes,
    bool Versioned,
    long? KeepFree = null,
    int WaitingForRoom = 0);

/// <summary>A folder a paired computer has offered, which nobody on this host has taken up yet.</summary>
public sealed record SyncthingOffer(string FolderId, string Label, string DeviceId);

/// <summary>One of your own computers, as the panel shows it.</summary>
public sealed record SyncDevice(string DeviceId, string Name, bool Connected, string? Address);

/// <summary>One of your folders that is kept the same on your computers.</summary>
/// <param name="WaitingForRoom">Files from your computers that can't arrive until this Uncloud has room.</param>
public sealed record SyncFolder(
    string Id,
    string Path,
    string Label,
    IReadOnlyList<string> DeviceIds,
    string? State,
    string? Error,
    int Files,
    long Bytes,
    int WaitingForRoom = 0);

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
    /// <summary>
    /// Adds a two-way folder that keeps what the other side deletes or replaces, and leaves
    /// <paramref name="keepFree"/> bytes free on its drive (Syncthing's own share of it when null).
    /// </summary>
    Task AddFolderAsync(string id, string label, string path, IReadOnlyList<string> deviceIds, long? keepFree, CancellationToken cancellationToken);
    /// <summary>
    /// How much free space a folder leaves on its drive before Syncthing stops bringing files into
    /// it: bytes, or null for Syncthing's own share of the drive.
    /// </summary>
    Task SetKeepFreeAsync(string id, long? keepFree, CancellationToken cancellationToken);
    Task RemoveFolderAsync(string id, CancellationToken cancellationToken);
    Task SetFolderDevicesAsync(string id, IReadOnlyList<string> deviceIds, CancellationToken cancellationToken);
    Task SetFolderPathAsync(string id, string path, CancellationToken cancellationToken);
    Task KeepVersionsAsync(string id, CancellationToken cancellationToken);
    /// <summary>
    /// Whether a folder this device offers is set up here without asking. Only ever turned on by
    /// the Uncloud app, for the one host it paired with.
    /// </summary>
    Task AutoAcceptFromAsync(string deviceId, bool accept, CancellationToken cancellationToken);
    /// <summary>Where a folder accepted without asking is put.</summary>
    Task SetDefaultFolderPathAsync(string path, CancellationToken cancellationToken);
}
