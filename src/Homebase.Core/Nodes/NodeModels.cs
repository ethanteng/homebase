namespace Homebase.Core.Nodes;

public sealed record NodeDevice(string DeviceId, string Name, bool Connected, string? Address);

public sealed record SharedFolder(
    string Id,
    string Label,
    string LocalPath,
    IReadOnlyList<string> DeviceIds,
    string? State,
    int Files,
    long Bytes);

/// <summary>A folder another computer has offered to share, waiting to be accepted here.</summary>
public sealed record PendingFolder(string Id, string Label, string OfferedBy, string OfferedByName);

public sealed record NodeStatus(
    bool Available,
    string? Detail,
    string? DeviceId,
    IReadOnlyList<NodeDevice> Devices,
    IReadOnlyList<SharedFolder> Folders,
    IReadOnlyList<PendingFolder> Offers);

/// <summary>
/// The seam between Homebase and the Syncthing instance it supervises, so pairing and sharing
/// logic is testable without the binary running.
/// </summary>
public interface ISyncthingApi
{
    bool IsAvailable { get; }
    string? Unavailable { get; }
    Task<string> DeviceIdAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<NodeDevice>> DevicesAsync(CancellationToken cancellationToken);
    Task AddDeviceAsync(string deviceId, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<SharedFolder>> FoldersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingFolder>> OffersAsync(CancellationToken cancellationToken);
    Task AddFolderAsync(string id, string label, string path, IReadOnlyList<string> deviceIds, CancellationToken cancellationToken);
    Task IgnoreAsync(string folderId, IReadOnlyList<string> patterns, CancellationToken cancellationToken);
}
