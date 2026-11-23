using Homebase.Core;
using Homebase.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Uncloud.Desktop;

/// <summary>How this computer's copy stands, in words for the menu bar.</summary>
public sealed record ComputerStatus(bool Connected, bool Busy, string Summary, IReadOnlyList<SyncthingFolder> Folders);

/// <summary>
/// Where the folders the host shares with this computer are in the account's folder, as the host
/// says when asked; null when it can't say, being from before it could.
/// </summary>
/// <exception cref="PairingException">The host couldn't be asked just now.</exception>
public delegate Task<IReadOnlyList<PairedFolder>?> WhereFoldersGo(CancellationToken cancellationToken);

/// <summary>
/// This computer's side of syncing: its own Syncthing, set up to keep ~/Uncloud in step with the
/// one host it paired with and nobody else.
/// </summary>
public sealed class ComputerSync(ISyncthingApi syncthing, DesktopPaths paths, ILogger? logger = null)
{
    /// <summary>
    /// How long the host has to go on saying it no longer shares a folder before this computer stops
    /// keeping it. Just after pairing the host can say so for a moment about a folder it is about to
    /// share, and stopping would only start the folder over.
    /// </summary>
    public static readonly TimeSpan StoppedFor = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How soon the host is asked again where a folder goes when one is waiting to be set up or
    /// moved, and its last answer didn't say.
    /// </summary>
    public static readonly TimeSpan AskAtMostEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How soon it's asked again only to check folders already kept here that it hasn't placed since
    /// the app opened: ones set up by name under a host that couldn't say then, which a host that
    /// can say now has somewhere else. Nothing waits on that, so a host that still can't say, or
    /// can't be reached, isn't asked every few seconds.
    /// </summary>
    public static readonly TimeSpan ConfirmAtMostEvery = TimeSpan.FromHours(1);

    /// <summary>The clock, replaceable so waiting can be tested without waiting.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    // A folder's place never changes on the host: its ID is made from it. So an answer is kept.
    private readonly Dictionary<string, PairedFolder> _places = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _notShared = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _askedAt = DateTimeOffset.MinValue;
    private bool _hostCantSay;
    private bool _autoAcceptOff;

    /// <summary>
    /// Sets this computer up for what pairing returned: the host as the only device it talks to,
    /// and the host's folders under ~/Uncloud. Folders the host shares later are set up by
    /// <see cref="KeepInStepAsync"/>. Running it again changes nothing that is already right.
    /// </summary>
    public async Task ApplyAsync(PairingResult pairing, CancellationToken cancellationToken)
    {
        // Not alongside a look already under way, which could set the same folder up as well.
        await _gate.WaitAsync(cancellationToken);
        try { await ApplyGatedAsync(pairing, cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task ApplyGatedAsync(PairingResult pairing, CancellationToken cancellationToken)
    {
        // Pairing is a host saying where its folders are. What a previous one couldn't say, and
        // whether this process already turned auto-accept off for that one, doesn't decide the next.
        _hostCantSay = false;
        _askedAt = DateTimeOffset.MinValue;
        _autoAcceptOff = false;
        _notShared.Clear();

        Directory.CreateDirectory(paths.Files);
        if ((await syncthing.DevicesAsync(cancellationToken)).All(device => device.DeviceId != pairing.HostDeviceId))
            await syncthing.AddDeviceAsync(pairing.HostDeviceId, "Uncloud", cancellationToken);
        await StopAcceptingByItselfAsync(pairing.HostDeviceId, cancellationToken);

        var existing = (await syncthing.FoldersAsync(cancellationToken)).Select(folder => folder.Id).ToHashSet();
        foreach (var folder in pairing.Folders)
        {
            _places[folder.Id] = folder;
            if (existing.Contains(folder.Id)) continue;
            var path = FolderPath(folder);
            Directory.CreateDirectory(path);
            SyncService.GuardMetadata(path);
            await syncthing.AddFolderAsync(folder.Id, folder.Label, path, [pairing.HostDeviceId], null, cancellationToken);
        }
    }

    /// <summary>
    /// Keeps the folders here the ones the host shares with this computer, each at the same place
    /// under ~/Uncloud as on the host. Run whenever the app looks: a folder synced later from
    /// Computers is set up here; one stopped there stops being kept here, its files left where they
    /// are; and one Syncthing put under its ID, as it did by itself when a folder of the same name
    /// was in the way — or one put under its name by a host that couldn't say where it went — is
    /// moved to where it belongs once nothing is there.
    /// </summary>
    /// <param name="whereTheyGo">Asks the host, only when there is something it hasn't placed yet.</param>
    public async Task KeepInStepAsync(string hostDeviceId, WhereFoldersGo whereTheyGo, CancellationToken cancellationToken)
    {
        if (!syncthing.IsAvailable || !await _gate.WaitAsync(0, cancellationToken)) return;
        try
        {
            await StopAcceptingByItselfAsync(hostDeviceId, cancellationToken);
            var folders = await syncthing.FoldersAsync(cancellationToken);
            // This app pauses a folder only while moving it, so one of the host's still paused is a
            // move whose end didn't reach Syncthing, or the app closing part-way. Nothing else would
            // ever start it again: Resume in the menu is for the host, not a folder.
            foreach (var folder in folders.Where(folder => folder.Paused && folder.DeviceIds.Contains(hostDeviceId)))
            {
                await syncthing.PauseFolderAsync(folder.Id, false, cancellationToken);
                logger?.LogInformation("Started {Folder} again, which was left stopped", folder.Id);
            }

            var host = (await syncthing.DevicesAsync(cancellationToken)).FirstOrDefault(device => device.DeviceId == hostDeviceId);
            // What the host shares is only known while it's connected. Not sharing has to be seen
            // throughout, so a gap starts the wait again.
            if (host is not { Connected: true, Paused: false })
            {
                _notShared.Clear();
                return;
            }

            var kept = new List<SyncthingFolder>();
            foreach (var folder in folders)
            {
                if (folder.DeviceIds.Contains(hostDeviceId) && await StoppedOnHostAsync(folder.Id, hostDeviceId, cancellationToken))
                {
                    await syncthing.RemoveFolderAsync(folder.Id, cancellationToken);
                    _notShared.Remove(folder.Id);
                    logger?.LogInformation("Stopped keeping {Folder}, which Uncloud no longer syncs with this computer", folder.Id);
                    continue;
                }
                kept.Add(folder);
            }

            var offers = (await syncthing.OffersAsync(cancellationToken))
                .Where(offer => offer.DeviceId == hostDeviceId && kept.All(folder => folder.Id != offer.FolderId))
                .DistinctBy(offer => offer.FolderId)
                .ToArray();
            // Only where Syncthing would have put a folder by itself, or this app by name: anywhere
            // else is where the host said, and its place never changes.
            var movable = kept.Where(folder => folder.DeviceIds.Contains(hostDeviceId)
                && (Under(folder, folder.Id) || Under(folder, folder.Label))).ToArray();
            // A folder offered or under its ID is waiting on the answer; one only to be checked isn't.
            var waiting = offers.Select(offer => offer.FolderId)
                .Concat(movable.Where(folder => Under(folder, folder.Id)).Select(folder => folder.Id))
                .Any(id => !_places.ContainsKey(id));
            if (waiting || movable.Any(folder => !_places.ContainsKey(folder.Id)))
                await AskAsync(waiting ? AskAtMostEvery : ConfirmAtMostEvery, whereTheyGo, cancellationToken);

            foreach (var folder in movable)
            {
                if (PlaceOf(folder.Id, folder.Label) is not { } place) continue;
                if (await MoveAsync(folder, place, kept, cancellationToken) is { } moved)
                    kept[kept.IndexOf(folder)] = moved;
            }
            foreach (var offer in offers)
            {
                if (PlaceOf(offer.FolderId, offer.Label) is not { } place) continue;
                if (await AcceptAsync(place, hostDeviceId, kept, cancellationToken) is { } accepted) kept.Add(accepted);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Folders the host shares are set up here by this app, at the place the host has them.
    /// Syncthing's own way puts one under its name, and under its ID when that name is taken.
    /// </summary>
    private async Task StopAcceptingByItselfAsync(string hostDeviceId, CancellationToken cancellationToken)
    {
        if (_autoAcceptOff) return;
        await syncthing.AutoAcceptFromAsync(hostDeviceId, false, cancellationToken);
        _autoAcceptOff = true;
    }

    private async Task<bool> StoppedOnHostAsync(string folderId, string hostDeviceId, CancellationToken cancellationToken)
    {
        if (await syncthing.ProgressAsync(folderId, hostDeviceId, cancellationToken) is not { State: SyncthingProgress.NotSharing })
        {
            _notShared.Remove(folderId);
            return false;
        }
        if (!_notShared.TryGetValue(folderId, out var since))
        {
            _notShared[folderId] = Now();
            return false;
        }
        return Now() - since >= StoppedFor;
    }

    /// <summary>Asks the host where its folders go, unless it was asked within <paramref name="atMostEvery"/>.</summary>
    private async Task AskAsync(TimeSpan atMostEvery, WhereFoldersGo whereTheyGo, CancellationToken cancellationToken)
    {
        if (Now() - _askedAt < atMostEvery) return;
        _askedAt = Now();
        IReadOnlyList<PairedFolder>? answer;
        try { answer = await whereTheyGo(cancellationToken); }
        catch (PairingException error)
        {
            logger?.LogWarning("Couldn’t ask Uncloud where its folders go: {Reason}", error.Message);
            return;
        }
        _hostCantSay = answer is null;
        foreach (var place in answer ?? []) _places[place.Id] = place;
    }

    /// <summary>
    /// Where a folder belongs, as the host last said. A host from before it could say has each
    /// folder go under its name, as Syncthing would have put it; null until the host has answered.
    /// </summary>
    private PairedFolder? PlaceOf(string folderId, string label) =>
        _places.TryGetValue(folderId, out var known) ? known
        : _hostCantSay ? new PairedFolder(folderId, label, label)
        : null;

    /// <summary>Whether a folder is at ~/Uncloud/<paramref name="name"/>.</summary>
    private bool Under(SyncthingFolder folder, string name) => SamePlace(folder.Path, Path.Combine(paths.Files, name));

    private async Task<SyncthingFolder?> AcceptAsync(PairedFolder place, string hostDeviceId, List<SyncthingFolder> kept, CancellationToken cancellationToken)
    {
        if (TargetFor(place, kept, except: null) is not { } path) return null;
        Directory.CreateDirectory(path);
        SyncService.GuardMetadata(path);
        await syncthing.AddFolderAsync(place.Id, place.Label, path, [hostDeviceId], null, cancellationToken);
        logger?.LogInformation("Now keeping {Folder} at {Path}", place.Id, path);
        return new SyncthingFolder(place.Id, place.Label, path, [hostDeviceId], null, null, 0, 0, true);
    }

    /// <summary>
    /// Moves a folder to where it belongs, stopped while it goes so Syncthing neither writes into it
    /// nor finds it missing. Only into a place with nothing in it yet: whatever is there may not be
    /// the same files, and isn't merged into them.
    /// </summary>
    private async Task<SyncthingFolder?> MoveAsync(SyncthingFolder folder, PairedFolder place, List<SyncthingFolder> kept, CancellationToken cancellationToken)
    {
        if (TargetFor(place, kept, except: folder.Id) is not { } target || SamePlace(target, folder.Path)
            || !Vacant(target) || !Directory.Exists(folder.Path))
            return null;

        await syncthing.PauseFolderAsync(folder.Id, true, cancellationToken);
        // Once stopped, it is put back or moved, whatever the app is asked meanwhile.
        var uncancelled = CancellationToken.None;
        var moved = false;
        try
        {
            Clear(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(folder.Path, target);
            moved = true;
            await syncthing.SetFolderPathAsync(folder.Id, target, uncancelled);
            logger?.LogInformation("Moved {Folder} to {Path}", folder.Id, target);
            return folder with { Path = target };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or LibraryException)
        {
            logger?.LogWarning(error, "Couldn’t move {Folder} to {Path}", folder.Id, target);
            if (moved)
                try { Directory.Move(target, folder.Path); }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                    logger?.LogWarning(again, "Couldn’t put {Folder} back at {Path}", folder.Id, folder.Path);
                }
            return null;
        }
        finally
        {
            try { await syncthing.PauseFolderAsync(folder.Id, false, uncancelled); }
            // Left for the next look, which starts any of the host's folders it finds stopped.
            catch (Exception error) when (error is LibraryException or HttpRequestException or TaskCanceledException)
            {
                logger?.LogWarning(error, "Couldn’t start {Folder} again after moving it; trying again on the next look", folder.Id);
            }
        }
    }

    private const string FinderMetadata = ".DS_Store";

    /// <summary>
    /// Nothing there, or a folder with nothing in it but what Finder writes on opening it, which
    /// isn't synced anyway: as good as nothing to move a folder into.
    /// </summary>
    private static bool Vacant(string path) =>
        !File.Exists(path) && (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path)
            .All(entry => Path.GetFileName(entry) == FinderMetadata && File.Exists(entry)));

    /// <summary>
    /// Makes way for a folder at a <see cref="Vacant"/> place. One by one, and the folder itself
    /// without recursing, so anything put there since it was looked at stops the move instead.
    /// </summary>
    private static void Clear(string path)
    {
        if (!Directory.Exists(path)) return;
        var finder = Path.Combine(path, FinderMetadata);
        if (File.Exists(finder)) File.Delete(finder);
        Directory.Delete(path, recursive: false);
    }

    /// <summary>
    /// Where a folder goes here, unless that would put it inside another folder kept here or around
    /// one — two Syncthing folders over the same files. Then it waits for that one to go.
    /// </summary>
    private string? TargetFor(PairedFolder place, IEnumerable<SyncthingFolder> kept, string? except)
    {
        string target;
        try { target = FolderPath(place); }
        catch (InvalidDataException error)
        {
            logger?.LogWarning("{Reason}", error.Message);
            return null;
        }
        return kept.Any(folder => folder.Id != except && Overlap(folder.Path, target)) ? null : target;
    }

    /// <summary>
    /// All of the account at ~/Uncloud itself; a single folder of it at the same place under
    /// ~/Uncloud as on the host, so Work/Documents and Personal/Documents stay two folders. The
    /// path comes from the host, so it is held to staying inside ~/Uncloud all the same.
    /// </summary>
    public string FolderPath(PairedFolder folder)
    {
        var parts = folder.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".." || part.Contains('\\') || part.Contains('\0')))
            throw new InvalidDataException($"Uncloud offered a folder this computer won’t keep: “{folder.Path}”.");
        return parts.Length == 0 ? paths.Files : Path.Combine([paths.Files, .. parts]);
    }

    // Macs don't tell Desktop from desktop, so neither does this.
    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SamePlace(string first, string second) =>
        Trim(first).Equals(Trim(second), StringComparison.OrdinalIgnoreCase);

    private static bool Overlap(string first, string second)
    {
        string a = Trim(first), b = Trim(second);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

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
        // After any look already under way: a folder it sets up after these are listed would
        // outlive the disconnect, and pairing again leaves a folder it already has as it is.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var folder in await syncthing.FoldersAsync(cancellationToken))
                await syncthing.RemoveFolderAsync(folder.Id, cancellationToken);
            if ((await syncthing.DevicesAsync(cancellationToken)).Any(device => device.DeviceId == hostDeviceId))
                await syncthing.RemoveDeviceAsync(hostDeviceId, cancellationToken);
            _places.Clear();
            _notShared.Clear();
            _hostCantSay = false;
            _askedAt = DateTimeOffset.MinValue;
            _autoAcceptOff = false;
        }
        finally
        {
            _gate.Release();
        }
    }
}
