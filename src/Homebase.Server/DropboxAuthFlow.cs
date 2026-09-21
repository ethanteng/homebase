using Homebase.Core;
using Homebase.Core.Providers;

namespace Homebase.Server;

/// <summary>
/// Holds the one-time PKCE verifier and state between sending the browser to Dropbox and
/// Dropbox sending it back. State is checked and consumed on return, which is what stops another
/// site from feeding Homebase an authorization code of its choosing.
/// </summary>
public sealed class DropboxAuthFlow
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly object _lock = new();
    private (string Verifier, string State, DateTimeOffset Expires)? _pending;

    public string Begin(string appKey, string redirectUri)
    {
        var verifier = DropboxOAuth.CreateVerifier();
        var state = DropboxOAuth.CreateVerifier();
        lock (_lock) _pending = (verifier, state, DateTimeOffset.UtcNow.Add(Lifetime));
        return DropboxOAuth.AuthorizeUrl(appKey, redirectUri, verifier, state);
    }

    /// <summary>Returns the verifier for a matching state, and forgets it either way.</summary>
    public string Consume(string? state)
    {
        lock (_lock)
        {
            var pending = _pending;
            _pending = null;
            if (pending is null || state is null || DateTimeOffset.UtcNow > pending.Value.Expires)
                throw new LibraryException("That sign-in didn’t come from Homebase, or it took too long. Try again.", "provider_auth");
            // Fixed-time comparison: the state is a secret for the length of the round trip.
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(pending.Value.State), System.Text.Encoding.UTF8.GetBytes(state)))
                throw new LibraryException("That sign-in didn’t come from Homebase. Try again.", "provider_auth");
            return pending.Value.Verifier;
        }
    }
}
