using System.Text;
using System.Text.Json;

namespace Homebase.Core.Sync;

/// <summary>Where the supervised Syncthing instance can be reached, once it is up.</summary>
public interface ISyncthingEndpoint
{
    bool IsReady { get; }
    string? Unavailable { get; }
    Uri? BaseAddress { get; }
    string? ApiKey { get; }
}

/// <summary>Syncthing's local REST API. Everything here talks to loopback only.</summary>
public sealed class SyncthingApi(HttpClient client, ISyncthingEndpoint endpoint) : ISyncthingApi
{
    // How long a replaced or deleted file is kept on this host. Staggered versioning thins them
    // out as they age — every version for the first hour, one a day for the first month.
    private const int KeepVersionsFor = 30 * 24 * 60 * 60;

    public bool IsAvailable => endpoint.IsReady;
    public string? Unavailable => endpoint.Unavailable;

    public async Task<string> DeviceIdAsync(CancellationToken cancellationToken)
    {
        using var document = await GetAsync("/rest/system/status", cancellationToken);
        return document.RootElement.TryGetProperty("myID", out var id) ? id.GetString() ?? "" : "";
    }

    public async Task<IReadOnlyList<SyncthingDevice>> DevicesAsync(CancellationToken cancellationToken)
    {
        var self = await DeviceIdAsync(cancellationToken);
        using var connections = await GetAsync("/rest/system/connections", cancellationToken);
        var online = connections.RootElement.TryGetProperty("connections", out var map) ? map : default;

        using var configured = await GetAsync("/rest/config/devices", cancellationToken);
        var devices = new List<SyncthingDevice>();
        foreach (var device in configured.RootElement.EnumerateArray())
        {
            var id = device.TryGetProperty("deviceID", out var value) ? value.GetString() ?? "" : "";
            // The host itself is in the list too; it isn't a computer anybody paired.
            if (id.Length == 0 || id == self) continue;
            var connected = false;
            string? address = null;
            if (online.ValueKind == JsonValueKind.Object && online.TryGetProperty(id, out var state))
            {
                connected = state.TryGetProperty("connected", out var flag) && flag.GetBoolean();
                address = state.TryGetProperty("address", out var at) ? at.GetString() : null;
            }
            devices.Add(new SyncthingDevice(id,
                device.TryGetProperty("name", out var name) ? name.GetString() ?? id[..7] : id[..7],
                connected, address,
                device.TryGetProperty("paused", out var paused) && paused.GetBoolean()));
        }
        return devices;
    }

    public Task AddDeviceAsync(string deviceId, string name, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/rest/config/devices", JsonSerializer.Serialize(new
        {
            deviceID = deviceId,
            name,
            addresses = new[] { "dynamic" },
            // A computer one account paired must never be able to add others, or have folders
            // created here just by offering them.
            introducer = false,
            autoAcceptFolders = false
        }), cancellationToken);

    public Task RemoveDeviceAsync(string deviceId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, $"/rest/config/devices/{Uri.EscapeDataString(deviceId)}", null, cancellationToken);

    public Task PauseDeviceAsync(string deviceId, bool paused, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Patch, $"/rest/config/devices/{Uri.EscapeDataString(deviceId)}",
            JsonSerializer.Serialize(new { paused }), cancellationToken);

    public async Task<IReadOnlyList<SyncthingFolder>> FoldersAsync(CancellationToken cancellationToken)
    {
        var self = await DeviceIdAsync(cancellationToken);
        using var configured = await GetAsync("/rest/config/folders", cancellationToken);
        var folders = new List<SyncthingFolder>();
        foreach (var folder in configured.RootElement.EnumerateArray())
        {
            var id = folder.TryGetProperty("id", out var value) ? value.GetString() ?? "" : "";
            if (id.Length == 0) continue;
            var devices = new List<string>();
            if (folder.TryGetProperty("devices", out var shared))
                foreach (var device in shared.EnumerateArray())
                    if (device.TryGetProperty("deviceID", out var deviceId) && deviceId.GetString() is { } text && text != self)
                        devices.Add(text);
            var versioned = folder.TryGetProperty("versioning", out var versioning)
                && versioning.TryGetProperty("type", out var type) && type.GetString() is { Length: > 0 };

            string? state = null, error = null;
            var files = 0;
            long bytes = 0;
            try
            {
                using var status = await GetAsync($"/rest/db/status?folder={Uri.EscapeDataString(id)}", cancellationToken);
                state = status.RootElement.TryGetProperty("state", out var current) ? current.GetString() : null;
                error = status.RootElement.TryGetProperty("error", out var problem) ? problem.GetString() : null;
                files = status.RootElement.TryGetProperty("localFiles", out var count) ? count.GetInt32() : 0;
                bytes = status.RootElement.TryGetProperty("localBytes", out var size) ? size.GetInt64() : 0;
            }
            catch (LibraryException)
            {
                // A folder whose status can't be read is still worth listing.
            }

            folders.Add(new SyncthingFolder(id,
                folder.TryGetProperty("label", out var label) ? label.GetString() ?? id : id,
                folder.TryGetProperty("path", out var path) ? path.GetString() ?? "" : "",
                devices, state, string.IsNullOrEmpty(error) ? null : error, files, bytes, versioned));
        }
        return folders;
    }

    /// <summary>Folders a paired device has offered that this host hasn't taken up.</summary>
    public async Task<IReadOnlyList<SyncthingOffer>> OffersAsync(CancellationToken cancellationToken)
    {
        using var document = await GetAsync("/rest/cluster/pending/folders", cancellationToken);
        var offers = new List<SyncthingOffer>();
        if (document.RootElement.ValueKind != JsonValueKind.Object) return offers;
        foreach (var folder in document.RootElement.EnumerateObject())
        {
            if (!folder.Value.TryGetProperty("offeredBy", out var offeredBy)) continue;
            foreach (var device in offeredBy.EnumerateObject())
            {
                var label = device.Value.TryGetProperty("label", out var text) ? text.GetString() ?? "" : "";
                offers.Add(new SyncthingOffer(folder.Name, label.Length > 0 ? label : folder.Name, device.Name));
            }
        }
        return offers;
    }

    public Task AddFolderAsync(string id, string label, string path, IReadOnlyList<string> deviceIds, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/rest/config/folders", JsonSerializer.Serialize(new
        {
            id,
            label,
            path,
            // A change made on either side reaches the other. When both change the same file
            // while apart, Syncthing keeps both rather than losing one.
            type = "sendreceive",
            fsWatcherEnabled = true,
            fsWatcherDelayS = 10,
            versioning = Versioning(),
            devices = deviceIds.Select(deviceId => new { deviceID = deviceId }).ToArray()
        }), cancellationToken);

    public Task RemoveFolderAsync(string id, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, $"/rest/config/folders/{Uri.EscapeDataString(id)}", null, cancellationToken);

    public Task SetFolderDevicesAsync(string id, IReadOnlyList<string> deviceIds, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Patch, $"/rest/config/folders/{Uri.EscapeDataString(id)}", JsonSerializer.Serialize(new
        {
            devices = deviceIds.Select(deviceId => new { deviceID = deviceId }).ToArray()
        }), cancellationToken);

    public Task SetFolderPathAsync(string id, string path, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Patch, $"/rest/config/folders/{Uri.EscapeDataString(id)}",
            JsonSerializer.Serialize(new { path }), cancellationToken);

    public Task KeepVersionsAsync(string id, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Patch, $"/rest/config/folders/{Uri.EscapeDataString(id)}",
            JsonSerializer.Serialize(new { versioning = Versioning() }), cancellationToken);

    /// <summary>
    /// A deletion made on a laptop reaches this host as faithfully as a new file does. Versions
    /// kept here, under the folder's hidden <c>.stversions</c>, are what make that recoverable.
    /// </summary>
    private static object Versioning() => new
    {
        type = "staggered",
        @params = new Dictionary<string, string> { ["maxAge"] = KeepVersionsFor.ToString() },
        cleanupIntervalS = 3600
    };

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(HttpMethod.Get, path, null, cancellationToken);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task SendAsync(HttpMethod method, string path, string? body, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(method, path, body, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, string? body, CancellationToken cancellationToken)
    {
        if (!endpoint.IsReady || endpoint.BaseAddress is null)
            throw new LibraryException(endpoint.Unavailable ?? "Syncthing isn’t running.", "sync_unavailable");
        using var request = new HttpRequestMessage(method, new Uri(endpoint.BaseAddress, path));
        request.Headers.Add("X-API-Key", endpoint.ApiKey ?? "");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException error)
        {
            throw new LibraryException($"Uncloud couldn’t reach Syncthing. {error.Message}", "sync_unavailable");
        }
        if (response.IsSuccessStatusCode) return response;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Dispose();
        throw new LibraryException(
            $"Syncthing refused that request ({(int)response.StatusCode}). {detail.Trim()}".Trim(), "sync_failed");
    }
}
