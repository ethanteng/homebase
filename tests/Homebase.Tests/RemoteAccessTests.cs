using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
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
    private static Tunnels Open(string provider, params string[] addresses)
    {
        var tunnels = new Tunnels(addresses);
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

    private static RemoteAccessOptions Read(Dictionary<string, string?> settings) =>
        RemoteAccessOptions.From(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

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
    private sealed class Tunnels(params string[] addresses) : IDisposable
    {
        private readonly ConcurrentQueue<string> _addresses = new(addresses);
        private readonly List<Tunnel> _opened = [];

        public Tunnel? Latest { get { lock (_opened) return _opened.LastOrDefault(); } }

        public ITunnel Next(RemoteAccessOptions options)
        {
            // The last address stands in for a tunnel service that keeps handing back the same
            // name, so a reconnect loop never runs out of them.
            if (!_addresses.TryDequeue(out var address)) address = addresses[^1];
            var tunnel = new Tunnel(address);
            lock (_opened) _opened.Add(tunnel);
            return tunnel;
        }

        public void Dispose()
        {
            lock (_opened) foreach (var tunnel in _opened) tunnel.Drop();
        }
    }

    private sealed class Tunnel(string address) : ITunnel
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Closed => _closed.Task;
        public Task<string> OpenAsync(int port, CancellationToken cancellationToken) => Task.FromResult(address);
        public void Drop() => _closed.TrySetResult();
        public ValueTask DisposeAsync() { Drop(); return ValueTask.CompletedTask; }
    }
}
