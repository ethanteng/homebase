using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core.Accounts;
using Homebase.Core.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Homebase.Tests;

/// <summary>
/// One Uncloud host, started against a disposable preference directory. Every test that touches
/// the API goes through here, because nothing is reachable any more without signing in first.
/// </summary>
public sealed class TestHost : WebApplicationFactory<Program>
{
    public const string Password = "correct horse battery";

    private readonly Dictionary<string, string?> _settings;
    private readonly Func<string, IDropboxConnection>? _dropbox;

    static TestHost() =>
        // A real host hashes once a month; this suite signs in hundreds of times a run.
        PasswordHasher.Override = 1_000;

    public TestHost(
        string configDirectory,
        Func<string, IDropboxConnection>? dropbox = null,
        IDictionary<string, string?>? settings = null)
    {
        _settings = new Dictionary<string, string?> { ["Homebase:ConfigDirectory"] = configDirectory };
        if (settings is not null)
            foreach (var (key, value) in settings) _settings[key] = value;
        _dropbox = dropbox;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
        // TestServer leaves the connection's remote address unset, and forwarded headers are only
        // believed when they come from a known proxy, so without this there is no proxy to be.
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new CallerAddress(IPAddress.Loopback)));
        if (_dropbox is not null)
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IDropboxApiFactory>(new StubDropboxFactory(_dropbox)));
    }

    /// <summary>A client that isn't signed in, but does carry the header mutations require.</summary>
    public HttpClient Anonymous()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Homebase-Request", "1");
        return client;
    }

    /// <summary>
    /// A client that stops at a redirect instead of following it. A provider callback answers with
    /// one, and where it sends the browser is the thing worth looking at.
    /// </summary>
    public HttpClient NotFollowingRedirects()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Homebase-Request", "1");
        return client;
    }

    /// <summary>Creates the first account, which is this host's administrator, and signs in.</summary>
    public async Task<HttpClient> SignUpAsync(string username = "owner", string password = Password)
    {
        var client = Anonymous();
        var response = await client.PostAsJsonAsync("/api/setup",
            new { username, displayName = username, password });
        response.EnsureSuccessStatusCode();
        return client;
    }

    public async Task<HttpClient> SignInAsync(string username, string password = Password)
    {
        var client = Anonymous();
        var response = await client.PostAsJsonAsync("/api/session", new { username, password });
        response.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Adds an account as the administrator, then signs in as it.</summary>
    public async Task<HttpClient> AddUserAsync(HttpClient admin, string username, bool isAdmin = false)
    {
        var response = await admin.PostAsJsonAsync("/api/users",
            new { username, displayName = username, password = Password, isAdmin });
        response.EnsureSuccessStatusCode();
        return await SignInAsync(username);
    }

    public static async Task SetHostRootAsync(HttpClient admin, string path)
    {
        var response = await admin.PutAsJsonAsync("/api/host", new { path });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Where this client's own files live, which only the host gets to decide.</summary>
    public static async Task<string> UserRootAsync(HttpClient client)
    {
        var library = await client.GetFromJsonAsync<JsonElement>("/api/library");
        return library.GetProperty("rootPath").GetString()!;
    }

    private sealed class CallerAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, proceed) =>
            {
                context.Connection.RemoteIpAddress ??= address;
                await proceed(context);
            });
            next(builder);
        };
    }

    private sealed class StubDropboxFactory(Func<string, IDropboxConnection> forUser) : IDropboxApiFactory
    {
        public IDropboxConnection For(string userId) => forUser(userId);
        public void Forget(string userId) { }
        public void ForgetAll() { }
    }
}
