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
///
/// And it remembers the app key and redirect URI the sign-in was started with, rather than working
/// them out again when the code comes back. Dropbox checks a code against the app it was issued to
/// and the address it was issued for, and neither is reliably what this host would answer a minute
/// later: an administrator setting or clearing the host key changes which Dropbox app every account
/// without one of its own uses, and a tunnel coming up changes the address. Both can happen while
/// the browser is away at dropbox.com, and recomputing either loses a sign-in the person completed
/// correctly.
/// </summary>
public sealed class DropboxAuthFlow
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private sealed record Pending(
        string Verifier, string? State, string? ReturnTo, string AppKey, string? RedirectUri,
        DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <param name="wayBack">
    /// Puts the way back to this host on the end of the state, for a sign-in that returns by way of
    /// the relay rather than straight here. Left alone for one that comes back directly, which needs
    /// to tell the relay nothing.
    /// </param>
    public string Begin(string userId, string appKey, string redirectUri, string? returnTo = null,
        Func<string, string>? wayBack = null)
    {
        var verifier = DropboxOAuth.CreateVerifier();
        var state = DropboxOAuth.CreateVerifier();
        if (wayBack is not null) state = wayBack(state);
        Prune();
        // One at a time per account: starting a sign-in abandons whichever one came before it.
        _pending[userId] = new Pending(verifier, state, returnTo, appKey, redirectUri,
            DateTimeOffset.UtcNow.Add(Lifetime));
        return DropboxOAuth.AuthorizeUrl(appKey, redirectUri, verifier, state);
    }

    /// <summary>
    /// Starts a sign-in that Dropbox will not return anywhere: it shows the code on its own page and
    /// the person brings it back by hand. The only way to connect from a computer this host cannot
    /// be reached at, since every address Dropbox would return to has to be registered with the app
    /// beforehand and nobody can register their own.
    ///
    /// There is no state, because state guards a callback and there is no callback to guard. What
    /// takes its place is the session: the code is handed back through <see cref="ClaimedBy"/> by an
    /// account that is already signed in, and only ever becomes that account's connection.
    /// </summary>
    public string BeginWithoutReturn(string userId, string appKey)
    {
        var verifier = DropboxOAuth.CreateVerifier();
        Prune();
        _pending[userId] = new Pending(verifier, null, null, appKey, null,
            DateTimeOffset.UtcNow.Add(Lifetime));
        return DropboxOAuth.AuthorizeUrl(appKey, null, verifier, null);
    }

    /// <summary>
    /// The verifier and app key this account is waiting to finish a pasted sign-in with. Left in
    /// place rather than taken: a code copied across by hand gets mistyped, and losing the whole
    /// sign-in over one wrong character would mean starting again at Dropbox for nothing. The
    /// caller takes it with <see cref="Forget"/> once a code has actually been spent, which is what
    /// stops one being used twice; until then it stands until it expires.
    ///
    /// Refuses a sign-in that expected to come back on its own: that one's code belongs to a
    /// redirect, and accepting a pasted code against it would let a person be talked into carrying
    /// across a code from a sign-in they did not start.
    /// </summary>
    public (string Verifier, string AppKey) ClaimedBy(string userId)
    {
        if (_pending.TryGetValue(userId, out var pending)
            && pending.State is null
            && DateTimeOffset.UtcNow <= pending.Expires)
            return (pending.Verifier, pending.AppKey);
        throw new LibraryException(
            "That code didn't come from a sign-in Uncloud started, or it took too long. "
            + "Press Connect again.", "provider_auth");
    }

    /// <summary>
    /// The account, verifier, return address, app key and redirect URI this state belongs to,
    /// forgotten on the way out so a code can never be replayed against it.
    /// </summary>
    public (string UserId, string Verifier, string? ReturnTo, string AppKey, string? RedirectUri)
        Consume(string? state)
    {
        if (state is not null)
        {
            var offered = Encoding.UTF8.GetBytes(state);
            foreach (var (userId, pending) in _pending)
            {
                if (pending.State is null) continue;
                var candidate = Encoding.UTF8.GetBytes(pending.State);
                if (candidate.Length != offered.Length
                    || !CryptographicOperations.FixedTimeEquals(candidate, offered)) continue;
                _pending.TryRemove(userId, out _);
                if (DateTimeOffset.UtcNow <= pending.Expires)
                    return (userId, pending.Verifier, pending.ReturnTo, pending.AppKey,
                        pending.RedirectUri);
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
