namespace Homebase.Core.Sync;

/// <summary>A computer as Syncthing sees it.</summary>
public sealed record SyncthingDevice(string DeviceId, string Name, bool Connected, string? Address, bool Paused);

/// <summary>A folder as Syncthing sees it. The device list never includes this host.</summary>
/// <param name="KeepFree">
/// The free space, in bytes, Syncthing leaves on the folder's drive before it stops bringing files
/// in, or null for a share of the drive, which is how Syncthing sets it by itself.
/// </param>
/// <param name="WaitingForRoom">Files a computer sent that Syncthing is holding back for want of room.</param>
/// <param name="Paused">Stopped in Syncthing's configuration, keeping what it has.</param>
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
    int WaitingForRoom = 0,
    bool Paused = false);

/// <summary>A folder a paired computer has offered, which nobody on this host has taken up yet.</summary>
public sealed record SyncthingOffer(string FolderId, string Label, string DeviceId);

/// <summary>
/// How far another device has got with a folder, as this Syncthing last heard from it. Known only
/// from what that device last said, so a device out of reach is as far as it was when it went.
/// </summary>
/// <param name="State">
/// Syncthing's word for whether the other device has the folder at all: <c>valid</c> when it does,
/// <c>notSharing</c> when it doesn't share it back, <c>paused</c>, or <c>unknown</c> while it's away
/// or hasn't said yet — when how far it had got is still what's known of it.
/// </param>
/// <param name="Completion">How much of the folder it has, out of 100.</param>
/// <param name="NeedBytes">What it still has to bring in.</param>
/// <param name="NeedItems">Files and folders it still has to catch up on.</param>
/// <param name="NeedDeletes">
/// Deletions it still has to make. Syncthing counts these apart from <paramref name="NeedItems"/>,
/// and they take no bytes, so a computer with only these left would otherwise look finished.
/// </param>
public sealed record SyncthingProgress(string State, double Completion, long NeedBytes, int NeedItems, int NeedDeletes = 0)
{
    public const string Valid = "valid";
    public const string NotSharing = "notSharing";
}

/// <summary>One of your own computers, as the panel shows it.</summary>
public sealed record SyncDevice(string DeviceId, string Name, bool Connected, string? Address);

/// <summary>One of your folders that is kept the same on your computers.</summary>
/// <param name="State">How this Uncloud's own copy stands: idle once it has everything.</param>
/// <param name="Files">What this Uncloud has of it.</param>
/// <param name="WaitingForRoom">Files from your computers that can't arrive until this Uncloud has room.</param>
/// <param name="Computers">
/// How far each of your computers has got with it. This Uncloud having everything says nothing
/// about whether they do.
/// </param>
public sealed record SyncFolder(
    string Id,
    string Path,
    string Label,
    IReadOnlyList<string> DeviceIds,
    string? State,
    string? Error,
    int Files,
    long Bytes,
    int WaitingForRoom = 0,
    IReadOnlyList<SyncProgress>? Computers = null);

/// <summary>How far one of your computers has got with a folder.</summary>
/// <param name="State">See <see cref="SyncthingProgress.State"/>.</param>
/// <param name="NeedDeletes">See <see cref="SyncthingProgress.NeedDeletes"/>.</param>
public sealed record SyncProgress(string DeviceId, string State, double Completion, long NeedBytes, int NeedItems, int NeedDeletes = 0);

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
    /// <summary>How far another device has got with a folder; null when Syncthing can't say.</summary>
    Task<SyncthingProgress?> ProgressAsync(string folderId, string deviceId, CancellationToken cancellationToken);
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
    /// <summary>Stops a folder, or starts it again, without forgetting what it has.</summary>
    Task PauseFolderAsync(string id, bool paused, CancellationToken cancellationToken);
    Task KeepVersionsAsync(string id, CancellationToken cancellationToken);
    /// <summary>
    /// Whether a folder this device offers is set up here without asking. Only ever turned off now:
    /// the Uncloud app puts the host's folders where the host has them, which Syncthing can't.
    /// </summary>
    Task AutoAcceptFromAsync(string deviceId, bool accept, CancellationToken cancellationToken);
}
