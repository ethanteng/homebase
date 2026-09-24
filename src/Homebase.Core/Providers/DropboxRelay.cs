namespace Homebase.Core.Providers;

/// <summary>
/// Uncloud's own Dropbox app, and the one address on the internet that its sign-ins come back to.
///
/// Dropbox only returns a browser to an address registered with the app being signed in to, and
/// an address on somebody's own computer can only be registered by somebody with an account on
/// dropbox.com/developers. That is the whole of why connecting Dropbox used to mean a developer
/// console: not the flow, which was always ordinary OAuth, but the one registration step that
/// nobody but the person running Uncloud could do for them.
///
/// So it is done once, centrally: one app, one registered address, a page there that forwards the
/// sign-in the last hop to whichever Uncloud started it. Anybody who would rather not rely on
/// that still sets their own key and is not affected by any of this.
///
/// <para>
/// The key below is not a secret, and is in the open here on purpose. Uncloud signs in with PKCE
/// exactly because a program running on somebody's own computer cannot keep a secret: there is no
/// client secret anywhere in Uncloud, and a key on its own authorises nothing. What a sign-in
/// needs is the verifier, which is made on the computer that started it and never leaves.
/// </para>
///
/// <para>
/// Which is also what makes the hop through <see cref="CallbackUrl"/> safe to offer. The code that
/// passes through it cannot be exchanged for a token by the page, by whoever hosts it, or by
/// anyone reading its logs, because none of them has the other half. The relay's own rule — that
/// it forwards a sign-in to nowhere but the loopback address — is what keeps that true, and it is
/// in <c>api/dropbox-callback.js</c> next to the reasoning.
/// </para>
/// </summary>
public sealed class DropboxRelay
{
    /// <summary>
    /// Uncloud's Dropbox app key, registered against <see cref="CallbackUrl"/>. Empty in a build
    /// nobody has set one for, and then everything here is simply unavailable: an account connects
    /// through its own key or the host's, exactly as it did before any of this existed.
    /// </summary>
    private const string BuiltIn = "hsfwtd0lqlnivnc";

    /// <summary>
    /// Where Dropbox sends every sign-in that went through Uncloud's own app. Registered with that
    /// app, so it is fixed: a build already released is looking for its sign-in to come back here,
    /// and changing it strands every one of them. Worth treating as permanent.
    /// </summary>
    public const string CallbackUrl = "https://www.uncloud.life/api/dropbox-callback";

    /// <param name="appKeyOverride">
    /// A key to use instead of the built-in one. Null means there was none given, and the build's
    /// own is used; an empty one is a deliberate "none", which is how a build that carries a key can
    /// still be run without offering it.
    /// </param>
    public DropboxRelay(string? appKeyOverride = null, string? callbackOverride = null)
    {
        AppKey = appKeyOverride is null ? Trimmed(BuiltIn) : Trimmed(appKeyOverride);
        Callback = Trimmed(callbackOverride) ?? CallbackUrl;
    }

    /// <summary>The app key sign-ins through the relay use, or null when this build has none.</summary>
    public string? AppKey { get; }

    /// <summary>The address those sign-ins come back to.</summary>
    public string Callback { get; }

    /// <summary>Whether this build can offer Dropbox to somebody who has set nothing up.</summary>
    public bool Available => AppKey is not null;

    /// <summary>
    /// The state to send, with the way back to this host on the end of it: the scheme it answers
    /// on and the port it listens on, which is all the relay is told and all it needs. The rest is
    /// the randomness the state is for, and the whole string is what comes back and gets checked,
    /// so the suffix costs the state nothing.
    ///
    /// Deliberately not a whole address. The relay builds one around this port and the loopback
    /// host, and a port is the most a request could ask for that still cannot reach past the
    /// person's own computer.
    /// </summary>
    public static string StateWithWayBack(string state, bool secure, int port) =>
        $"{state}.{(secure ? 's' : 'h')}{port}";

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
