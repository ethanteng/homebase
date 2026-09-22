using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Homebase.Core.Accounts;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Sync;

/// <summary>
/// Keeps folders in an account's space the same on that person's own computers, so a file added,
/// changed or deleted on a laptop is added, changed or deleted here too, and the other way round.
/// </summary>
/// <remarks>
/// The peer protocol is Syncthing's, and there is one Syncthing for the whole host with one
/// configuration that knows nothing about accounts. What makes it per-account is here: every
/// computer and every folder is recorded against the account that added it, every call is
/// answered from that record, and a folder can only ever be made inside the caller's own root.
/// </remarks>
public sealed partial class SyncService(
    ISyncthingApi syncthing,
    SyncOwnership ownership,
    HostService host,
    UserStore users,
    ILogger<SyncService> logger)
{
    public const string IgnoreFile = ".stignore";

    // Everything that changes Syncthing's configuration, one at a time, so two requests can't
    // both decide a folder is free and both add it.
    private readonly SemaphoreSlim _gate = new(1, 1);

    [GeneratedRegex("^[A-Z2-7]{56}$")]
    private static partial Regex DeviceIdPattern { get; }

    public async Task<SyncStatus> StatusAsync(string userId, CancellationToken cancellationToken)
    {
        var devices = ownership.Devices(userId);
        var folders = ownership.Folders(userId);
        if (!syncthing.IsAvailable)
            // What this account has set up is still worth showing when Syncthing isn't running.
            return new SyncStatus(false, syncthing.Unavailable, null,
                devices.Select(device => new SyncDevice(device.DeviceId, device.Name, false, null)).ToArray(),
                folders.Select(folder => new SyncFolder(folder.FolderId, folder.Path, Label(folder.Path),
                    [], null, null, 0, 0)).ToArray(),
                []);

        var mine = devices.Select(device => device.DeviceId).ToHashSet(StringComparer.Ordinal);
        var live = (await syncthing.DevicesAsync(cancellationToken)).ToDictionary(device => device.DeviceId);
        var liveFolders = (await syncthing.FoldersAsync(cancellationToken)).ToDictionary(folder => folder.Id);
        var offers = await syncthing.OffersAsync(cancellationToken);

        return new SyncStatus(true, null,
            await syncthing.DeviceIdAsync(cancellationToken),
            devices.Select(device => live.TryGetValue(device.DeviceId, out var state)
                ? new SyncDevice(device.DeviceId, device.Name, state.Connected, state.Address)
                : new SyncDevice(device.DeviceId, device.Name, false, null)).ToArray(),
            folders.Select(folder => liveFolders.TryGetValue(folder.FolderId, out var state)
                ? new SyncFolder(folder.FolderId, folder.Path, Label(folder.Path),
                    state.DeviceIds.Where(mine.Contains).ToArray(), state.State, state.Error, state.Files, state.Bytes)
                : new SyncFolder(folder.FolderId, folder.Path, Label(folder.Path), [], null, null, 0, 0)).ToArray(),
            // Only offers made by this account's own computers, for folders nobody here has yet.
            offers.Where(offer => mine.Contains(offer.DeviceId) && !liveFolders.ContainsKey(offer.FolderId))
                .Select(offer => new SyncOffer(offer.FolderId, offer.Label, offer.DeviceId,
                    devices.First(device => device.DeviceId == offer.DeviceId).Name))
                .ToArray());
    }

    /// <summary>Lets one of this person's computers connect to the host.</summary>
    public async Task PairAsync(string userId, string deviceId, string? name, CancellationToken cancellationToken)
    {
        Require();
        var id = NormalizeDeviceId(deviceId);
        if (id == await syncthing.DeviceIdAsync(cancellationToken))
            throw new LibraryException("That’s this Uncloud’s own ID. Use the one from your computer.", "invalid_device");
        var label = string.IsNullOrWhiteSpace(name) ? "My computer" : name.Trim();
        if (label.Length > 64) label = label[..64];

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (ownership.FindDevice(id) is { } owned)
                // Not saying whose: which computers other people have is theirs to know.
                throw new LibraryException(owned.UserId == userId
                    ? "That computer is already paired."
                    : "That computer is already paired with another account on this Uncloud.", "conflict");

            if (!ownership.ClaimDevice(userId, id, label))
                throw new LibraryException("That computer is already paired.", "conflict");
            try
            {
                var existing = (await syncthing.DevicesAsync(cancellationToken)).FirstOrDefault(device => device.DeviceId == id);
                if (existing is null) await syncthing.AddDeviceAsync(id, label, cancellationToken);
                else if (existing.Paused) await syncthing.PauseDeviceAsync(id, false, cancellationToken);
            }
            catch
            {
                ownership.ReleaseDevice(id);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops a computer syncing with the host. Nothing on either side is deleted.</summary>
    public async Task UnpairAsync(string userId, string deviceId, CancellationToken cancellationToken)
    {
        Require();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var device = OwnedDevice(userId, deviceId);
            var live = await syncthing.FoldersAsync(cancellationToken);
            foreach (var folder in ownership.Folders(userId))
                if (live.FirstOrDefault(entry => entry.Id == folder.FolderId) is { } state && state.DeviceIds.Contains(device.DeviceId))
                    await syncthing.SetFolderDevicesAsync(folder.FolderId,
                        state.DeviceIds.Where(id => id != device.DeviceId).ToArray(), cancellationToken);
            if ((await syncthing.DevicesAsync(cancellationToken)).Any(entry => entry.DeviceId == device.DeviceId))
                await syncthing.RemoveDeviceAsync(device.DeviceId, cancellationToken);
            ownership.ReleaseDevice(device.DeviceId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Offers a folder in this account's space to its computers. An empty path is the whole
    /// account folder: Uncloud's own index lives there, and is kept out by an ignore file written
    /// before Syncthing ever sees the folder.
    /// </summary>
    public async Task<SyncFolder> ShareAsync(string userId, string userRoot, string? relativePath, IReadOnlyList<string>? deviceIds, CancellationToken cancellationToken)
    {
        Require();
        var path = NormalizePath(relativePath);
        var fullPath = PathPolicy.Resolve(userRoot, path);
        if (!Directory.Exists(fullPath))
            throw new LibraryException("That folder isn’t in your Uncloud folder.", "not_found");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var mine = ownership.Devices(userId);
            string[] targets = deviceIds is { Count: > 0 }
                ? deviceIds.Select(id => OwnedDevice(userId, id).DeviceId).Distinct().ToArray()
                : mine.Select(device => device.DeviceId).ToArray();
            if (targets.Length == 0)
                throw new LibraryException("Pair one of your computers before syncing a folder.", "no_devices");

            RejectOverlap(userId, path);
            var id = FolderId(userId, path);
            if ((await syncthing.FoldersAsync(cancellationToken)).Any(folder => folder.Id == id))
                throw new LibraryException("That folder is already syncing.", "conflict");

            return await AddAsync(userId, id, path, fullPath, targets, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes up a folder one of this person's computers has offered, so what is on that computer
    /// arrives here and stays in step. Syncthing offers and waits; without this nothing moves.
    /// </summary>
    public async Task<SyncFolder> AcceptAsync(string userId, string userRoot, string folderId, string? relativePath, CancellationToken cancellationToken)
    {
        Require();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var mine = ownership.Devices(userId).Select(device => device.DeviceId).ToHashSet(StringComparer.Ordinal);
            // An offer from somebody else's computer is answered exactly as one that doesn't exist.
            var offers = (await syncthing.OffersAsync(cancellationToken))
                .Where(offer => offer.FolderId == folderId && mine.Contains(offer.DeviceId))
                .ToArray();
            if (offers.Length == 0)
                throw new LibraryException("None of your computers is offering that folder.", "not_found");
            if (ownership.FindFolder(folderId) is not null
                || (await syncthing.FoldersAsync(cancellationToken)).Any(folder => folder.Id == folderId))
                throw new LibraryException("That folder is already syncing.", "conflict");

            // The offered label comes from the other computer, so it gets this account's rules too.
            var path = NormalizePath(relativePath ?? offers[0].Label);
            if (path.Length == 0)
                throw new LibraryException("Choose a folder inside your Uncloud folder to keep this in.", "unsupported");
            RejectOverlap(userId, path);
            var fullPath = PathPolicy.Resolve(userRoot, path);
            Directory.CreateDirectory(fullPath);
            PathPolicy.RejectLink(fullPath);

            return await AddAsync(userId, folderId, path, fullPath,
                offers.Select(offer => offer.DeviceId).Distinct().ToArray(), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops syncing a folder. The files stay here, and stay on the computers too.</summary>
    public async Task StopAsync(string userId, string folderId, CancellationToken cancellationToken)
    {
        Require();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var folder = ownership.Folders(userId).FirstOrDefault(entry => entry.FolderId == folderId)
                ?? throw new LibraryException("You aren’t syncing that folder.", "not_found");
            if ((await syncthing.FoldersAsync(cancellationToken)).Any(entry => entry.Id == folder.FolderId))
                await syncthing.RemoveFolderAsync(folder.FolderId, cancellationToken);
            ownership.ReleaseFolder(folder.FolderId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops or restarts every computer an account paired, when it is disabled or enabled. If
    /// Syncthing can't be reached now, <see cref="ReconcileAsync"/> does it when it can.
    /// </summary>
    public async Task SuspendAsync(string userId, bool suspended, CancellationToken cancellationToken)
    {
        var devices = ownership.Devices(userId);
        if (devices.Count == 0 || !syncthing.IsAvailable) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var live = (await syncthing.DevicesAsync(cancellationToken)).ToDictionary(device => device.DeviceId);
            foreach (var device in devices)
                if (live.TryGetValue(device.DeviceId, out var state) && state.Paused != suspended)
                    await syncthing.PauseDeviceAsync(device.DeviceId, suspended, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes an account's syncing along with the account. The account's folder stays, as it
    /// always does, but nothing may carry on writing into it afterwards — so if Syncthing can't be
    /// reached to be told, the account isn't deleted.
    /// </summary>
    /// <remarks>
    /// Syncthing is cleaned up first and the account deleted last, because deleting the account
    /// cascades away the records that say what to clean up. Each record goes only once its
    /// computer or folder is gone from Syncthing, so a failure part-way leaves an account whose
    /// remaining records are exactly what is left to do, and deleting it again finishes the job.
    /// </remarks>
    public async Task ForgetAsync(string userId, Action deleteAccount, CancellationToken cancellationToken)
    {
        var devices = ownership.Devices(userId);
        var folders = ownership.Folders(userId);
        if ((devices.Count > 0 || folders.Count > 0) && !syncthing.IsAvailable)
            throw new LibraryException(
                "This account still syncs with its computers, and Uncloud can’t reach Syncthing to stop that. Try again once Syncthing is running.",
                "sync_unavailable");

        // An account that won't be deleted (the last administrator, say) keeps its computers.
        users.RequireDeletable(userId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Once started, a closed browser tab is no reason to stop half-way.
            var uncancelled = CancellationToken.None;
            if (devices.Count > 0 || folders.Count > 0)
            {
                var liveFolders = (await syncthing.FoldersAsync(uncancelled)).Select(folder => folder.Id).ToHashSet();
                foreach (var folder in folders)
                {
                    if (liveFolders.Contains(folder.FolderId))
                        await syncthing.RemoveFolderAsync(folder.FolderId, uncancelled);
                    ownership.ReleaseFolder(folder.FolderId);
                }
                var liveDevices = (await syncthing.DevicesAsync(uncancelled)).Select(device => device.DeviceId).ToHashSet();
                foreach (var device in devices)
                {
                    if (liveDevices.Contains(device.DeviceId))
                        await syncthing.RemoveDeviceAsync(device.DeviceId, uncancelled);
                    ownership.ReleaseDevice(device.DeviceId);
                }
            }
            deleteAccount();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Brings Syncthing's configuration back in line with what Uncloud recorded: run when
    /// Syncthing comes up and when the host's folder moves. Folders follow the host's folder,
    /// disabled accounts' computers stay paused, every folder keeps versions, and folders set up
    /// before syncing was per-account are given to the account whose folder they are in.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!syncthing.IsAvailable || host.RootPath is not { } hostRoot) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var usersDirectory = Path.Combine(hostRoot, UserPaths.UsersDirectory);
            var liveFolders = await syncthing.FoldersAsync(cancellationToken);
            var liveDevices = (await syncthing.DevicesAsync(cancellationToken)).ToDictionary(device => device.DeviceId);

            foreach (var folder in liveFolders.Where(folder => ownership.FindFolder(folder.Id) is null))
            {
                var relative = Path.GetRelativePath(usersDirectory, folder.Path);
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) continue;
                var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                if (users.Find(parts[0]) is null)
                {
                    // Syncing into the folder of an account that no longer exists: whatever left
                    // it there, nobody may keep writing into it.
                    logger.LogWarning("Stopped synced folder {Folder}, which belonged to a deleted account", folder.Id);
                    await syncthing.RemoveFolderAsync(folder.Id, cancellationToken);
                    continue;
                }
                if (parts.Length < 2) continue;
                var owner = parts[0];
                if (!ownership.ClaimFolder(owner, folder.Id, string.Join('/', parts[1..]))) continue;
                logger.LogInformation("Gave synced folder {Folder} to the account whose folder it is in", folder.Id);
                foreach (var deviceId in folder.DeviceIds)
                    if (ownership.FindDevice(deviceId) is null && liveDevices.TryGetValue(deviceId, out var device))
                        ownership.ClaimDevice(owner, deviceId, device.Name);
            }

            foreach (var folder in ownership.Folders())
            {
                var state = liveFolders.FirstOrDefault(entry => entry.Id == folder.FolderId);
                if (state is null)
                {
                    logger.LogWarning("Synced folder {Folder} is no longer in Syncthing's configuration", folder.FolderId);
                    ownership.ReleaseFolder(folder.FolderId);
                    continue;
                }
                // Moving the host's folder moves where every account's folders are. If the new
                // one doesn't hold them, Syncthing finds no folder marker and stops rather than
                // treating an empty folder as everything having been deleted.
                var expected = Path.Combine([usersDirectory, folder.UserId, .. folder.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
                if (!string.Equals(state.Path, expected, StringComparison.Ordinal))
                    await syncthing.SetFolderPathAsync(folder.FolderId, expected, cancellationToken);
                if (!state.Versioned) await syncthing.KeepVersionsAsync(folder.FolderId, cancellationToken);
                if (Directory.Exists(expected)) GuardMetadata(expected);
            }

            foreach (var device in ownership.Devices())
            {
                var disabled = users.Find(device.UserId) is not { IsActive: true };
                if (!liveDevices.TryGetValue(device.DeviceId, out var state))
                {
                    if (!disabled) await syncthing.AddDeviceAsync(device.DeviceId, device.Name, cancellationToken);
                    continue;
                }
                if (state.Paused != disabled)
                    await syncthing.PauseDeviceAsync(device.DeviceId, disabled, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A stable id per account and path, so the same folder is recognisable again.</summary>
    public static string FolderId(string userId, string relativePath)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}\n{relativePath.ToLowerInvariant()}"));
        var slug = new string(Label(relativePath).ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        if (slug.Length > 24) slug = slug[..24].Trim('-');
        if (slug.Length == 0) slug = "folder";
        return $"uncloud-{slug}-{Convert.ToHexString(digest)[..12].ToLowerInvariant()}";
    }

    /// <summary>What the folder is called on the other computer, which is where it lands by default.</summary>
    public static string Label(string relativePath) =>
        relativePath.Length == 0 ? "Uncloud" : relativePath[(relativePath.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Syncthing shows IDs in eight dashed groups but accepts them without; people paste either.
    /// The checksum characters are Syncthing's to verify.
    /// </summary>
    public static string NormalizeDeviceId(string? deviceId)
    {
        var compact = new string((deviceId ?? "").Where(character => character is not ('-' or ' ')).ToArray())
            .Trim().ToUpperInvariant();
        if (!DeviceIdPattern.IsMatch(compact))
            throw new LibraryException(
                "That doesn’t look like a device ID. Copy the whole ID from Syncthing on your computer.", "invalid_device");
        return string.Join('-', Enumerable.Range(0, 8).Select(group => compact.Substring(group * 7, 7)));
    }

    private static string NormalizePath(string? relativePath) =>
        string.Join('/', (relativePath ?? "").Trim().Split('/', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// One folder synced inside another would be two Syncthing folders fighting over the same
    /// files, so a folder can't be synced inside one already synced, or around one.
    /// </summary>
    private void RejectOverlap(string userId, string path)
    {
        foreach (var existing in ownership.Folders(userId))
        {
            var a = existing.Path;
            if (a.Equals(path, StringComparison.OrdinalIgnoreCase))
                throw new LibraryException("That folder is already syncing.", "conflict");
            if (a.Length == 0 || path.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase))
                throw new LibraryException($"That folder is inside {Label(a)}, which already syncs.", "conflict");
            if (path.Length == 0 || a.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
                throw new LibraryException($"That folder holds {Label(a)}, which already syncs on its own. Stop syncing it first.", "conflict");
        }
    }

    private async Task<SyncFolder> AddAsync(string userId, string id, string path, string fullPath, string[] devices, CancellationToken cancellationToken)
    {
        // Written before Syncthing knows the folder exists, so its first scan already skips
        // Uncloud's index. Copying a live SQLite database between machines corrupts it.
        GuardMetadata(fullPath);
        if (!ownership.ClaimFolder(userId, id, path))
            throw new LibraryException("That folder is already syncing.", "conflict");
        try
        {
            await syncthing.AddFolderAsync(id, Label(path), fullPath, devices, cancellationToken);
        }
        catch
        {
            ownership.ReleaseFolder(id);
            throw;
        }
        return new SyncFolder(id, path, Label(path), devices, null, null, 0, 0);
    }

    /// <summary>
    /// Keeps Uncloud's own metadata out of a synced folder, adding to whatever ignore rules are
    /// already there rather than replacing them.
    /// </summary>
    public static void GuardMetadata(string folderPath)
    {
        var file = Path.Combine(folderPath, IgnoreFile);
        PathPolicy.RejectLink(file);
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        string[] required = [".homebase", "(?d).DS_Store"];
        var missing = required.Where(pattern => !lines.Contains(pattern)).ToArray();
        if (missing.Length == 0) return;
        if (lines.Count == 0) lines.Add("// Written by Uncloud. .homebase is its index and must never be synced.");
        lines.AddRange(missing);
        File.WriteAllLines(file, lines);
    }

    private SyncOwnership.Device OwnedDevice(string userId, string deviceId)
    {
        string id;
        try { id = NormalizeDeviceId(deviceId); }
        catch (LibraryException) { throw new LibraryException("That isn’t one of your computers.", "not_found"); }
        return ownership.FindDevice(id) is { } device && device.UserId == userId
            ? device
            : throw new LibraryException("That isn’t one of your computers.", "not_found");
    }

    private void Require()
    {
        if (!syncthing.IsAvailable)
            throw new LibraryException(
                syncthing.Unavailable ?? "Syncthing isn’t running, so Uncloud can’t reach your computers.", "sync_unavailable");
    }
}
