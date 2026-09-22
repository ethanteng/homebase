using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Core.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Homebase.Tests;

/// <summary>
/// Setting Dropbox up from inside Uncloud, rather than by restarting it from a terminal with an
/// environment variable set. These run against the real Dropbox client, not the stub, because what
/// is being tested is where the app key comes from.
/// </summary>
public sealed class DropboxSetupTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;

    public DropboxSetupTests()
    {
        Directory.CreateDirectory(_temporary);
        _host = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
    }

    private TestHost CreateApp(string? environmentKey = null) => new(_config, settings: environmentKey is null
        ? null
        : new Dictionary<string, string?> { ["Homebase:Dropbox:AppKey"] = environmentKey });

    private async Task<HttpClient> StartAsync(TestHost app)
    {
        var client = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(client, _host);
        return client;
    }

    [Fact]
    public async Task An_administrator_sets_the_app_key_in_Uncloud_and_Dropbox_becomes_connectable()
    {
        using var app = CreateApp();
        using var client = await StartAsync(app);

        // Nothing in the environment and nothing stored: there is no Dropbox to connect to yet,
        // and the panel is told so rather than offering a button that cannot work.
        var before = await client.GetFromJsonAsync<JsonElement>("/api/providers/dropbox");
        Assert.False(before.GetProperty("configured").GetBoolean());
        Assert.True(before.GetProperty("canConfigure").GetBoolean());

        (await client.PutAsJsonAsync("/api/host/dropbox", new { appKey = "  typed-in-key  " }))
            .EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<JsonElement>("/api/providers/dropbox");
        Assert.True(after.GetProperty("configured").GetBoolean());
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/host/dropbox");
        // Trimmed, because an app key copied out of a browser usually arrives with whitespace.
        Assert.Equal("typed-in-key", settings.GetProperty("appKey").GetString());
        Assert.False(settings.GetProperty("fromEnvironment").GetBoolean());

        // Connecting now gets as far as Dropbox's own sign-in page, carrying that key and a
        // challenge — the half of the flow that doesn't need Dropbox to answer.
        var connect = await client.PostAsJsonAsync("/api/providers/dropbox/connect", new { });
        connect.EnsureSuccessStatusCode();
        var url = (await connect.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizeUrl").GetString()!;
        Assert.StartsWith(DropboxOAuth.AuthorizeEndpoint, url);
        Assert.Contains("client_id=typed-in-key", url);
        Assert.Contains("code_challenge_method=S256", url);
        // Read-only and lasting: what the app asks for is not the caller's to change.
        Assert.Contains("token_access_type=offline", url);
    }

    [Fact]
    public async Task The_environment_variable_still_works_and_a_stored_key_replaces_it()
    {
        using var app = CreateApp("from-the-environment");
        using var client = await StartAsync(app);

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/host/dropbox");
        Assert.True(settings.GetProperty("configured").GetBoolean());
        Assert.True(settings.GetProperty("fromEnvironment").GetBoolean());
        // Nothing to prefill the box with: it isn't Uncloud's to show back as its own setting.
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("appKey").ValueKind);

        (await client.PutAsJsonAsync("/api/host/dropbox", new { appKey = "typed-in-key" }))
            .EnsureSuccessStatusCode();

        var replaced = await client.GetFromJsonAsync<JsonElement>("/api/host/dropbox");
        Assert.False(replaced.GetProperty("fromEnvironment").GetBoolean());
        Assert.Equal("typed-in-key", replaced.GetProperty("appKey").GetString());

        // Cleared, it falls back to the environment rather than to nothing.
        (await client.PutAsJsonAsync("/api/host/dropbox", new { appKey = "" })).EnsureSuccessStatusCode();
        var cleared = await client.GetFromJsonAsync<JsonElement>("/api/host/dropbox");
        Assert.True(cleared.GetProperty("fromEnvironment").GetBoolean());
        Assert.True(cleared.GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Changing_the_app_key_signs_out_the_connections_it_would_have_broken()
    {
        using var app = CreateApp();
        using var client = await StartAsync(app);
        (await client.PutAsJsonAsync("/api/host/dropbox", new { appKey = "first-key" })).EnsureSuccessStatusCode();

        // Two accounts with a connection each, authorised through the app "first-key" names.
        var connectors = app.Services.GetRequiredService<ConnectorStore>();
        foreach (var account in app.Services.GetRequiredService<UserStore>().List())
            connectors.Save(account.Id, DropboxApi.ProviderName, "refresh-token", "Their Dropbox");
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/providers/dropbox"))
            .GetProperty("connected").GetBoolean());

        var response = await client.PutAsJsonAsync("/api/host/dropbox", new { appKey = "second-key" });
        response.EnsureSuccessStatusCode();

        // A refresh token issued to one app cannot be refreshed against another, so leaving them
        // would only fail later, on a background refresh nobody is watching.
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("disconnected").GetInt32());
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/providers/dropbox"))
            .GetProperty("connected").GetBoolean());

        // Saving the same key again is not a change, so nobody is signed out for nothing.
        var again = await client.PutAsJsonAsync("/api/host/dropbox", new { appKey = "second-key" });
        Assert.Equal(0, (await again.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("disconnected").GetInt32());
    }

    [Fact]
    public async Task Changing_the_app_key_also_stops_the_clients_already_holding_a_token()
    {
        // Deleting the stored refresh tokens is not enough on its own. A Dropbox client that has
        // already been used holds a live access token and answers from it without going back to
        // the refresh token, so an account signed out on paper could keep reading Dropbox until
        // that token expired — and a running import would carry on downloading. Disconnecting the
        // client is what clears that token, so this watches for the disconnect itself.
        var stub = new StubDropbox();
        using var app = new TestHost(_config, _ => stub);
        using var client = await StartAsync(app);
        (await client.PutAsJsonAsync("/api/host/dropbox", new { appKey = "first-key" })).EnsureSuccessStatusCode();

        var workspaces = app.Services.GetRequiredService<UserWorkspaces>();
        var account = app.Services.GetRequiredService<UserStore>().List().Single();
        app.Services.GetRequiredService<ConnectorStore>()
            .Save(account.Id, DropboxApi.ProviderName, "refresh-token", "Their Dropbox");
        // Built and used, which is what leaves a client cached behind the workspace.
        var before = workspaces.For(account.Id);
        Assert.True(stub.IsConnected);

        (await client.PutAsJsonAsync("/api/host/dropbox", new { appKey = "second-key" }))
            .EnsureSuccessStatusCode();

        // The client the workspace was holding was disconnected rather than left with its token,
        // and the next request builds a fresh workspace rather than handing back the old one.
        Assert.False(stub.IsConnected);
        Assert.NotSame(before, workspaces.For(account.Id));
    }

    [Fact]
    public async Task The_redirect_address_to_register_is_the_one_Uncloud_will_actually_use()
    {
        using var app = new TestHost(_config, settings: new Dictionary<string, string?>
        {
            ["Homebase:PublicUrl"] = "https://uncloud.local/",
            ["Homebase:AllowedHosts"] = "uncloud.local"
        });
        using var client = await StartAsync(app);

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/host/dropbox");

        // Shown so it can be copied rather than typed from the README and got wrong; a redirect
        // URI Dropbox doesn't hold exactly refuses the sign-in.
        Assert.Equal("https://uncloud.local/api/providers/dropbox/callback",
            settings.GetProperty("redirectUri").GetString());
        Assert.Equal(["account_info.read", "files.metadata.read", "files.content.read"],
            settings.GetProperty("scopes").EnumerateArray().Select(scope => scope.GetString()));
    }

    [Fact]
    public async Task A_sign_in_comes_back_to_the_address_it_started_from()
    {
        // 127.0.0.1 and localhost are one computer but two cookie jars, so returning to the wrong
        // one used to read as being signed out at the moment the connection succeeded.
        using var app = CreateApp("app-key");
        using var client = await StartAsync(app);

        // Pressed while looking at http://127.0.0.1, which is not the address Dropbox comes back to.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/providers/dropbox/connect");
        request.Headers.Host = "127.0.0.1";
        var connect = await client.SendAsync(request);
        connect.EnsureSuccessStatusCode();
        var url = (await connect.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizeUrl").GetString()!;
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;

        using var callbacks = app.NotFollowingRedirects();
        var denied = await callbacks.GetAsync(
            $"/api/providers/dropbox/callback?error=access_denied&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Found, denied.StatusCode);
        Assert.Equal("http://127.0.0.1/?dropbox=denied", denied.Headers.Location?.ToString());
    }

    [Fact]
    public async Task A_state_Uncloud_never_issued_lands_back_on_Uncloud_and_nothing_else()
    {
        using var app = CreateApp("app-key");
        using var client = await StartAsync(app);
        using var callbacks = app.NotFollowingRedirects();

        // The return address is carried by the state, so a forged one has none to offer. It has to
        // fall back to a relative hop rather than becoming a way to send somebody off this host.
        foreach (var state in new[] { "made-up", "" })
        {
            var response = await callbacks.GetAsync(
                $"/api/providers/dropbox/callback?code=abc&state={Uri.EscapeDataString(state)}");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal("/?dropbox=failed", response.Headers.Location?.ToString());
        }
    }

    [Fact]
    public async Task Only_an_administrator_sets_up_the_hosts_Dropbox_app()
    {
        using var app = CreateApp();
        using var admin = await StartAsync(app);
        using var member = await app.AddUserAsync(admin, "bo");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/host/dropbox")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PutAsJsonAsync("/api/host/dropbox", new { appKey = "mine" })).StatusCode);

        // What a member is told is that it isn't set up and that it isn't theirs to set up.
        var status = await member.GetFromJsonAsync<JsonElement>("/api/providers/dropbox");
        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.False(status.GetProperty("canConfigure").GetBoolean());
    }

    [Fact]
    public async Task Connecting_without_an_app_key_is_refused_with_something_to_do_about_it()
    {
        using var app = CreateApp();
        using var client = await StartAsync(app);

        var response = await client.PostAsJsonAsync("/api/providers/dropbox/connect", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("app key", problem.GetProperty("detail").GetString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }
}
