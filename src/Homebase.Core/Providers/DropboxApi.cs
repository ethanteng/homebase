using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Homebase.Core.Providers;

/// <summary>
/// Dropbox's HTTP API, read-only. Homebase asks for metadata and content and nothing else, so a
/// mistake here cannot change what is in the connected account.
/// </summary>
public sealed class DropboxApi(HttpClient client, DropboxTokenStore tokens, string? appKey) : IDropboxApi
{
    private const string Api = "https://api.dropboxapi.com";
    private const string Content = "https://content.dropboxapi.com";
    // However long Dropbox asks for, an import shouldn't stall on one folder for minutes.
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Attempts per request. Dropbox rate-limits a long recursive import, and a refusal it asks
    /// us to retry must not read as a folder that can't be listed.
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Waiting between attempts, replaced in tests so they don't sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(appKey);
    public bool IsConnected => IsConfigured && tokens.Load() is not null;
    public string? AccountName => tokens.LoadAccountName();

    public string AppKey => appKey ?? throw new LibraryException(
        "Uncloud isn’t set up for Dropbox yet. Add a Dropbox app key to connect.", "provider_unconfigured");

    public async Task ConnectAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        var result = await DropboxOAuth.ExchangeAsync(client, AppKey, code, verifier, redirectUri, cancellationToken);
        if (result.RefreshToken is null)
            throw new LibraryException("Dropbox didn’t return a lasting connection. Try connecting again.", "provider_auth");
        _accessToken = result.AccessToken;
        _expiresAt = result.ExpiresAt;
        string? name = null;
        try { name = (await GetAccountAsync(cancellationToken)).Name; } catch (LibraryException) { }
        await tokens.SaveAsync(result.RefreshToken, name, cancellationToken);
    }

    public void Disconnect()
    {
        tokens.Clear();
        _accessToken = null;
        _expiresAt = DateTimeOffset.MinValue;
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // A minute of headroom so a request can't start with a token that expires mid-flight.
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-1)) return _accessToken;
            var refresh = tokens.Load()
                ?? throw new LibraryException("Connect Uncloud to Dropbox first.", "provider_disconnected");
            var result = await DropboxOAuth.RefreshAsync(client, AppKey, refresh, cancellationToken);
            _accessToken = result.AccessToken;
            _expiresAt = result.ExpiresAt;
            return _accessToken;
        }
        finally { _gate.Release(); }
    }

    public async Task<DropboxAccount> GetAccountAsync(CancellationToken cancellationToken)
    {
        using var document = await RpcAsync("/2/users/get_current_account", null, cancellationToken);
        var root = document.RootElement;
        return new DropboxAccount(
            root.GetProperty("account_id").GetString() ?? "",
            root.TryGetProperty("name", out var name) && name.TryGetProperty("display_name", out var display)
                ? display.GetString() ?? "Dropbox" : "Dropbox",
            root.TryGetProperty("email", out var email) ? email.GetString() : null);
    }

    public async Task<IReadOnlyList<DropboxEntry>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        // Dropbox represents the account root as an empty string, not "/".
        var argument = new { path = path is "" or "/" ? "" : path.TrimEnd('/') };
        var entries = new List<DropboxEntry>();
        // Dropbox pages large folders. Without following the cursor, everything past the first
        // page would simply be absent from the picker with no sign that it was missing.
        var endpoint = "/2/files/list_folder";
        var body = JsonSerializer.Serialize(argument);
        for (var page = 0; page < 500; page++)
        {
            using var document = await RpcAsync(endpoint, body, cancellationToken);
            entries.AddRange(ReadEntries(document.RootElement));
            if (!document.RootElement.TryGetProperty("has_more", out var more) || !more.GetBoolean()) break;
            if (!document.RootElement.TryGetProperty("cursor", out var cursor)) break;
            endpoint = "/2/files/list_folder/continue";
            body = JsonSerializer.Serialize(new { cursor = cursor.GetString() });
        }
        return entries
            .OrderByDescending(entry => entry.IsFolder)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<DropboxEntry> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        using var document = await RpcAsync("/2/files/get_metadata", JsonSerializer.Serialize(new { path }), cancellationToken);
        return ReadEntry(document.RootElement);
    }

    public async Task<Stream> DownloadAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Content}/2/files/download");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(cancellationToken));
            request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { path }));
            // The response outlives this method when it succeeds: the caller reads the stream.
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsStreamAsync(cancellationToken);

            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = response.StatusCode;
            var wait = RetryAfter(response, attempt);
            response.Dispose();
            if (wait is null) throw Failure(status, detail);
            await Wait(wait.Value, cancellationToken);
        }
    }

    private async Task<JsonDocument> RpcAsync(string path, string? body, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(cancellationToken));
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode) return JsonDocument.Parse(text);
            if (RetryAfter(response, attempt) is not { } wait) throw Failure(response.StatusCode, text);
            await Wait(wait, cancellationToken);
        }
    }

    /// <summary>
    /// How long to wait before asking again, or null when this refusal is final. Dropbox asks for a
    /// pause with 429 during a long import and can have a moment of its own with a 5xx; anything
    /// else — a missing path, an expired connection — would only fail the same way again.
    /// </summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response, int attempt)
    {
        if (attempt >= MaxAttempts) return null;
        var status = (int)response.StatusCode;
        if (status != 429 && status < 500) return null;
        // Dropbox's own number beats a guess, but it isn't a licence to wait all afternoon.
        var asked = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
        var wait = asked ?? TimeSpan.FromSeconds(1 << (attempt - 1));
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > LongestWait ? LongestWait : wait;
    }

    private static LibraryException Failure(System.Net.HttpStatusCode status, string detail) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized =>
            new LibraryException("Dropbox rejected the connection. Connect Uncloud to Dropbox again.", "provider_auth"),
        System.Net.HttpStatusCode.Conflict when detail.Contains("not_found", StringComparison.Ordinal) =>
            new LibraryException("That file is no longer in Dropbox.", "not_found"),
        System.Net.HttpStatusCode.TooManyRequests =>
            new LibraryException("Dropbox is asking Uncloud to slow down. Try again in a moment.", "busy"),
        _ => new LibraryException($"Dropbox couldn’t complete that request ({(int)status}).", "provider_failed")
    };

    private static IEnumerable<DropboxEntry> ReadEntries(JsonElement root)
    {
        if (!root.TryGetProperty("entries", out var entries)) yield break;
        foreach (var entry in entries.EnumerateArray()) yield return ReadEntry(entry);
    }

    private static DropboxEntry ReadEntry(JsonElement element)
    {
        var tag = element.TryGetProperty(".tag", out var value) ? value.GetString() : null;
        var pathDisplay = element.TryGetProperty("path_display", out var display) ? display.GetString() ?? "" : "";
        var pathLower = element.TryGetProperty("path_lower", out var lower) ? lower.GetString() ?? "" : "";
        return new DropboxEntry(
            element.TryGetProperty("id", out var id) ? id.GetString() ?? pathLower : pathLower,
            element.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            pathLower,
            pathDisplay.Length > 0 ? pathDisplay : pathLower,
            tag == "folder",
            element.TryGetProperty("size", out var size) ? size.GetInt64() : null,
            element.TryGetProperty("rev", out var rev) ? rev.GetString() : null,
            element.TryGetProperty("server_modified", out var modified) && modified.TryGetDateTimeOffset(out var when) ? when : null);
    }
}
