using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Homebase.Core.Providers;

/// <summary>
/// The authorization-code flow with PKCE, which is the flow for a client that cannot keep a
/// secret. Homebase ships an app key only; there is no client secret to leak.
/// </summary>
public static class DropboxOAuth
{
    public const string AuthorizeEndpoint = "https://www.dropbox.com/oauth2/authorize";
    public const string TokenEndpoint = "https://api.dropboxapi.com/oauth2/token";

    public sealed record Tokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

    public static string CreateVerifier()
    {
        Span<byte> bytes = stackalloc byte[64];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string AuthorizeUrl(string appKey, string redirectUri, string verifier, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = appKey,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256",
            // Offline access is what makes the connection outlive a single access token.
            ["token_access_type"] = "offline",
            ["state"] = state
        };
        return $"{AuthorizeEndpoint}?{string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))}";
    }

    public static Task<Tokens> ExchangeAsync(HttpClient client, string appKey, string code, string verifier, string redirectUri, CancellationToken cancellationToken) =>
        PostAsync(client, new Dictionary<string, string>
        {
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["client_id"] = appKey,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri
        }, cancellationToken);

    public static Task<Tokens> RefreshAsync(HttpClient client, string appKey, string refreshToken, CancellationToken cancellationToken) =>
        PostAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = appKey
        }, cancellationToken);

    private static async Task<Tokens> PostAsync(HttpClient client, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new LibraryException($"Dropbox refused the sign-in ({(int)response.StatusCode}). Try connecting again.", "provider_auth");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var access = root.TryGetProperty("access_token", out var token) ? token.GetString() : null;
        if (access is null) throw new LibraryException("Dropbox didn’t return an access token.", "provider_auth");
        var seconds = root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 14400;
        return new Tokens(access,
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            DateTimeOffset.UtcNow.AddSeconds(seconds));
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
