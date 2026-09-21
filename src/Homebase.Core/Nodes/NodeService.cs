using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Homebase.Core.Nodes;

/// <summary>
/// Pairs other machines with this Homebase and shares folders from the library with them.
/// The peer protocol is Syncthing's; what lives here are Homebase's own rules about which
/// paths may be shared at all.
/// </summary>
public sealed partial class NodeService(LibraryService library, ISyncthingApi syncthing)
{
    // Syncthing device IDs are eight dash-separated groups; the checksum is Syncthing's to verify.
    [GeneratedRegex("^[A-Z2-7]{7}(-[A-Z2-7]{7}){7}$")]
    private static partial Regex DeviceIdPattern { get; }

    public async Task<NodeStatus> StatusAsync(CancellationToken cancellationToken)
    {
        if (!syncthing.IsAvailable)
            return new NodeStatus(false, syncthing.Unavailable, null, [], [], []);
        return new NodeStatus(true, null,
            await syncthing.DeviceIdAsync(cancellationToken),
            await syncthing.DevicesAsync(cancellationToken),
            await syncthing.FoldersAsync(cancellationToken),
            await syncthing.OffersAsync(cancellationToken));
    }

    public async Task PairAsync(string deviceId, string? name, CancellationToken cancellationToken)
    {
        Require();
        var trimmed = (deviceId ?? "").Trim().ToUpperInvariant();
        if (!DeviceIdPattern.IsMatch(trimmed))
            throw new LibraryException(
                "That doesn’t look like a device ID. Copy the whole ID from the other computer.", "invalid_device");
        if (trimmed == await syncthing.DeviceIdAsync(cancellationToken))
            throw new LibraryException("That’s this computer’s own ID. Use the other computer’s.", "invalid_device");

        var known = await syncthing.DevicesAsync(cancellationToken);
        if (known.Any(device => device.DeviceId == trimmed))
            throw new LibraryException("That computer is already paired with Uncloud.");

        await syncthing.AddDeviceAsync(trimmed,
            string.IsNullOrWhiteSpace(name) ? "Another computer" : name.Trim(), cancellationToken);
    }

    /// <summary>Shares one folder inside the library with paired devices.</summary>
    public async Task<SharedFolder> ShareAsync(string relativePath, IReadOnlyList<string> deviceIds, CancellationToken cancellationToken)
    {
        Require();
        var root = library.State.RootPath
            ?? throw new LibraryException("Choose your Uncloud folder first.", "not_configured");

        var normalized = (relativePath ?? "").Trim().Trim('/');
        // The library root holds .homebase/index.db, a live SQLite database. Copying that between
        // machines corrupts it, so only folders inside the library can ever be shared.
        if (normalized.Length == 0)
            throw new LibraryException(
                "Share a folder inside your Uncloud folder, not the whole thing.", "unsupported");

        var fullPath = PathPolicy.Resolve(root, normalized);
        if (!Directory.Exists(fullPath))
            throw new LibraryException("That folder isn’t in your Uncloud folder.", "not_found");

        var paired = await syncthing.DevicesAsync(cancellationToken);
        var targets = deviceIds is { Count: > 0 }
            ? deviceIds.Select(id => id.Trim().ToUpperInvariant()).ToArray()
            : paired.Select(device => device.DeviceId).ToArray();
        if (targets.Length == 0)
            throw new LibraryException("Pair another computer before sharing a folder.", "no_devices");
        foreach (var target in targets)
            if (!paired.Any(device => device.DeviceId == target))
                throw new LibraryException("That computer isn’t paired with Uncloud yet.", "invalid_device");

        var folders = await syncthing.FoldersAsync(cancellationToken);
        var id = FolderId(normalized);
        if (folders.Any(folder => folder.Id == id))
            throw new LibraryException("That folder is already shared.");

        await syncthing.AddFolderAsync(id, normalized, fullPath, targets, cancellationToken);
        // Belt and braces: even a folder that somehow contains metadata never carries it across.
        // No (?d) prefix — that marks a file as one Syncthing may delete, which is the opposite
        // of what a protected metadata folder wants.
        await syncthing.IgnoreAsync(id, ["/.homebase", ".homebase"], cancellationToken);

        return (await syncthing.FoldersAsync(cancellationToken)).FirstOrDefault(folder => folder.Id == id)
            ?? new SharedFolder(id, normalized, fullPath, targets, null, 0, 0);
    }

    /// <summary>
    /// Takes up a folder another computer has offered. Sharing a folder does not configure it on
    /// the far side — Syncthing offers it and waits — so without this step the second computer
    /// stays unconfigured and nothing moves.
    /// </summary>
    public async Task<SharedFolder> AcceptAsync(string folderId, string? relativePath, CancellationToken cancellationToken)
    {
        Require();
        var root = library.State.RootPath
            ?? throw new LibraryException("Choose your Uncloud folder first.", "not_configured");

        var offer = (await syncthing.OffersAsync(cancellationToken))
            .FirstOrDefault(pending => pending.Id == folderId)
            ?? throw new LibraryException("No computer is offering that folder.", "not_found");

        // The offered label is the sharer's path; it still has to satisfy this library's rules.
        var normalized = (relativePath ?? offer.Label).Trim().Trim('/');
        if (normalized.Length == 0)
            throw new LibraryException(
                "Choose a folder inside your Uncloud folder to keep this in.", "unsupported");

        var fullPath = PathPolicy.Resolve(root, normalized);
        PathPolicy.RejectLink(fullPath);
        Directory.CreateDirectory(fullPath);

        await syncthing.AddFolderAsync(folderId, normalized, fullPath, [offer.OfferedBy], cancellationToken);
        await syncthing.IgnoreAsync(folderId, ["/.homebase", ".homebase"], cancellationToken);

        return (await syncthing.FoldersAsync(cancellationToken)).FirstOrDefault(folder => folder.Id == folderId)
            ?? new SharedFolder(folderId, normalized, fullPath, [offer.OfferedBy], null, 0, 0);
    }

    /// <summary>A stable id per library path, so re-sharing the same folder is recognisable.</summary>
    public static string FolderId(string relativePath)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.ToLowerInvariant()));
        var slug = new string(relativePath.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        if (slug.Length > 24) slug = slug[..24].Trim('-');
        return $"homebase-{slug}-{Convert.ToHexString(digest)[..8].ToLowerInvariant()}";
    }

    private void Require()
    {
        if (!syncthing.IsAvailable)
            throw new LibraryException(
                syncthing.Unavailable ?? "Syncthing isn’t running, so Uncloud can’t reach other computers.", "unsupported");
    }
}
