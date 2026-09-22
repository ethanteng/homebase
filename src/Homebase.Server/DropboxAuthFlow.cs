using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Homebase.Core;
using Homebase.Core.Providers;

namespace Homebase.Server;

/// <summary>
/// Holds the one-time PKCE verifier between sending a browser to Dropbox and Dropbox sending it
/// back, against the account that started it.
///
/// The return trip is a navigation from dropbox.com, so it cannot be relied on to carry a
/// session. The state does that work instead: 512 bits of randomness, compared in fixed time,
/// good for ten minutes, and consumed exactly once. It names which account to connect, which is
/// also why two people connecting Dropbox at the same moment no longer overwrite each other.
///
/// It also remembers where the person was. Dropbox returns the browser to the one redirect URI
/// registered with the app, which may not be the address they opened Uncloud at — 127.0.0.1 and
/// localhost are one host but two cookie jars, so landing on the other one reads as being signed
/// out at the very moment the connection succeeded. The origin is only ever one this Uncloud
/// already answers to, checked before it is stored.
/// </summary>
public sealed class DropboxAuthFlow
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private sealed record Pending(string Verifier, string State, string? ReturnTo, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public string Begin(string userId, string appKey, string redirectUri, string? returnTo = null)
    {
        var verifier = DropboxOAuth.CreateVerifier();
        var state = DropboxOAuth.CreateVerifier();
        Prune();
        // One at a time per account: starting a sign-in abandons whichever one came before it.
        _pending[userId] = new Pending(verifier, state, returnTo, DateTimeOffset.UtcNow.Add(Lifetime));
        return DropboxOAuth.AuthorizeUrl(appKey, redirectUri, verifier, state);
    }

    /// <summary>
    /// The account, verifier and return address this state belongs to, forgotten on the way out so
    /// a code can never be replayed against it.
    /// </summary>
    public (string UserId, string Verifier, string? ReturnTo) Consume(string? state)
    {
        if (state is not null)
        {
            var offered = Encoding.UTF8.GetBytes(state);
            foreach (var (userId, pending) in _pending)
            {
                var candidate = Encoding.UTF8.GetBytes(pending.State);
                if (candidate.Length != offered.Length
                    || !CryptographicOperations.FixedTimeEquals(candidate, offered)) continue;
                _pending.TryRemove(userId, out _);
                if (DateTimeOffset.UtcNow <= pending.Expires) return (userId, pending.Verifier, pending.ReturnTo);
                break;
            }
        }
        throw new LibraryException(
            "That sign-in didn't come from Uncloud, or it took too long. Try again.", "provider_auth");
    }

    public void Forget(string userId) => _pending.TryRemove(userId, out _);

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (userId, pending) in _pending)
            if (now > pending.Expires)
                _pending.TryRemove(userId, out _);
    }
}
