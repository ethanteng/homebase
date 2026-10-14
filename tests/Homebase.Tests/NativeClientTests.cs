using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Homebase.Tests;

/// <summary>
/// The iPhone app talks to the host the way the clients here do: no Origin, X-Homebase-Request on
/// every request, and the session cookie's value sent back by hand rather than kept in a cookie jar
/// (docs/ios.md). A change to the host that would shut it out fails here, not on somebody's phone.
/// </summary>
public sealed class NativeClientTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;

    public NativeClientTests()
    {
        Directory.CreateDirectory(_temporary);
        _host = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporary)) Directory.Delete(_temporary, recursive: true);
    }

    /// <summary>A client like the app: it keeps no cookies, and says which requests are its own.</summary>
    private static HttpClient App(TestHost host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Homebase-Request", "1");
        return client;
    }

    private static string SessionToken(HttpResponseMessage response)
    {
        const string name = "uncloud_session=";
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(name, StringComparison.Ordinal));
        return cookie[name.Length..].Split(';')[0];
    }

    [Fact]
    public async Task An_app_that_sends_its_own_session_cookie_signs_in_browses_downloads_and_signs_out()
    {
        using var host = new TestHost(_config);
        using var browser = await host.SignUpAsync();
        await TestHost.SetHostRootAsync(browser, _host);
        var root = await TestHost.UserRootAsync(browser);
        Directory.CreateDirectory(Path.Combine(root, "C++ notes"));
        await File.WriteAllTextAsync(Path.Combine(root, "C++ notes", "a+b = c.txt"), "plus");

        using var app = App(host);
        var signIn = await app.PostAsJsonAsync("/api/session", new { username = "owner", password = TestHost.Password });
        signIn.EnsureSuccessStatusCode();
        var token = SessionToken(signIn);
        Assert.False(string.IsNullOrEmpty(token));

        // Keeping no cookies, it is nobody until it sends the token itself.
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("/api/files?path=")).StatusCode);
        app.DefaultRequestHeaders.Add("Cookie", $"uncloud_session={token}");
        var session = await app.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal("owner", session.GetProperty("user").GetProperty("username").GetString());

        // A path with + in it, sent as %2B, which is how the app sends every +.
        var listing = await app.GetFromJsonAsync<JsonElement>("/api/files?path=C%2B%2B%20notes");
        Assert.Equal("C++ notes", listing.GetProperty("path").GetString());
        var entry = Assert.Single(listing.GetProperty("entries").EnumerateArray());
        Assert.Equal("a+b = c.txt", entry.GetProperty("name").GetString());
        Assert.Equal(4, entry.GetProperty("size").GetInt64());
        // The shape of date the app reads: up to seven digits after the seconds, and an offset.
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d{1,7})?(Z|[+-]\d\d:\d\d)$",
            entry.GetProperty("modifiedAt").GetString());
        // Sent as it is, a + reads as a space, and the folder isn't found.
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync("/api/files?path=C++%20notes")).StatusCode);

        var download = await app.GetAsync("/api/files/download?path=C%2B%2B%20notes/a%2Bb%20%3D%20c.txt");
        download.EnsureSuccessStatusCode();
        Assert.Equal("plus", await download.Content.ReadAsStringAsync());

        (await app.DeleteAsync("/api/session")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("/api/files?path=")).StatusCode);
    }

    [Fact]
    public async Task An_app_still_has_to_say_a_change_is_its_own()
    {
        using var host = new TestHost(_config);
        using var browser = await host.SignUpAsync();
        using var unmarked = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await unmarked.PostAsJsonAsync("/api/session", new { username = "owner", password = TestHost.Password });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Refusals_come_with_words_the_app_shows_as_they_are()
    {
        using var host = new TestHost(_config);
        using var browser = await host.SignUpAsync();
        using var app = App(host);

        var wrong = await app.PostAsJsonAsync("/api/session", new { username = "owner", password = "not the password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var problem = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("That username and password don’t match.", problem.GetProperty("detail").GetString());

        // What an older host answers, signed in, for something it doesn't have yet, which the app reads
        // as the host needing an update.
        var missing = await browser.GetAsync("/api/not-yet-a-thing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        problem = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("This endpoint doesn’t exist.", problem.GetProperty("detail").GetString());
    }
}
