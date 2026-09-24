using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Accounts;
using Microsoft.Data.Sqlite;

namespace Homebase.Tests;

/// <summary>
/// The races and deployment shapes that a single request never shows. Each of these fails on the
/// code as it stood before the fix beside it.
/// </summary>
public sealed class HardeningTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;

    public HardeningTests()
    {
        Directory.CreateDirectory(_temporary);
        _host = PathPolicy.NormalizeRoot(
            Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
    }

    [Fact]
    public async Task The_first_administrator_cannot_be_created_beside_another()
    {
        var database = new ControlDatabase(_config);
        var store = new UserStore(database);

        // Somebody else is part-way through claiming this host and holds the write lock.
        using var other = database.Open();
        using var claim = other.BeginTransaction(deferred: false);
        using (var insert = other.CreateCommand())
        {
            insert.Transaction = claim;
            insert.CommandText = """
                INSERT INTO users(id, username, display_name, password_hash, is_admin, created_at, disabled_at)
                VALUES ('first', 'ada', 'Ada', 'x', 1, $created, NULL)
                """;
            insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }

        var racing = Task.Run(() => store.CreateFirstAdmin("bo", "Bo", TestHost.Password));
        // Long enough for the second claim to have reached the database and be waiting there.
        await Task.Delay(250);
        claim.Commit();

        // Today a split check would probably still be safe by accident, because opening a
        // connection runs the schema DDL and takes the write lock. That is not a property to
        // rest an admin account on, and it goes away the moment opening is made cheaper.
        var refused = await Assert.ThrowsAsync<LibraryException>(() => racing);
        Assert.Equal("conflict", refused.Code);
        Assert.Single(store.List());
    }

    [Fact]
    public async Task Two_administrators_stepping_down_at_once_cannot_leave_the_host_with_none()
    {
        using var app = new TestHost(_config);
        using var first = await app.SignUpAsync("ada");
        await TestHost.SetHostRootAsync(first, _host);
        using var second = await app.AddUserAsync(first, "bo", isAdmin: true);
        var users = (await first.GetFromJsonAsync<JsonElement[]>("/api/users"))!;
        var ada = users.Single(user => user.GetProperty("username").GetString() == "ada").GetProperty("id").GetString();
        var bo = users.Single(user => user.GetProperty("username").GetString() == "bo").GetProperty("id").GetString();

        // Each demotes the other at the same moment; separately, both checks would pass.
        await Task.WhenAll(
            first.PatchAsJsonAsync($"/api/users/{bo}", new { isAdmin = false }),
            second.PatchAsJsonAsync($"/api/users/{ada}", new { isAdmin = false }));

        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_config, "homebase.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users WHERE is_admin = 1 AND disabled_at IS NULL";
        Assert.True(Convert.ToInt32(command.ExecuteScalar()) >= 1,
            "Both demotions went through and nobody can look after this Uncloud any more.");
    }

    [Fact]
    public async Task A_session_still_being_used_has_its_cookie_pushed_out_too()
    {
        using var app = new TestHost(_config);
        using var client = await app.SignUpAsync("ada");
        await TestHost.SetHostRootAsync(client, _host);

        // A month of daily use, arrived at directly: the stored expiry is a day and a bit old.
        var before = DateTimeOffset.UtcNow.AddDays(28);
        using (var connection = new SqliteConnection(
                   $"Data Source={Path.Combine(_config, "homebase.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET expires_at = $expires";
            command.Parameters.AddWithValue("$expires", before.ToString("O"));
            command.ExecuteNonQuery();
        }

        var response = await client.GetAsync("/api/library");

        response.EnsureSuccessStatusCode();
        // Extending only the row would leave the browser discarding a session in active use.
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("uncloud_session=", StringComparison.Ordinal));
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Revoking_an_account_stops_the_import_it_had_running()
    {
        var dropbox = new StubDropbox { Gated = true };
        dropbox.AddFolder("/work");
        dropbox.Add("/work/first.txt", "rev1", "First.");
        dropbox.Add("/work/second.txt", "rev1", "Second.");
        using var app = new TestHost(_config, _ => dropbox);
        using var admin = await app.SignUpAsync("ada");
        await TestHost.SetHostRootAsync(admin, _host);
        using var member = await app.AddUserAsync(admin, "bo");
        var root = await TestHost.UserRootAsync(member);
        var id = (await admin.GetFromJsonAsync<JsonElement[]>("/api/users"))!
            .Single(user => user.GetProperty("username").GetString() == "bo").GetProperty("id").GetString();

        (await member.PostAsJsonAsync("/api/imports", new { remotePath = "/work" })).EnsureSuccessStatusCode();
        await dropbox.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Access is taken away mid-import, then the download that was in flight is let through.
        (await admin.PatchAsJsonAsync($"/api/users/{id}", new { disabled = true })).EnsureSuccessStatusCode();
        dropbox.Gate.TrySetResult();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && dropbox.Downloaded.Count < 1) await Task.Delay(15);
        await Task.Delay(250);
        // Whatever arrived stays, as with any stopped import; nothing new is fetched afterwards.
        Assert.DoesNotContain("/work/second.txt", dropbox.Downloaded);
        Assert.False(File.Exists(Path.Combine(root, "Dropbox", "work", "second.txt")),
            "The import kept writing into a folder whose owner had just been signed out for good.");
    }

    [Fact]
    public async Task A_proxy_that_terminates_tls_is_believed_only_when_it_is_named()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Homebase:Bind"] = "0.0.0.0",
            ["Homebase:AllowedHosts"] = "uncloud.local"
        };

        // Without a named proxy, Uncloud sees plain HTTP and refuses the browser's https Origin,
        // which would break every sign-in behind the reverse proxy the README describes.
        using (var bare = new TestHost(_config, settings: settings))
        using (var client = Proxied(bare))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/setup",
                new { username = "ada", password = TestHost.Password })).StatusCode);
        }

        settings["Homebase:TrustedProxies"] = "127.0.0.1";
        using var app = new TestHost(_config, settings: settings);
        using var proxied = Proxied(app);

        var response = await proxied.PostAsJsonAsync("/api/setup",
            new { username = "ada", password = TestHost.Password });

        response.EnsureSuccessStatusCode();
        // And the scheme the proxy reported is the one the cookie is issued for.
        Assert.Contains("secure", Assert.Single(response.Headers.GetValues("Set-Cookie")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_proxy_cannot_be_told_who_its_client_is()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Homebase:Bind"] = "0.0.0.0",
            ["Homebase:AllowedHosts"] = "uncloud.local",
            ["Homebase:TrustedProxies"] = "127.0.0.1"
        };
        using var app = new TestHost(_config, settings: settings);
        using var owner = await app.SignUpAsync("ada");

        // A proxy is named to be believed about the scheme and the host, and about nothing else.
        // Were a forwarded client address taken from it as well, anybody reaching Uncloud through
        // it could invent a new one per attempt and never meet the sign-in throttle at all.
        using var guessing = Proxied(app);
        HttpStatusCode last = default;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            guessing.DefaultRequestHeaders.Remove("X-Forwarded-For");
            guessing.DefaultRequestHeaders.Add("X-Forwarded-For", $"203.0.113.{attempt + 1}");
            var refused = await guessing.PostAsJsonAsync("/api/session",
                new { username = $"guess{attempt}", password = "not the password" });
            last = refused.StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    /// <summary>A request as a TLS-terminating reverse proxy would pass it on.</summary>
    private static HttpClient Proxied(TestHost app)
    {
        var client = app.Anonymous();
        client.DefaultRequestHeaders.Host = "uncloud.local";
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "uncloud.local");
        client.DefaultRequestHeaders.Add("Origin", "https://uncloud.local");
        return client;
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }
}
