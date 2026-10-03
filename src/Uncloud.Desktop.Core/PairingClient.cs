using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core.Sync;

namespace Uncloud.Desktop;

/// <summary>
/// A request this computer made to be added, as it shows it: the address it asked, the id its QR
/// code carries, and the secret it collects the answer with, which never leaves this computer.
/// </summary>
public sealed record PairingRequest(Uri Address, string Id, string Secret, DateTimeOffset ExpiresAt)
{
    /// <summary>Where a signed-in person approves it: what the QR code says.</summary>
    public Uri ApproveUrl => new(Address, $"/?pair={Uri.EscapeDataString(Id)}");
}

/// <summary>Pairs this computer with the Uncloud it came from, by a code or by asking to be approved.</summary>
public sealed class PairingClient(HttpClient client)
{
    public Task<PairingResult> PairAsync(PairingLink link, string deviceId, string computerName, CancellationToken cancellationToken) =>
        SendAsync<PairingResult>(link.Address, "/api/sync/pair",
            new { code = link.Code, deviceId, name = computerName, syncEverything = true }, cancellationToken);

    /// <summary>Asks to be added, for somebody signed in to approve.</summary>
    public async Task<PairingRequest> RequestAsync(Uri address, string deviceId, string computerName, CancellationToken cancellationToken)
    {
        try
        {
            var ticket = await SendAsync<PairingTicket>(address, "/api/sync/pair/requests",
                new { deviceId, name = computerName, syncEverything = true }, cancellationToken);
            return new PairingRequest(address, ticket.Id, ticket.Secret, ticket.ExpiresAt);
        }
        catch (PairingExpiredException)
        {
            // Nothing here can have run out yet: this is an Uncloud from before computers asked.
            throw new PairingException(
                "This Uncloud can’t show a code to scan until the app on the host is updated. Use a pairing code from My computers instead.");
        }
    }

    /// <summary>
    /// What it takes to sync, once somebody has approved the request; null while nobody has yet.
    /// A request that has run out is a <see cref="PairingExpiredException"/>, for a new one.
    /// </summary>
    public async Task<PairingResult?> AnswerAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        var answer = await SendAsync<PairingAnswer>(request.Address,
            $"/api/sync/pair/requests/{Uri.EscapeDataString(request.Id)}/answer", new { secret = request.Secret }, cancellationToken);
        return answer.Approved
            ? answer.Result ?? throw new PairingException("Uncloud answered, but not with anything this app understands.")
            : null;
    }

    private async Task<T> SendAsync<T>(Uri address, string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address, path)) { Content = JsonContent.Create(body) };
        // What Uncloud asks of anything that changes something, so a web page elsewhere can't.
        request.Headers.Add("X-Homebase-Request", "1");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException
            // The client giving up, not the caller: told apart so a window waiting for approval
            // carries on waiting instead of taking it for being closed.
            || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new PairingException(
                $"Couldn’t reach Uncloud at {address.Host}. Check the address, and that this computer can open it in a browser. ({error.Message})");
        }
        using (response)
        {
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                    ?? throw new PairingException("Uncloud answered, but not with anything this app understands.");
            var detail = await DetailAsync(response, cancellationToken);
            throw response.StatusCode is HttpStatusCode.NotFound ? new PairingExpiredException(detail) : new PairingException(detail);
        }
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (problem.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text)
                return text;
        }
        catch (JsonException) { }
        return $"Uncloud refused to pair this computer ({(int)response.StatusCode}).";
    }
}

public class PairingException(string message) : Exception(message);

/// <summary>
/// Uncloud no longer knows the request: it ran out, or was lost when the host restarted. The
/// answer is a new one.
/// </summary>
public sealed class PairingExpiredException(string message) : PairingException(message);
