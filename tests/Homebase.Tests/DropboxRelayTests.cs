using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Homebase.Core;
using Homebase.Core.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Homebase.Tests;

/// <summary>
/// Connecting Dropbox without registering anything, through Uncloud's own Dropbox app.
///
/// The obstacle was never the flow: Dropbox will only return a sign-in to an address registered
/// with the app, and only somebody with a dropbox.com developer account can register the address of
/// a program on a stranger's computer. One app and one registered address, with a page there that
/// forwards the last hop, is what removes that step for everybody — and the hop is only safe
/// because of what the relay refuses to do, which is tested next to the relay itself.
/// </summary>
public sealed class DropboxRelayTests : IDisposable
{
    private const string RelayKey = "uncloud-own-app-key";

    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;

    public DropboxRelayTests()
    {
        Directory.CreateDirectory(_temporary);
        _host = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
    }

    /// <summary>
    /// A build that has Uncloud's own app key in it. Set through configuration rather than the
    /// constant, so the suite says what it depends on instead of going quiet if that is ever empty.
    /// </summary>
    private TestHost CreateApp(
        Func<string, IDropboxConnection>? dropbox = null,
        IPAddress? caller = null,
        params (string Key, string? Value)[] extra)
    {
        var settings = new Dictionary<string, string?> { ["Homebase:Dropbox:RelayAppKey"] = RelayKey };
        foreach (var (key, value) in extra) settings[key] = value;
        return new TestHost(_config, dropbox, settings, caller: caller);
    }

    private async Task<HttpClient> StartAsync(TestHost app)
    {
        var client = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(client, _host);
        return client;
    }

    private static async Task<Uri> AuthorizeUrlAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/providers/dropbox/connect", new { });
        response.EnsureSuccessStatusCode();
        return new Uri((await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("authorizeUrl").GetString()!);
    }

    private static string? Parameter(Uri authorize, string name) =>
        HttpUtility.ParseQueryString(authorize.Query)[name];

    [Fact]
    public async Task Dropbox_can_be_connected_by_somebody_who_has_set_nothing_up()
    {
        using var app = CreateApp();
        using var client = await StartAsync(app);

        // The whole point: a fresh Uncloud, nothing in the environment, nothing stored, nobody sent
        // to a developer console — and Dropbox is still offered.
        var panel = await client.GetFromJsonAsync<JsonElement>("/api/providers/dropbox");
        Assert.True(panel.GetProperty("configured").GetBoolean());

        var mine = await client.GetFromJsonAsync<JsonElement>("/api/account/dropbox");
        Assert.Equal("Relay", mine.GetProperty("source").GetString());
        // Nothing of their own is set, and the box they'd type it into stays empty.
        Assert.True(mine.GetProperty("appKey").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task A_sign_in_through_Uncloud_s_own_app_comes_back_by_way_of_the_relay()
    {
        using var app = CreateApp();
        using var client = await StartAsync(app);

        var authorize = await AuthorizeUrlAsync(client);

        Assert.Equal(RelayKey, Parameter(authorize, "client_id"));
        // The one address registered with that app. Anything else and Dropbox refuses the sign-in.
        Assert.Equal(DropboxRelay.CallbackUrl, Parameter(authorize, "redirect_uri"));
        // And the state carries the way home, because the relay is told nothing else about where
        // this host is: the scheme it answers on and the port it listens on, and no more than that.
        Assert.EndsWith(".h5210", Parameter(authorize, "state"));
        // Still PKCE, which is what makes a hop through somebody else's page safe at all: the
        // verifier stays here, so the code that passes through the relay cannot be spent there.
        Assert.Equal("S256", Parameter(authorize, "code_challenge_method"));
        Assert.False(string.IsNullOrWhiteSpace(Parameter(authorize, "code_challenge")));
    }

    [Fact]
    public async Task A_key_somebody_here_chose_is_used_instead_and_comes_straight_back()
    {
        using var app = CreateApp();
        using var client = await StartAsync(app);

        // Uncloud's own app is the floor, not the preference: an administrator who sets a key has
        // deliberately chosen a Dropbox app, and a default that quietly won would be a bug.
        var set = await client.PutAsJsonAsync("/api/host/dropbox", new { appKey = "the-hosts-key" });
        set.EnsureSuccessStatusCode();

        var mine = await client.GetFromJsonAsync<JsonElement>("/api/account/dropbox");
        Assert.Equal("Host", mine.GetProperty("source").GetString());

        var authorize = await AuthorizeUrlAsync(client);
        Assert.Equal("the-hosts-key", Parameter(authorize, "client_id"));
        // Their own app, their own registered address: no relay in it anywhere.
        Assert.Equal("http://localhost:5210/api/providers/dropbox/callback", Parameter(authorize, "redirect_uri"));
        Assert.DoesNotContain(".h", Parameter(authorize, "state"));
    }

    [Fact]
    public async Task An_account_s_own_key_still_beats_Uncloud_s_own()
    {
        using var app = CreateApp();
        using var client = await StartAsync(app);

        var set = await client.PutAsJsonAsync("/api/account/dropbox", new { appKey = "my-own-key" });
        set.EnsureSuccessStatusCode();
        Assert.Equal("Own", (await set.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("source").GetString());

        Assert.Equal("my-own-key", Parameter(await AuthorizeUrlAsync(client), "client_id"));
    }

    [Fact]
    public async Task The_exchange_presents_the_address_the_sign_in_actually_started_from()
    {
        // Dropbox requires the exchange to present the same redirect URI the sign-in was started
        // with. Which one this host would name is not fixed while a browser is away at dropbox.com:
        // an administrator setting a host key moves every account that had none from the relay's
        // address to this host's. Working it out again on the way back would then present the wrong
        // one and lose a sign-in the person completed correctly.
        var dropboxes = new StubDropboxes();
        using var app = CreateApp(dropbox: dropboxes.For);
        using var client = await StartAsync(app);

        var state = Assert.IsType<string>(Parameter(await AuthorizeUrlAsync(client), "state"));
        Assert.EndsWith(".h5210", state);
        var mine = Assert.Single(dropboxes.All);
        var began = mine.Key;

        // An administrator sets a host key while the sign-in is out at Dropbox. That moves this
        // account off the relay, so what this host would now name as its redirect URI changes —
        // and the key in force changes with it, which the stub stands in for here.
        (await client.PutAsJsonAsync("/api/host/dropbox", new { appKey = "arrived-mid-sign-in" }))
            .EnsureSuccessStatusCode();
        mine.Key = "arrived-mid-sign-in";

        using var callbacks = app.NotFollowingRedirects();
        var back = await callbacks.GetAsync(
            $"/api/providers/dropbox/callback?code=abc&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Found, back.StatusCode);

        var connected = Assert.Single(dropboxes.All, stub => stub.ExchangedWith is not null);
        Assert.Equal(DropboxRelay.CallbackUrl, connected.ExchangedWith);
        // And under the app it began under. Dropbox checks a code against the app it was issued to
        // as well as the address, so presenting the key that arrived mid-sign-in loses it just as
        // surely as presenting the wrong address would.
        Assert.Equal(began, connected.ExchangedUnder);
        Assert.NotEqual("arrived-mid-sign-in", connected.ExchangedUnder);
    }

    [Fact]
    public async Task A_host_serving_its_own_secure_address_signs_in_without_a_return_trip()
    {
        // The relay's last hop is a plain navigation to the loopback address. A certificate is
        // issued for the name people use, not usually for 127.0.0.1, so that hop would stop at a
        // certificate warning instead of arriving — and a certificate that does cover it cannot be
        // told apart from here, so this refuses either way rather than guessing.
        var certificate = Path.Combine(_temporary, "uncloud.pfx");
        await File.WriteAllBytesAsync(certificate, SelfSigned());
        using var app = CreateApp(extra: [("Homebase:Certificate:Path", certificate)]);
        using var client = await StartAsync(app);

        await AssertSignsInByHandAsync(client);
    }

    [Fact]
    public async Task A_host_behind_somebody_else_s_proxy_signs_in_without_a_return_trip()
    {
        // X-Forwarded-For is believed only from a tunnel Uncloud opened itself — a proxy somebody
        // else configured is taken at its word about scheme and host and nothing more. So the
        // address a request arrives from is the proxy's, and a proxy on this machine is loopback:
        // everybody behind it would read as sitting at the machine and be sent to their own.
        using var app = CreateApp(caller: IPAddress.Loopback,
            extra: [("Homebase:TrustedProxies", "127.0.0.1")]);
        using var client = await StartAsync(app);

        await AssertSignsInByHandAsync(client);
    }

    /// <summary>A certificate to point <c>Homebase:Certificate:Path</c> at; never used to serve.</summary>
    private static byte[] SelfSigned()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=uncloud.local", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx);
    }

    [Fact]
    public async Task A_browser_on_another_computer_signs_in_without_a_return_trip()
    {
        // The relay finishes by sending the browser to the loopback address, which is this host only
        // when the browser is on this computer. Somebody reaching Uncloud over a tunnel would be
        // sent to their own machine — nothing there, or worse, a different Uncloud. So they are not
        // sent anywhere: Dropbox is asked for no return trip and shows them the code instead.
        using var app = CreateApp(caller: IPAddress.Parse("192.168.1.50"),
            extra: [("Homebase:Bind", "0.0.0.0"), ("Homebase:AllowedHosts", "uncloud.local")]);
        using var client = await StartAsync(app);

        await AssertSignsInByHandAsync(client);

        // Somebody who would rather not paste anything sets a key of their own, and the sign-in
        // comes straight back to this host again — where they are sitting stops mattering.
        (await client.PutAsJsonAsync("/api/account/dropbox", new { appKey = "my-own-key" }))
            .EnsureSuccessStatusCode();
        var authorize = await AuthorizeUrlAsync(client);
        Assert.Equal("my-own-key", Parameter(authorize, "client_id"));
        Assert.NotNull(Parameter(authorize, "redirect_uri"));
    }

    /// <summary>
    /// Pressing Connect offers a sign-in with nowhere to come back to, which is what every reason
    /// the relay can't finish now leads to rather than a dead end.
    /// </summary>
    private static async Task AssertSignsInByHandAsync(HttpClient client)
    {
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/account/dropbox"))
            .GetProperty("relayReachable").GetBoolean());

        var started = await client.PostAsJsonAsync("/api/providers/dropbox/connect", new { });
        started.EnsureSuccessStatusCode();
        var body = await started.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("paste").GetBoolean());
        Assert.Null(Parameter(new Uri(body.GetProperty("authorizeUrl").GetString()!), "redirect_uri"));
    }

    [Fact]
    public async Task The_account_screen_knows_when_Uncloud_s_own_app_is_no_use_to_this_browser()
    {
        // The refusal above sends somebody to My account. That screen must not then tell them
        // there is nothing to set up, which is what it says to everybody else on the relay — being
        // turned away and reassured in the same breath is worse than either on its own.
        using var app = CreateApp(caller: IPAddress.Parse("192.168.1.50"),
            extra: [("Homebase:Bind", "0.0.0.0"), ("Homebase:AllowedHosts", "uncloud.local")]);
        using var client = await StartAsync(app);

        var mine = await client.GetFromJsonAsync<JsonElement>("/api/account/dropbox");

        Assert.Equal("Relay", mine.GetProperty("source").GetString());
        Assert.False(mine.GetProperty("relayReachable").GetBoolean());
    }

    [Fact]
    public async Task The_account_screen_says_the_relay_is_reachable_from_the_computer_it_runs_on()
    {
        using var app = CreateApp();
        using var client = await StartAsync(app);

        var mine = await client.GetFromJsonAsync<JsonElement>("/api/account/dropbox");

        Assert.Equal("Relay", mine.GetProperty("source").GetString());
        Assert.True(mine.GetProperty("relayReachable").GetBoolean());
    }

    [Fact]
    public async Task A_browser_the_relay_cannot_reach_is_given_a_sign_in_to_bring_back_by_hand()
    {
        // The relay can only hand a finished sign-in back at the loopback address, and every other
        // address Dropbox could return to has to be registered with the app in advance — which is
        // the step Uncloud's own app exists to remove. So ask Dropbox for no return trip at all: it
        // shows the code, and the person carries it. One paste, and it works from any computer.
        var dropboxes = new StubDropboxes();
        using var app = CreateApp(dropbox: dropboxes.For, caller: IPAddress.Parse("192.168.1.50"),
            extra: [("Homebase:Bind", "0.0.0.0"), ("Homebase:AllowedHosts", "uncloud.local")]);
        using var client = await StartAsync(app);

        var started = await client.PostAsJsonAsync("/api/providers/dropbox/connect", new { });
        started.EnsureSuccessStatusCode();
        var body = await started.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("paste").GetBoolean());

        var authorize = new Uri(body.GetProperty("authorizeUrl").GetString()!);
        // No address to come back to, and so no state either: state guards a callback, and there
        // is no callback to guard.
        Assert.Null(Parameter(authorize, "redirect_uri"));
        Assert.Null(Parameter(authorize, "state"));
        // Still PKCE, which is what keeps a code useless to anyone who did not start this sign-in.
        Assert.Equal("S256", Parameter(authorize, "code_challenge_method"));

        var finished = await client.PostAsJsonAsync(
            "/api/providers/dropbox/paste", new { code = "the-code-dropbox-showed" });
        finished.EnsureSuccessStatusCode();

        var connected = Assert.Single(dropboxes.All, stub => stub.ExchangedUnder is not null);
        Assert.Equal(connected.Key, connected.ExchangedUnder);
        // Exchanged for nothing, because it was issued for nothing: Dropbox checks the code against
        // the address it was given, and sending one now would lose the sign-in.
        Assert.Null(connected.ExchangedWith);
    }

    [Fact]
    public async Task A_pasted_code_is_refused_when_no_sign_in_is_waiting_for_one()
    {
        var dropboxes = new StubDropboxes();
        using var app = CreateApp(dropbox: dropboxes.For, caller: IPAddress.Parse("192.168.1.50"),
            extra: [("Homebase:Bind", "0.0.0.0"), ("Homebase:AllowedHosts", "uncloud.local")]);
        using var client = await StartAsync(app);

        // Nothing started, so there is no verifier to check a code against and nothing to connect.
        var cold = await client.PostAsJsonAsync("/api/providers/dropbox/paste", new { code = "abc" });
        Assert.Equal(HttpStatusCode.Conflict, cold.StatusCode);

        Assert.DoesNotContain(dropboxes.All, stub => stub.ExchangedUnder is not null);
    }

    [Fact]
    public async Task A_pasted_code_cannot_finish_a_sign_in_that_expected_to_come_back_on_its_own()
    {
        // That sign-in's code belongs to a redirect. Accepting one here would mean somebody could be
        // talked into carrying a code across from a sign-in they did not start.
        var dropboxes = new StubDropboxes();
        using var app = CreateApp(dropbox: dropboxes.For);
        using var client = await StartAsync(app);

        var redirecting = await client.PostAsJsonAsync("/api/providers/dropbox/connect", new { });
        redirecting.EnsureSuccessStatusCode();
        Assert.False((await redirecting.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("paste").GetBoolean());

        var refused = await client.PostAsJsonAsync("/api/providers/dropbox/paste", new { code = "abc" });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.DoesNotContain(dropboxes.All, stub => stub.ExchangedUnder is not null);
    }

    [Fact]
    public async Task A_mistyped_code_can_be_typed_again_without_starting_over()
    {
        // A code copied across by hand gets mistyped. Losing the sign-in over one wrong character
        // would send somebody back to Dropbox for nothing.
        var dropboxes = new StubDropboxes();
        using var app = CreateApp(dropbox: dropboxes.For, caller: IPAddress.Parse("192.168.1.50"),
            extra: [("Homebase:Bind", "0.0.0.0"), ("Homebase:AllowedHosts", "uncloud.local")]);
        using var client = await StartAsync(app);
        (await client.PostAsJsonAsync("/api/providers/dropbox/connect", new { })).EnsureSuccessStatusCode();

        // Dropbox refuses the first one, as it would a code with a character missing.
        Assert.Single(dropboxes.All).RefuseNext = true;
        var mistyped = await client.PostAsJsonAsync("/api/providers/dropbox/paste", new { code = "wrong" });
        Assert.Equal(HttpStatusCode.Conflict, mistyped.StatusCode);

        // The same sign-in is still standing, and the right code finishes it.
        (await client.PostAsJsonAsync("/api/providers/dropbox/paste", new { code = "right" }))
            .EnsureSuccessStatusCode();
        Assert.Contains(dropboxes.All, stub => stub.ExchangedUnder is not null);
    }

    [Fact]
    public async Task A_pasted_code_is_good_once()
    {
        var dropboxes = new StubDropboxes();
        using var app = CreateApp(dropbox: dropboxes.For, caller: IPAddress.Parse("192.168.1.50"),
            extra: [("Homebase:Bind", "0.0.0.0"), ("Homebase:AllowedHosts", "uncloud.local")]);
        using var client = await StartAsync(app);

        (await client.PostAsJsonAsync("/api/providers/dropbox/connect", new { })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/providers/dropbox/paste", new { code = "abc" }))
            .EnsureSuccessStatusCode();

        // The verifier is gone with it, so the same code cannot be spent again.
        var again = await client.PostAsJsonAsync("/api/providers/dropbox/paste", new { code = "abc" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task A_build_with_no_key_of_its_own_behaves_as_though_none_of_this_existed()
    {
        // The key is empty in a build nobody has set one for, and that has to be the old behaviour
        // exactly rather than a half-configured relay: no source, no offer, and the same refusal
        // with the same thing to do about it.
        using var app = new TestHost(_config, settings: new Dictionary<string, string?>
        {
            ["Homebase:Dropbox:RelayAppKey"] = ""
        });
        using var client = await StartAsync(app);

        var mine = await client.GetFromJsonAsync<JsonElement>("/api/account/dropbox");
        Assert.Equal("None", mine.GetProperty("source").GetString());
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/providers/dropbox"))
            .GetProperty("configured").GetBoolean());

        var refused = await client.PostAsJsonAsync("/api/providers/dropbox/connect", new { });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("app key", (await refused.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("detail").GetString());
    }

    [Fact]
    public void The_way_home_is_a_port_and_a_scheme_and_nothing_a_relay_could_be_sent_by()
    {
        // Deliberately not an address. The relay builds one around the loopback host, so the most a
        // state can ask for is a port on the person's own computer — a whole address here would be
        // an open redirect wearing Uncloud's consent screen.
        var state = DropboxOAuth.CreateVerifier();

        Assert.Equal($"{state}.h5210", DropboxRelay.StateWithWayBack(state, secure: false, 5210));
        Assert.Equal($"{state}.s8443", DropboxRelay.StateWithWayBack(state, secure: true, 8443));
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }
}
