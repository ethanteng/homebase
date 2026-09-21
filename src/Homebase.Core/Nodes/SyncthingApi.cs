using System.Text;
using System.Text.Json;

namespace Homebase.Core.Nodes;

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
    public bool IsAvailable => endpoint.IsReady;
    public string? Unavailable => endpoint.Unavailable;

    public async Task<string> DeviceIdAsync(CancellationToken cancellationToken)
    {
        using var document = await GetAsync("/rest/system/status", cancellationToken);
        return document.RootElement.TryGetProperty("myID", out var id) ? id.GetString() ?? "" : "";
    }

    public async Task<IReadOnlyList<NodeDevice>> DevicesAsync(CancellationToken cancellationToken)
    {
        var self = await DeviceIdAsync(cancellationToken);
        using var connections = await GetAsync("/rest/system/connections", cancellationToken);
        var online = connections.RootElement.TryGetProperty("connections", out var map) ? map : default;

        using var configured = await GetAsync("/rest/config/devices", cancellationToken);
        var devices = new List<NodeDevice>();
        foreach (var device in configured.RootElement.EnumerateArray())
        {
            var id = device.TryGetProperty("deviceID", out var value) ? value.GetString() ?? "" : "";
            // The local device is in the list too; it isn't a node you paired.
            if (id.Length == 0 || id == self) continue;
            var connected = false;
            string? address = null;
            if (online.ValueKind == JsonValueKind.Object && online.TryGetProperty(id, out var state))
            {
                connected = state.TryGetProperty("connected", out var flag) && flag.GetBoolean();
                address = state.TryGetProperty("address", out var at) ? at.GetString() : null;
            }
            devices.Add(new NodeDevice(id,
                device.TryGetProperty("name", out var name) ? name.GetString() ?? id[..7] : id[..7],
                connected, address));
        }
        return devices;
    }

    public Task AddDeviceAsync(string deviceId, string name, CancellationToken cancellationToken) =>
        PostAsync("/rest/config/devices",
            JsonSerializer.Serialize(new { deviceID = deviceId, name, addresses = new[] { "dynamic" } }), cancellationToken);

    public async Task<IReadOnlyList<SharedFolder>> FoldersAsync(CancellationToken cancellationToken)
    {
        using var configured = await GetAsync("/rest/config/folders", cancellationToken);
        var folders = new List<SharedFolder>();
        foreach (var folder in configured.RootElement.EnumerateArray())
        {
            var id = folder.TryGetProperty("id", out var value) ? value.GetString() ?? "" : "";
            if (id.Length == 0) continue;
            var devices = new List<string>();
            if (folder.TryGetProperty("devices", out var shared))
                foreach (var device in shared.EnumerateArray())
                    if (device.TryGetProperty("deviceID", out var deviceId) && deviceId.GetString() is { } text)
                        devices.Add(text);

            string? state = null;
            var files = 0;
            long bytes = 0;
            try
            {
                using var status = await GetAsync($"/rest/db/status?folder={Uri.EscapeDataString(id)}", cancellationToken);
                state = status.RootElement.TryGetProperty("state", out var current) ? current.GetString() : null;
                files = status.RootElement.TryGetProperty("localFiles", out var count) ? count.GetInt32() : 0;
                bytes = status.RootElement.TryGetProperty("localBytes", out var size) ? size.GetInt64() : 0;
            }
            catch (LibraryException)
            {
                // A folder whose status can't be read is still worth listing.
            }

            folders.Add(new SharedFolder(id,
                folder.TryGetProperty("label", out var label) ? label.GetString() ?? id : id,
                folder.TryGetProperty("path", out var path) ? path.GetString() ?? "" : "",
                devices, state, files, bytes));
        }
        return folders;
    }

    /// <summary>Folders a paired device has offered that this one hasn't accepted yet.</summary>
    public async Task<IReadOnlyList<PendingFolder>> OffersAsync(CancellationToken cancellationToken)
    {
        var known = await DevicesAsync(cancellationToken);
        using var document = await GetAsync("/rest/cluster/pending/folders", cancellationToken);
        var offers = new List<PendingFolder>();
        if (document.RootElement.ValueKind != JsonValueKind.Object) return offers;
        foreach (var folder in document.RootElement.EnumerateObject())
        {
            if (!folder.Value.TryGetProperty("offeredBy", out var offeredBy)) continue;
            foreach (var device in offeredBy.EnumerateObject())
            {
                var label = device.Value.TryGetProperty("label", out var text) ? text.GetString() ?? "" : "";
                offers.Add(new PendingFolder(folder.Name,
                    label.Length > 0 ? label : folder.Name,
                    device.Name,
                    known.FirstOrDefault(entry => entry.DeviceId == device.Name)?.Name ?? device.Name[..7]));
            }
        }
        return offers;
    }

    public Task AddFolderAsync(string id, string label, string path, IReadOnlyList<string> deviceIds, CancellationToken cancellationToken) =>
        PostAsync("/rest/config/folders", JsonSerializer.Serialize(new
        {
            id,
            label,
            path,
            // Both sides may change files; Syncthing keeps a conflict copy rather than losing one.
            type = "sendreceive",
            fsWatcherEnabled = true,
            fsWatcherDelayS = 10,
            devices = deviceIds.Select(deviceId => new { deviceID = deviceId }).ToArray()
        }), cancellationToken);

    public Task IgnoreAsync(string folderId, IReadOnlyList<string> patterns, CancellationToken cancellationToken) =>
        PostAsync($"/rest/db/ignores?folder={Uri.EscapeDataString(folderId)}",
            JsonSerializer.Serialize(new { ignore = patterns }), cancellationToken);

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task PostAsync(string path, string body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, CancellationToken cancellationToken)
    {
        if (!endpoint.IsReady || endpoint.BaseAddress is null)
            throw new LibraryException(endpoint.Unavailable ?? "Syncthing isn’t running.", "unsupported");
        using var request = new HttpRequestMessage(method, new Uri(endpoint.BaseAddress, path));
        request.Headers.Add("X-API-Key", endpoint.ApiKey ?? "");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return response;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Dispose();
        throw new LibraryException(
            $"Syncthing refused that request ({(int)response.StatusCode}). {detail.Trim()}".Trim(), "node_failed");
    }
}
