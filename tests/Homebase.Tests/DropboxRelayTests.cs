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

    private static string Parameter(Uri authorize, string name) =>
        HttpUtility.ParseQueryString(authorize.Query)[name]!;

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

        var state = Parameter(await AuthorizeUrlAsync(client), "state");
        Assert.EndsWith(".h5210", state);

        // The key in force changes while the sign-in is out at Dropbox.
        (await client.PutAsJsonAsync("/api/host/dropbox", new { appKey = "arrived-mid-sign-in" }))
            .EnsureSuccessStatusCode();

        using var callbacks = app.NotFollowingRedirects();
        var back = await callbacks.GetAsync(
            $"/api/providers/dropbox/callback?code=abc&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Found, back.StatusCode);

        var connected = Assert.Single(dropboxes.All, stub => stub.ExchangedWith is not null);
        Assert.Equal(DropboxRelay.CallbackUrl, connected.ExchangedWith);
    }

    [Fact]
    public async Task Uncloud_s_own_app_is_not_offered_to_a_browser_on_another_computer()
    {
        // The relay finishes by sending the browser to the loopback address, which is this host only
        // when the browser is on this computer. Somebody reaching Uncloud over a tunnel would be
        // sent to their own machine — nothing there, or worse, a different Uncloud. Saying so beats
        // sending them somewhere that cannot work.
        using var app = CreateApp(caller: IPAddress.Parse("192.168.1.50"),
            extra: [("Homebase:Bind", "0.0.0.0"), ("Homebase:AllowedHosts", "uncloud.local")]);
        using var client = await StartAsync(app);

        var refused = await client.PostAsJsonAsync("/api/providers/dropbox/connect", new { });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var detail = (await refused.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("detail").GetString()!;
        // And told the way round it, which is a thing they can do without anyone's help.
        Assert.Contains("your own Dropbox app", detail);

        // That key is theirs to set, and once set the sign-in comes straight back to this host, so
        // where they are sitting stops mattering.
        (await client.PutAsJsonAsync("/api/account/dropbox", new { appKey = "my-own-key" }))
            .EnsureSuccessStatusCode();
        Assert.Equal("my-own-key", Parameter(await AuthorizeUrlAsync(client), "client_id"));
    }

    [Fact]
    public async Task A_build_with_no_key_of_its_own_behaves_as_though_none_of_this_existed()
    {
        // The key is empty in a build nobody has set one for, and that has to be the old behaviour
        // exactly rather than a half-configured relay: no source, no offer, and the same refusal
        // with the same thing to do about it.
        using var app = new TestHost(_config);
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
