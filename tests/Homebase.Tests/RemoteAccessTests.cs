using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homebase.Tests;

/// <summary>
/// Remote access is settled before the application is built, so a test drives it by replacing
/// <see cref="RemoteAccess.Override"/> — one switch for the whole process. These tests therefore
/// run on their own, or another test's host would come up through somebody else's tunnel.
/// </summary>
[CollectionDefinition(nameof(RemoteAccessTests), DisableParallelization = true)]
public sealed class RemoteAccessCollection;

/// <summary>
/// Reaching this host from outside the house. No test here runs anybody's tunnel program: the
/// one that would is pointed at a name that isn't a program, to prove the refusal. The rest
/// stand in for the tunnel so the parts that matter — which names Uncloud answers to, where it
/// tells Dropbox to return the browser, who is allowed to read the address — are what is tested.
/// </summary>
[Collection(nameof(RemoteAccessTests))]
public sealed class RemoteAccessTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), $"uncloud-remote-{Guid.NewGuid():N}");
    private readonly string _config;
    private readonly string _host;

    public RemoteAccessTests()
    {
        _config = Path.Combine(_temporary, "config");
        _host = Path.Combine(_temporary, "host");
        Directory.CreateDirectory(_config);
        Directory.CreateDirectory(_host);
    }

    /// <summary>Stands a tunnel up in place of the real one, announcing these addresses in turn.</summary>
    private Tunnels Open(string provider, params string[] addresses) =>
        Standing(provider, null, addresses);

    /// <summary>The same, for a tunnel that has to be allowed by a person before it opens.</summary>
    private Tunnels OpenAwaitingSignIn(string provider, string signIn, params string[] addresses) =>
        Standing(provider, signIn, addresses);

    private Tunnels Standing(string provider, string? signIn, string[] addresses)
    {
        var tunnels = new Tunnels(signIn, addresses);
        var options = Read(new() { ["Homebase:RemoteAccess:Provider"] = provider });
        RemoteAccess.Override = _ => new RemoteAccess(options, NullLogger.Instance, tunnels.Next);
        return tunnels;
    }

    /// <summary>A browser arriving through the tunnel, as the tunnel passes it on.</summary>
    private static HttpClient Through(TestHost app, string hostname, string from = "203.0.113.5")
    {
        var client = app.Anonymous();
        client.DefaultRequestHeaders.Host = hostname;
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", hostname);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", from);
        client.DefaultRequestHeaders.Add("Origin", $"https://{hostname}");
        return client;
    }

    /// <summary>
    /// A session issued through the tunnel is a Secure cookie, and the test client talks to the
    /// server over plain http, so its cookie jar rightly refuses to send it back. Carrying the
    /// cookie by hand is what a browser on the far side of the tunnel does for free.
    /// </summary>
    private static void KeepSession(HttpClient client, HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Select(header => header.Split(';')[0])
            .Single(pair => pair.StartsWith("uncloud_session=", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Add("Cookie", cookie);
    }

    [Fact]
    public async Task The_address_a_tunnel_announces_is_one_this_host_answers_to()
    {
        const string Announced = "home.tail9f3a.ts.net";
        using var tunnels = Open("tailscale", Announced);
        using var app = new TestHost(_config);

        using var owner = await app.SignUpAsync("ada");

        using var arriving = Through(app, Announced);
        var response = await arriving.PostAsJsonAsync("/api/session",
            new { username = "ada", password = TestHost.Password });

        response.EnsureSuccessStatusCode();
        // The tunnel is where TLS ends, so the session cookie has to be issued for https even
        // though Kestrel itself only ever saw plain HTTP arrive over loopback.
        Assert.Contains("secure", Assert.Single(response.Headers.GetValues("Set-Cookie")),
            StringComparison.OrdinalIgnoreCase);

        // A name the tunnel never announced is still nobody's way in.
        using var elsewhere = Through(app, "files.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await elsewhere.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task A_tunnel_that_forwards_only_the_scheme_is_still_understood()
    {
        const string Announced = "home.tail9f3a.ts.net";
        using var tunnels = Open("tailscale", Announced);
        using var app = new TestHost(_config);

        // What cloudflared and tailscale actually send: the browser's Host header passed through
        // untouched and the scheme forwarded beside it, with no X-Forwarded-Host at all.
        using var owner = await app.SignUpAsync("ada");

        using var arriving = app.Anonymous();
        arriving.DefaultRequestHeaders.Host = Announced;
        arriving.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        arriving.DefaultRequestHeaders.Add("Origin", $"https://{Announced}");

        var response = await arriving.PostAsJsonAsync("/api/session",
            new { username = "ada", password = TestHost.Password });

        response.EnsureSuccessStatusCode();
        Assert.Contains("secure", Assert.Single(response.Headers.GetValues("Set-Cookie")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_tunnel_decides_where_dropbox_returns_the_browser()
    {
        const string Announced = "home.tail9f3a.ts.net";
        using var tunnels = Open("tailscale", Announced);
        using var app = new TestHost(_config);
        using var admin = await app.SignUpAsync("ada");

        var state = await admin.GetFromJsonAsync<JsonElement>("/api/remote-access");

        Assert.Equal("on", state.GetProperty("status").GetString());
        Assert.Equal(Announced, state.GetProperty("hostname").GetString());
        Assert.Equal($"https://{Announced}", state.GetProperty("url").GetString());
        // Which is also the address the Dropbox redirect is built from: a host reached through
        // the tunnel cannot send people back to a name only its own network knows.
        Assert.Equal($"https://{Announced}", state.GetProperty("publicUrl").GetString());
    }

    [Fact]
    public async Task A_public_url_that_was_configured_is_not_overruled_by_the_tunnel()
    {
        const string Announced = "home.tail9f3a.ts.net";
        using var tunnels = Open("tailscale", Announced);
        using var app = new TestHost(_config, settings: new Dictionary<string, string?>
        {
            ["Homebase:PublicUrl"] = "https://files.example.com"
        });
        using var admin = await app.SignUpAsync("ada");

        var state = await admin.GetFromJsonAsync<JsonElement>("/api/remote-access");

        // A host that already had a name of its own keeps sending Dropbox there; the tunnel is
        // another way in, not a decision to re-register everything under a different address.
        Assert.Equal("https://files.example.com", state.GetProperty("publicUrl").GetString());
        Assert.Equal($"https://{Announced}", state.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Only_an_administrator_is_told_the_public_address()
    {
        const string Announced = "home.tail9f3a.ts.net";
        using var tunnels = Open("tailscale", Announced);
        using var app = new TestHost(_config);
        using var admin = await app.SignUpAsync("ada");
        await TestHost.SetHostRootAsync(admin, _host);
        (await admin.PostAsJsonAsync("/api/users",
            new { username = "bo", displayName = "bo", password = TestHost.Password })).EnsureSuccessStatusCode();

        using var member = await SignInThroughAsync(app, Announced, "bo");

        // The address is the administrator's to hand out, alongside everything else about the
        // host that only they are shown.
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/remote-access")).StatusCode);
    }

    [Fact]
    public async Task A_tunnel_that_comes_back_under_a_new_address_is_answered_under_it()
    {
        const string First = "shy-dog-42.trycloudflare.com";
        const string Second = "brave-cat-17.trycloudflare.com";
        using var tunnels = Open("cloudflare", First, Second);
        using var app = new TestHost(_config);
        using (var arriving = Through(app, First))
            (await arriving.GetAsync("/api/health")).EnsureSuccessStatusCode();

        // An unnamed Cloudflare tunnel is handed a different name every time it is opened, so
        // the address settled at startup cannot be the only one this host will ever answer to.
        tunnels.Latest!.Drop();

        using var moved = Through(app, Second);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        HttpResponseMessage response;
        while (!(response = await moved.GetAsync("/api/health")).IsSuccessStatusCode
               && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);

        response.EnsureSuccessStatusCode();
        // Following the tunnel is not the same as following anything: a name it never carried is
        // refused before and after a reconnect alike.
        using var never = Through(app, "someone-else-99.trycloudflare.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await never.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task The_first_account_cannot_be_claimed_over_the_internet()
    {
        const string Announced = "home.tail9f3a.ts.net";
        using var tunnels = Open("tailscale", Announced);
        using var app = new TestHost(_config);

        // Until the first account exists there is nobody here to refuse anybody, so whoever asks
        // first becomes this host's administrator. The tunnel puts that address on the internet.
        using var arriving = Through(app, Announced);
        var claimed = await arriving.PostAsJsonAsync("/api/setup",
            new { username = "mallory", displayName = "Mallory", password = TestHost.Password });

        Assert.Equal(HttpStatusCode.Forbidden, claimed.StatusCode);

        // And the owner, at the host itself, still sets it up as they always could.
        using var owner = await app.SignUpAsync("ada");
        var session = await owner.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal("ada", session.GetProperty("user").GetProperty("username").GetString());
    }

    [Fact]
    public async Task Guessing_from_the_internet_is_counted_against_the_guesser()
    {
        const string Announced = "home.tail9f3a.ts.net";
        using var tunnels = Open("tailscale", Announced);
        using var app = new TestHost(_config);
        using var owner = await app.SignUpAsync("ada");

        // Everything through a tunnel reaches Kestrel from loopback. Unless the forwarded client
        // address is the one counted, a spray of invented usernames from one stranger fills the
        // single bucket every other person on the host shares.
        using var attacker = Through(app, Announced, from: "198.51.100.66");
        HttpStatusCode last = default;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var refused = await attacker.PostAsJsonAsync("/api/session",
                new { username = $"guess{attempt}", password = "not the password" });
            last = refused.StatusCode;
            Assert.True(last is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests,
                $"A wrong password answered {last}.");
        }

        // The wall is still there, and it is the stranger who has run into it.
        Assert.Equal(HttpStatusCode.TooManyRequests, last);

        // Ada is somewhere else entirely, and her sign-in is not the stranger's to spend.
        using var ada = await SignInThroughAsync(app, Announced, "ada", from: "203.0.113.9");
        var session = await ada.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal("ada", session.GetProperty("user").GetProperty("username").GetString());
    }

    [Fact]
    public async Task A_tunnel_waiting_to_be_allowed_does_not_hold_the_host_offline()
    {
        const string Link = "https://login.tailscale.com/a/10692893011e9b";
        using var tunnels = OpenAwaitingSignIn("builtin", Link, "home.tail9f3a.ts.net");
        using var app = new TestHost(_config);

        // Everyone at home is waiting for their files while the administrator goes to find their
        // phone. Startup that blocked on them would take the household offline to do it.
        using var admin = await app.SignUpAsync("ada");
        var state = await admin.GetFromJsonAsync<JsonElement>("/api/remote-access");

        Assert.Equal("needs_sign_in", state.GetProperty("status").GetString());
        Assert.Equal(Link, state.GetProperty("signInUrl").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("hostname").ValueKind);
    }

    [Fact]
    public async Task Allowing_it_makes_the_host_answer_without_being_restarted()
    {
        const string Link = "https://login.tailscale.com/a/10692893011e9b";
        const string Announced = "home.tail9f3a.ts.net";
        using var tunnels = OpenAwaitingSignIn("builtin", Link, Announced);
        using var app = new TestHost(_config);
        using var admin = await app.SignUpAsync("ada");

        // Nobody has allowed it yet, so the name belongs to nobody.
        using (var early = Through(app, Announced))
            Assert.Equal(HttpStatusCode.Forbidden, (await early.GetAsync("/api/health")).StatusCode);

        tunnels.Latest!.Allow();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        JsonElement state;
        while ((state = await admin.GetFromJsonAsync<JsonElement>("/api/remote-access"))
                   .GetProperty("status").GetString() != "on"
               && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal("on", state.GetProperty("status").GetString());
        Assert.Equal($"https://{Announced}", state.GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("signInUrl").ValueKind);
        // Including where Dropbox returns the browser. Startup had no address to build that
        // from, and sending somebody back to a localhost they aren't sitting at is worse than
        // refusing them.
        Assert.Equal($"https://{Announced}", state.GetProperty("publicUrl").GetString());

        // And signing in through it works now, not after a restart: loopback is trusted as a
        // proxy because remote access is on, not because an address had already arrived.
        using var arriving = Through(app, Announced);
        var response = await arriving.PostAsJsonAsync("/api/session",
            new { username = "ada", password = TestHost.Password });

        response.EnsureSuccessStatusCode();
        Assert.Contains("secure", Assert.Single(response.Headers.GetValues("Set-Cookie")),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A host that was told nothing before launch, with a tunnel standing by for whoever asks.
    /// </summary>
    private Tunnels Available(params string[] addresses)
    {
        var tunnels = new Tunnels(null, addresses);
        var options = Read(new());
        RemoteAccess.Override = _ => new RemoteAccess(options, NullLogger.Instance, tunnels.Next);
        return tunnels;
    }

    [Fact]
    public async Task Remote_access_is_turned_on_from_the_panel_and_takes_effect_at_once()
    {
        const string Announced = "home.tail9f3a.ts.net";
        using var tunnels = Available(Announced);
        using var app = new TestHost(_config);
        using var admin = await app.SignUpAsync("ada");

        var before = await admin.GetFromJsonAsync<JsonElement>("/api/remote-access");
        Assert.Equal("off", before.GetProperty("status").GetString());
        Assert.True(before.GetProperty("canChange").GetBoolean());

        (await admin.PutAsJsonAsync("/api/remote-access", new { enabled = true })).EnsureSuccessStatusCode();
        var state = await Reachable(admin);

        Assert.Equal($"https://{Announced}", state.GetProperty("url").GetString());
        // Nothing was restarted, and the host answers to the new name straight away.
        using var arriving = Through(app, Announced);
        (await arriving.GetAsync("/api/health")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Turning_it_off_takes_the_way_in_with_it()
    {
        const string Announced = "home.tail9f3a.ts.net";
        using var tunnels = Available(Announced);
        using var app = new TestHost(_config);
        using var admin = await app.SignUpAsync("ada");
        (await admin.PutAsJsonAsync("/api/remote-access", new { enabled = true })).EnsureSuccessStatusCode();
        await Reachable(admin);

        var off = await admin.PutAsJsonAsync("/api/remote-access", new { enabled = false });
        off.EnsureSuccessStatusCode();

        Assert.Equal("off", (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        using var arriving = Through(app, Announced);
        Assert.Equal(HttpStatusCode.Forbidden, (await arriving.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task What_was_turned_on_is_still_on_after_a_restart()
    {
        const string Announced = "home.tail9f3a.ts.net";
        using (var tunnels = Available(Announced))
        using (var app = new TestHost(_config))
        using (var admin = await app.SignUpAsync("ada"))
        {
            (await admin.PutAsJsonAsync("/api/remote-access", new { enabled = true })).EnsureSuccessStatusCode();
            await Reachable(admin);
        }

        // The same host, started again. Nobody should have to say so twice.
        using var again = Available(Announced);
        using var restarted = new TestHost(_config);
        using var owner = await restarted.SignInAsync("ada");

        Assert.Equal($"https://{Announced}", (await Reachable(owner)).GetProperty("url").GetString());
    }

    [Fact]
    public async Task Saying_none_before_launch_keeps_it_off_whatever_was_remembered()
    {
        const string Announced = "home.tail9f3a.ts.net";
        // Turned on from the panel at some point, and remembered ever since.
        new ControlDatabase(_config).SetSetting(HostPaths.RemoteAccessSetting, "builtin");

        // Then somebody said none before launch, where the panel cannot answer back — and cannot
        // turn it off again either, so coming up reachable would be a door nobody could shut.
        using var tunnels = Open("none", Announced);
        using var app = new TestHost(_config);
        using var admin = await app.SignUpAsync("ada");

        await Task.Delay(250);
        var state = await admin.GetFromJsonAsync<JsonElement>("/api/remote-access");

        Assert.Equal("off", state.GetProperty("status").GetString());
        using var arriving = Through(app, Announced);
        Assert.Equal(HttpStatusCode.Forbidden, (await arriving.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task One_tunnel_is_watched_once_when_it_is_restored()
    {
        const string Announced = "home.tail9f3a.ts.net";
        new ControlDatabase(_config).SetSetting(HostPaths.RemoteAccessSetting, "builtin");
        using var tunnels = Available(Announced, Announced, Announced);
        using var app = new TestHost(_config);
        using var admin = await app.SignUpAsync("ada");
        await Reachable(admin);

        // The tunnel drops. One watcher opens one replacement; two would each open their own,
        // and whichever lost the race would carry traffic with nothing holding on to it.
        tunnels.Latest!.Drop();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (tunnels.Opened < 2 && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
        await Task.Delay(500);

        Assert.Equal(2, tunnels.Opened);
    }

    [Fact]
    public async Task Turning_it_off_while_it_waits_to_be_allowed_leaves_it_off()
    {
        const string Link = "https://login.tailscale.com/a/10692893011e9b";
        using var tunnels = new Tunnels(Link, "home.tail9f3a.ts.net");
        var options = Read(new());
        await using var remote = new RemoteAccess(options, NullLogger.Instance, tunnels.Next);

        remote.Start(5210, RemoteAccessProvider.Builtin);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (remote.State.Status != "needs_sign_in" && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);
        Assert.Equal("needs_sign_in", remote.State.Status);

        await remote.StopAsync();
        // The run that was cancelled mid-opening must not report "reconnecting" over this, or the
        // panel would show a tunnel forever trying and never offer the switch again.
        await Task.Delay(500);

        Assert.Equal("off", remote.State.Status);
        Assert.False(remote.IsEnabled);
        Assert.Null(remote.State.SignInUrl);
    }

    [Fact]
    public async Task A_provider_named_before_launch_is_not_the_panel_to_change()
    {
        const string Announced = "home.tail9f3a.ts.net";
        using var tunnels = Open("builtin", Announced);
        using var app = new TestHost(_config);
        using var admin = await app.SignUpAsync("ada");

        var state = await admin.GetFromJsonAsync<JsonElement>("/api/remote-access");
        Assert.False(state.GetProperty("canChange").GetBoolean());

        // Somebody said so before launch and meant it; a switch on a web page doesn't overrule it.
        var refused = await admin.PutAsJsonAsync("/api/remote-access", new { enabled = false });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    /// <summary>Waits for the panel to say a tunnel is carrying, as somebody watching it does.</summary>
    private static async Task<JsonElement> Reachable(HttpClient client)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        JsonElement state;
        while ((state = await client.GetFromJsonAsync<JsonElement>("/api/remote-access"))
                   .GetProperty("status").GetString() != "on"
               && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);
        Assert.Equal("on", state.GetProperty("status").GetString());
        return state;
    }

    [Fact]
    public async Task A_tunnel_can_be_turned_on_after_the_host_is_already_serving()
    {
        using var tunnels = new Tunnels(null, "first.tail9f3a.ts.net", "second.tail9f3a.ts.net");
        var options = Read(new() { ["Homebase:RemoteAccess:Provider"] = "none" });
        await using var remote = new RemoteAccess(options, NullLogger.Instance, tunnels.Next);

        // Nothing was asked for before launch, which is the ordinary case: somebody turns it on
        // later, from the interface, on a host everybody is already using.
        Assert.False(remote.IsEnabled);
        Assert.False(remote.IsCarrying);

        remote.Start(5210, RemoteAccessProvider.Builtin);
        Assert.Equal("first.tail9f3a.ts.net", await Settled(remote));
        Assert.True(remote.Answers("first.tail9f3a.ts.net"));

        // Turning it off takes the way in with it, at once rather than at the next restart.
        await remote.StopAsync();
        Assert.False(remote.IsEnabled);
        Assert.False(remote.Answers("first.tail9f3a.ts.net"));
        Assert.Equal("off", remote.State.Status);

        // And it can be turned on again, which the tunnel could not survive when a run's
        // cancellation belonged to the whole object rather than to the run.
        remote.Start(5210, RemoteAccessProvider.Builtin);
        Assert.Equal("second.tail9f3a.ts.net", await Settled(remote));
        Assert.True(remote.Answers("second.tail9f3a.ts.net"));
        Assert.False(remote.Answers("first.tail9f3a.ts.net"));
    }

    [Fact]
    public async Task Turning_it_on_twice_leaves_one_tunnel_running()
    {
        using var tunnels = new Tunnels(null, "first.tail9f3a.ts.net", "second.tail9f3a.ts.net");
        var options = Read(new() { ["Homebase:RemoteAccess:Provider"] = "none" });
        await using var remote = new RemoteAccess(options, NullLogger.Instance, tunnels.Next);

        remote.Start(5210, RemoteAccessProvider.Builtin);
        remote.Start(5210, RemoteAccessProvider.Builtin);
        Assert.Equal("first.tail9f3a.ts.net", await Settled(remote));

        // The second press found a run already going and left it alone. A tunnel nothing is
        // holding on to would go on carrying traffic after this one was stopped.
        Assert.Equal(1, tunnels.Opened);
    }

    /// <summary>Waits for a tunnel to settle on an address, as a person watching the panel does.</summary>
    private static async Task<string?> Settled(RemoteAccess remote)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (remote.State.Status != "on" && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
        return remote.State.Hostname;
    }

    [Fact]
    public void The_builtin_tunnel_is_the_one_uncloud_ships()
    {
        var options = Read(new() { ["Homebase:RemoteAccess:Provider"] = "builtin" });

        // Beside the application, so there is nothing to install and nothing to find on a PATH.
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows()
            ? "uncloud-tunnel.exe" : "uncloud-tunnel"), options.Executable);
        // And its identity is kept, so the allowing is asked for once rather than every start.
        Assert.Equal($"-target http://127.0.0.1:5210 -state \"{Path.Combine(_config, "tunnel")}\" "
            + "-hostname uncloud", options.ArgumentsFor(5210));
    }

    [Fact]
    public void A_name_asked_of_the_tailnet_is_not_an_answer_already()
    {
        // Under the builtin provider a hostname is what to call this node, not somewhere it can
        // already be reached. Taking it as an answer would report a tunnel nobody had allowed as
        // open, and bury the one link the administrator needs.
        Assert.False(Read(new()
        {
            ["Homebase:RemoteAccess:Provider"] = "builtin",
            ["Homebase:RemoteAccess:Hostname"] = "attic"
        }).AnnouncesNothing);

        // A Cloudflare tunnel named beforehand really does say nothing, and is the one exception.
        Assert.True(Read(new()
        {
            ["Homebase:RemoteAccess:Provider"] = "cloudflare",
            ["Homebase:RemoteAccess:Tunnel"] = "household",
            ["Homebase:RemoteAccess:Hostname"] = "files.example.com"
        }).AnnouncesNothing);
        Assert.False(Read(new() { ["Homebase:RemoteAccess:Provider"] = "cloudflare" }).AnnouncesNothing);
    }

    [Fact]
    public void Only_uncloud_own_tunnel_has_anybody_to_ask()
    {
        var builtin = Read(new() { ["Homebase:RemoteAccess:Provider"] = "builtin" });
        Assert.Equal("https://login.tailscale.com/a/abc",
            builtin.SignInPrompt!.Match("uncloud-tunnel: signin=https://login.tailscale.com/a/abc")
                .Groups[1].Value);
        // Exactly as tools/uncloud-tunnel prints it. Reading the scheme into the name would
        // leave Uncloud answering to something no browser ever sends as a Host.
        Assert.Equal("home.tail9f3a.ts.net",
            builtin.Announcement.Match("uncloud-tunnel: url=https://home.tail9f3a.ts.net").Groups[1].Value);
        Assert.Equal("home.tail9f3a.ts.net",
            builtin.Announcement.Match("uncloud-tunnel: url=https://home.tail9f3a.ts.net/").Groups[1].Value);

        // tailscale and cloudflared are signed in before Uncloud ever runs them.
        Assert.Null(Read(new() { ["Homebase:RemoteAccess:Provider"] = "tailscale" }).SignInPrompt);
        Assert.Null(Read(new() { ["Homebase:RemoteAccess:Provider"] = "cloudflare" }).SignInPrompt);
    }

    [Fact]
    public async Task A_tunnel_program_that_isnt_there_says_so_instead_of_starting()
    {
        var options = Read(new()
        {
            ["Homebase:RemoteAccess:Provider"] = "tailscale",
            ["Homebase:RemoteAccess:Command"] = "uncloud-no-such-tunnel-program"
        });
        RemoteAccess.Override = _ => new RemoteAccess(options, NullLogger.Instance);
        using var app = new TestHost(_config);

        // Refusing to start is the point: remote access was asked for, and a host that came up
        // without it would be quietly unreachable for everyone who is not on the network.
        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = app.Anonymous();
            await client.GetAsync("/api/health");
        });

        Assert.Contains("uncloud-no-such-tunnel-program", Flatten(failure));
    }

    [Fact]
    public void A_named_cloudflare_tunnel_has_to_be_told_the_name_it_answers_to()
    {
        var failure = Assert.Throws<LibraryException>(() => Read(new()
        {
            ["Homebase:RemoteAccess:Provider"] = "cloudflare",
            ["Homebase:RemoteAccess:Tunnel"] = "household"
        }));

        Assert.Equal("not_configured", failure.Code);
        Assert.Contains("Homebase__RemoteAccess__Hostname", failure.Message);
    }

    [Fact]
    public void A_hostname_is_taken_from_the_address_people_would_paste()
    {
        var options = Read(new()
        {
            ["Homebase:RemoteAccess:Provider"] = "cloudflare",
            ["Homebase:RemoteAccess:Tunnel"] = "household",
            ["Homebase:RemoteAccess:Hostname"] = "https://files.example.com/"
        });

        Assert.Equal("files.example.com", options.Hostname);
        // A named tunnel is run by name, and the port it is pointed at is this host's.
        Assert.Equal("tunnel --url http://127.0.0.1:5210 run household", options.ArgumentsFor(5210));
    }

    [Fact]
    public void Only_the_tunnel_service_owns_an_address_worth_believing()
    {
        var cloudflare = Read(new() { ["Homebase:RemoteAccess:Provider"] = "cloudflare" }).Announcement;

        // cloudflared prints a documentation link in the banner above the address it just made.
        Assert.DoesNotMatch(cloudflare,
            "INF Thank you for trying Cloudflare Tunnel ... https://developers.cloudflare.com/cloudflare-one/");
        Assert.Equal("shy-dog-42.trycloudflare.com",
            cloudflare.Match("INF |  https://shy-dog-42.trycloudflare.com  |").Groups[1].Value);

        var tailscale = Read(new() { ["Homebase:RemoteAccess:Provider"] = "tailscale" }).Announcement;
        Assert.DoesNotMatch(tailscale, "Visit https://tailscale.com/kb/1223/funnel to read more");
        Assert.Equal("home.tail9f3a.ts.net",
            tailscale.Match("Available on the internet:\thttps://home.tail9f3a.ts.net/").Groups[1].Value);
    }

    [Fact]
    public void Remote_access_is_off_until_it_is_asked_for()
    {
        Assert.False(Read(new()).IsEnabled);
        Assert.Throws<LibraryException>(() => Read(new() { ["Homebase:RemoteAccess:Provider"] = "ngrok" }));
    }

    private RemoteAccessOptions Read(Dictionary<string, string?> settings) =>
        RemoteAccessOptions.From(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), _config);

    /// <summary>Signs in through the tunnel as an account that was set up at the host itself.</summary>
    private static async Task<HttpClient> SignInThroughAsync(
        TestHost app, string hostname, string username, string from = "203.0.113.5")
    {
        var client = Through(app, hostname, from);
        var response = await client.PostAsJsonAsync("/api/session",
            new { username, password = TestHost.Password });
        response.EnsureSuccessStatusCode();
        KeepSession(client, response);
        return client;
    }

    private static string Flatten(Exception error)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? each = error; each is not null; each = each.InnerException) text.AppendLine(each.Message);
        return text.ToString();
    }

    public void Dispose()
    {
        RemoteAccess.Override = null;
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }

    /// <summary>Hands out one stand-in tunnel per opening, announcing the next address in turn.</summary>
    private sealed class Tunnels(string? signIn, params string[] addresses) : IDisposable
    {
        private readonly ConcurrentQueue<string> _addresses = new(addresses);
        private readonly List<Tunnel> _opened = [];

        public Tunnel? Latest { get { lock (_opened) return _opened.LastOrDefault(); } }

        public int Opened { get { lock (_opened) return _opened.Count; } }

        public ITunnel Next(RemoteAccessOptions options)
        {
            // The last address stands in for a tunnel service that keeps handing back the same
            // name, so a reconnect loop never runs out of them.
            if (!_addresses.TryDequeue(out var address))
                address = addresses.Length > 0
                    ? addresses[^1]
                    : throw new InvalidOperationException("This stand-in was given no address to announce.");
            Tunnel tunnel;
            lock (_opened)
            {
                // Only the first is asked to be allowed: a kept identity spares the rest, which
                // is the whole reason it is kept.
                tunnel = new Tunnel(address, _opened.Count == 0 ? signIn : null);
                _opened.Add(tunnel);
            }
            return tunnel;
        }

        public void Dispose()
        {
            lock (_opened) foreach (var tunnel in _opened) tunnel.Drop();
        }
    }

    private sealed class Tunnel(string address, string? signIn = null) : ITunnel
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Closed => _closed.Task;
        public Task<string> SignInRequired => _asked.Task;

        public Task<string> OpenAsync(int port, CancellationToken cancellationToken)
        {
            if (signIn is null) _opened.TrySetResult(address);
            else _asked.TrySetResult(signIn);
            return _opened.Task.WaitAsync(cancellationToken);
        }

        /// <summary>Stands in for the person following the link and allowing this host.</summary>
        public void Allow() => _opened.TrySetResult(address);

        public void Drop() => _closed.TrySetResult();
        public ValueTask DisposeAsync() { Drop(); return ValueTask.CompletedTask; }
    }
}
