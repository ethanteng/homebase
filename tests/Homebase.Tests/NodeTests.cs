using Homebase.Core;
using Homebase.Core.Nodes;

namespace Homebase.Tests;

public sealed class NodeTests : IDisposable
{
    private const string Self = "AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH";
    private const string Peer = "ZZZZZZZ-YYYYYYY-XXXXXXX-WWWWWWW-VVVVVVV-UUUUUUU-TTTTTTT-SSSSSSS";

    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private string _root;
    private readonly FakeSyncthing _syncthing = new();
    private readonly LibraryService _library;
    private readonly NodeService _nodes;

    public NodeTests()
    {
        Directory.CreateDirectory(_temporary);
        _root = Directory.CreateDirectory(Path.Combine(_temporary, "Library")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "Shared"));
        _library = new LibraryService(new SettingsStore(Path.Combine(_temporary, "Config")), new MetadataIndex());
        _library.SelectRootAsync(_root, CancellationToken.None).GetAwaiter().GetResult();
        // Selecting a root resolves symbolic links in its ancestors, which on macOS turns
        // /var into /private/var. Compare against what the library actually holds.
        _root = _library.State.RootPath!;
        _nodes = new NodeService(_library, _syncthing);
    }

    [Fact]
    public async Task Sharing_the_library_itself_is_refused()
    {
        // .homebase/index.db is a live SQLite database; copying it between machines corrupts it.
        await _nodes.PairAsync(Peer, "Laptop", CancellationToken.None);

        foreach (var path in new[] { "", "/", "  " })
        {
            var error = await Assert.ThrowsAsync<LibraryException>(
                () => _nodes.ShareAsync(path, [], CancellationToken.None));
            Assert.Equal("unsupported", error.Code);
        }
        Assert.Empty(_syncthing.Folders);
    }

    [Fact]
    public async Task Sharing_stays_inside_the_library_and_skips_hidden_folders()
    {
        await _nodes.PairAsync(Peer, "Laptop", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(_root, ".homebase", "nested"));

        foreach (var path in new[] { "../outside", "Shared/../../outside", ".homebase", ".homebase/nested" })
            await Assert.ThrowsAsync<LibraryException>(() => _nodes.ShareAsync(path, [], CancellationToken.None));
        Assert.Empty(_syncthing.Folders);
    }

    [Fact]
    public async Task A_shared_folder_is_registered_with_the_peer_and_ignores_metadata()
    {
        await _nodes.PairAsync(Peer, "Laptop", CancellationToken.None);

        var folder = await _nodes.ShareAsync("Shared", [], CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "Shared"), _syncthing.Folders[folder.Id].Path);
        Assert.Equal([Peer], _syncthing.Folders[folder.Id].Devices);
        Assert.Contains(".homebase", _syncthing.Ignores[folder.Id]);
        await Assert.ThrowsAsync<LibraryException>(() => _nodes.ShareAsync("Shared", [], CancellationToken.None));
    }

    [Fact]
    public async Task Sharing_needs_a_folder_that_exists_and_a_computer_to_share_it_with()
    {
        Assert.Equal("no_devices",
            (await Assert.ThrowsAsync<LibraryException>(() => _nodes.ShareAsync("Shared", [], CancellationToken.None))).Code);

        await _nodes.PairAsync(Peer, "Laptop", CancellationToken.None);
        Assert.Equal("not_found",
            (await Assert.ThrowsAsync<LibraryException>(() => _nodes.ShareAsync("Missing", [], CancellationToken.None))).Code);
        Assert.Equal("invalid_device",
            (await Assert.ThrowsAsync<LibraryException>(() => _nodes.ShareAsync("Shared", [Self], CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Pairing_checks_the_device_id_before_trusting_it()
    {
        foreach (var id in new[] { "", "not-a-device-id", "AAAAAAA-BBBBBBB", Self.Replace('A', '1') })
            Assert.Equal("invalid_device",
                (await Assert.ThrowsAsync<LibraryException>(() => _nodes.PairAsync(id, null, CancellationToken.None))).Code);

        // This computer's own ID is a mistake worth naming rather than a device to pair with.
        Assert.Equal("invalid_device",
            (await Assert.ThrowsAsync<LibraryException>(() => _nodes.PairAsync(Self, null, CancellationToken.None))).Code);

        await _nodes.PairAsync(Peer.ToLowerInvariant(), null, CancellationToken.None);
        Assert.Equal(Peer, Assert.Single(_syncthing.Devices).DeviceId);
        await Assert.ThrowsAsync<LibraryException>(() => _nodes.PairAsync(Peer, null, CancellationToken.None));
    }

    [Fact]
    public async Task Without_syncthing_the_panel_explains_itself_instead_of_failing()
    {
        _syncthing.Down = "Syncthing isn’t installed.";

        var status = await _nodes.StatusAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Equal("Syncthing isn’t installed.", status.Detail);
        Assert.Equal("unsupported",
            (await Assert.ThrowsAsync<LibraryException>(() => _nodes.PairAsync(Peer, null, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task A_folder_another_computer_offers_can_be_taken_up_here()
    {
        // Sharing from one side does not configure the other: Syncthing offers and waits.
        await _nodes.PairAsync(Peer, "Laptop", CancellationToken.None);
        _syncthing.Offers.Add(new PendingFolder("offered-id", "Shared", Peer, "Laptop"));

        var folder = await _nodes.AcceptAsync("offered-id", null, CancellationToken.None);

        Assert.Equal("offered-id", folder.Id);
        Assert.Equal(Path.Combine(_root, "Shared"), _syncthing.Folders["offered-id"].Path);
        Assert.Equal([Peer], _syncthing.Folders["offered-id"].Devices);
        Assert.Contains(".homebase", _syncthing.Ignores["offered-id"]);
        Assert.DoesNotContain(_syncthing.Ignores["offered-id"], pattern => pattern.StartsWith("(?d)"));
    }

    [Fact]
    public async Task An_offer_is_created_where_you_say_and_never_outside_the_library()
    {
        await _nodes.PairAsync(Peer, "Laptop", CancellationToken.None);
        _syncthing.Offers.Add(new PendingFolder("offered-id", "../escape", Peer, "Laptop"));

        // The offered label comes from the other computer, so it gets this library's rules too.
        await Assert.ThrowsAsync<LibraryException>(
            () => _nodes.AcceptAsync("offered-id", null, CancellationToken.None));
        Assert.Equal("not_found",
            (await Assert.ThrowsAsync<LibraryException>(
                () => _nodes.AcceptAsync("no-such-offer", "Shared", CancellationToken.None))).Code);

        // A folder that doesn't exist yet is created, since accepting means expecting files.
        var folder = await _nodes.AcceptAsync("offered-id", "Inbox/Laptop", CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_root, "Inbox", "Laptop")));
        Assert.Equal(Path.Combine(_root, "Inbox", "Laptop"), _syncthing.Folders[folder.Id].Path);
    }

    [Fact]
    public async Task A_library_reached_through_a_symlink_shares_the_path_it_resolves_to()
    {
        // Selecting a root resolves links in its ancestors, so the shared path is not the string
        // that was typed. On macOS this is every temporary folder, because /var links to /private/var.
        var real = Directory.CreateDirectory(Path.Combine(_temporary, "Real", "Shared")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(_temporary, "Link"), Path.Combine(_temporary, "Real"));

        using var library = new LibraryService(
            new SettingsStore(Path.Combine(_temporary, "LinkedConfig")), new MetadataIndex());
        await library.SelectRootAsync(Path.Combine(_temporary, "Link"), CancellationToken.None);
        var syncthing = new FakeSyncthing();
        var nodes = new NodeService(library, syncthing);
        await nodes.PairAsync(Peer, "Laptop", CancellationToken.None);

        var folder = await nodes.ShareAsync("Shared", [], CancellationToken.None);

        Assert.Equal(real, syncthing.Folders[folder.Id].Path);
        Assert.DoesNotContain("Link", syncthing.Folders[folder.Id].Path);
    }

    [Fact]
    public void A_library_path_always_maps_to_the_same_folder_id()
    {
        Assert.Equal(NodeService.FolderId("Shared/Notes"), NodeService.FolderId("shared/notes"));
        Assert.NotEqual(NodeService.FolderId("Shared/Notes"), NodeService.FolderId("Shared/Other"));
        Assert.StartsWith("homebase-", NodeService.FolderId("Shared/Notes"));
    }

    public void Dispose()
    {
        _library.Dispose();
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }

    private sealed class FakeSyncthing : ISyncthingApi
    {
        public List<NodeDevice> Devices { get; } = [];
        public Dictionary<string, (string Path, IReadOnlyList<string> Devices)> Folders { get; } = [];
        public Dictionary<string, IReadOnlyList<string>> Ignores { get; } = [];
        public string? Down { get; set; }

        public bool IsAvailable => Down is null;
        public string? Unavailable => Down;

        public Task<string> DeviceIdAsync(CancellationToken cancellationToken) => Task.FromResult(Self);
        public Task<IReadOnlyList<NodeDevice>> DevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NodeDevice>>(Devices.ToArray());
        public Task AddDeviceAsync(string deviceId, string name, CancellationToken cancellationToken)
        {
            Devices.Add(new NodeDevice(deviceId, name, false, null));
            return Task.CompletedTask;
        }
        public List<PendingFolder> Offers { get; } = [];
        public Task<IReadOnlyList<PendingFolder>> OffersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PendingFolder>>(Offers.ToArray());
        public Task<IReadOnlyList<SharedFolder>> FoldersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SharedFolder>>(Folders
                .Select(entry => new SharedFolder(entry.Key, entry.Key, entry.Value.Path, entry.Value.Devices, "idle", 0, 0))
                .ToArray());
        public Task AddFolderAsync(string id, string label, string path, IReadOnlyList<string> deviceIds, CancellationToken cancellationToken)
        {
            Folders[id] = (path, deviceIds);
            return Task.CompletedTask;
        }
        public Task IgnoreAsync(string folderId, IReadOnlyList<string> patterns, CancellationToken cancellationToken)
        {
            Ignores[folderId] = patterns;
            return Task.CompletedTask;
        }
    }
}
