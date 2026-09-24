using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core.Sync;

namespace Uncloud.Desktop;

/// <summary>Redeems a pairing code with the Uncloud it came from.</summary>
public sealed class PairingClient(HttpClient client)
{
    public async Task<PairingResult> PairAsync(PairingLink link, string deviceId, string computerName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(link.Address, "/api/sync/pair"))
        {
            Content = JsonContent.Create(new { code = link.Code, deviceId, name = computerName, syncEverything = true })
        };
        // What Uncloud asks of anything that changes something, so a web page elsewhere can't.
        request.Headers.Add("X-Homebase-Request", "1");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException error)
        {
            throw new PairingException(
                $"Couldn’t reach Uncloud at {link.Address.Host}. Check the address, and that this computer can open it in a browser. ({error.Message})");
        }
        using (response)
        {
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<PairingResult>(cancellationToken)
                    ?? throw new PairingException("Uncloud answered, but not with anything this app understands.");
            throw new PairingException(await DetailAsync(response, cancellationToken));
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

public sealed class PairingException(string message) : Exception(message);
