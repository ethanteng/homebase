using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Core.Sync;

namespace Homebase.Tests;

/// <summary>
/// The promise the whole design rests on: one account cannot reach another's files, by any
/// route the API offers. These are the tests to break before trusting anything else here.
/// </summary>
public sealed class IsolationTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;
    private readonly StubDropboxes _dropbox = new();
    private readonly FakeSyncthing _syncthing = new();

    public IsolationTests()
    {
        Directory.CreateDirectory(_temporary);
        // Resolved the way the host will resolve it: on macOS /var is a link into /private/var,
        // so a raw temporary path never equals the one the server answers with.
        _host = PathPolicy.NormalizeRoot(
            Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
    }

    private TestHost CreateApp() => new(_config, _dropbox.For, syncthing: _syncthing);

    /// <summary>An administrator and an ordinary member, each with a file of their own.</summary>
    private async Task<(HttpClient Admin, string AdminRoot, HttpClient Member, string MemberRoot)> TwoAccountsAsync(TestHost app)
    {
        var admin = await app.SignUpAsync("ada");
        await TestHost.SetHostRootAsync(admin, _host);
        var adminRoot = await TestHost.UserRootAsync(admin);
        var member = await app.AddUserAsync(admin, "bo");
        var memberRoot = await TestHost.UserRootAsync(member);
        await File.WriteAllTextAsync(Path.Combine(adminRoot, "ada-diary.txt"), "Ada's diary.");
        await File.WriteAllTextAsync(Path.Combine(memberRoot, "bo-taxes.txt"), "Bo's taxes.");
        return (admin, adminRoot, member, memberRoot);
    }

    [Fact]
    public async Task Each_account_gets_its_own_folder_and_sees_only_its_own_files()
    {
        using var app = CreateApp();
        var (admin, adminRoot, member, memberRoot) = await TwoAccountsAsync(app);
        using var _ = admin;
        using var __ = member;

        Assert.NotEqual(adminRoot, memberRoot);
        Assert.Equal(Path.Combine(_host, UserPaths.UsersDirectory), Path.GetDirectoryName(adminRoot));
        Assert.Equal(Path.Combine(_host, UserPaths.UsersDirectory), Path.GetDirectoryName(memberRoot));

        Assert.Equal("ada-diary.txt",
            Assert.Single((await admin.GetFromJsonAsync<DirectoryListing>("/api/files"))!.Entries).Name);
        Assert.Equal("bo-taxes.txt",
            Assert.Single((await member.GetFromJsonAsync<DirectoryListing>("/api/files"))!.Entries).Name);
    }

    [Fact]
    public async Task One_account_cannot_browse_or_download_another_by_naming_its_path()
    {
        using var app = CreateApp();
        var (admin, adminRoot, member, _) = await TwoAccountsAsync(app);
        using var ___ = admin;
        using var ____ = member;

        var adminId = Path.GetFileName(adminRoot);
        string[] attempts =
        [
            $"../{adminId}",
            $"../{adminId}/ada-diary.txt",
            $"../../users/{adminId}/ada-diary.txt",
            adminRoot,
            Path.Combine(adminRoot, "ada-diary.txt"),
            $"..\\{adminId}",
            "..",
            "../"
        ];
        foreach (var attempt in attempts)
        foreach (var endpoint in new[] { "/api/files", "/api/files/download" })
        {
            var response = await member.GetAsync($"{endpoint}?path={Uri.EscapeDataString(attempt)}");
            Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
                $"{endpoint} answered {(int)response.StatusCode} for “{attempt}”");
            if (response.Content.Headers.ContentLength is > 0)
                Assert.DoesNotContain("Ada's diary", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        // And the administrator's file is still exactly where it was.
        Assert.Equal("Ada's diary.", await File.ReadAllTextAsync(Path.Combine(adminRoot, "ada-diary.txt")));
    }

    [Fact]
    public async Task A_member_cannot_reach_the_hosts_own_settings_or_the_other_accounts()
    {
        using var app = CreateApp();
        var (admin, _, member, __) = await TwoAccountsAsync(app);
        using var ___ = admin;
        using var ____ = member;

        foreach (var endpoint in new[] { "/api/host", "/api/host/unclaimed", "/api/users" })
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(endpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PutAsJsonAsync("/api/host", new { path = _host })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync("/api/users", new { username = "sneak", password = TestHost.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync("/api/folder-picker", null)).StatusCode);

        // A member's own view never names the host's folder, only their own.
        var library = await member.GetFromJsonAsync<JsonElement>("/api/library");
        Assert.Equal(JsonValueKind.Null, library.GetProperty("hostRoot").ValueKind);
        Assert.False(library.GetProperty("canPickFolder").GetBoolean());
    }

    [Fact]
    public async Task Nothing_is_readable_without_signing_in()
    {
        using var app = CreateApp();
        var (admin, _, __, ___) = await TwoAccountsAsync(app);
        using var ____ = admin;
        using var client = app.Anonymous();

        string[] endpoints =
        [
            "/api/library", "/api/files", "/api/files/download?path=x", "/api/storage",
            "/api/imports", "/api/imports/job", "/api/providers/dropbox", "/api/providers/dropbox/files",
            "/api/host", "/api/users", "/api/sync"
        ];
        foreach (var endpoint in endpoints)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(endpoint)).StatusCode);

        // Health and the sign-in itself stay open, or nobody could ever get in.
        (await client.GetAsync("/api/health")).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/session")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_provider_connection_belongs_to_one_account_and_imports_land_in_its_own_folder()
    {
        using var app = CreateApp();
        var (admin, adminRoot, member, memberRoot) = await TwoAccountsAsync(app);
        using var _ = admin;
        using var __ = member;

        var adminId = Path.GetFileName(adminRoot);
        var memberId = Path.GetFileName(memberRoot);
        _dropbox.For(adminId).Add("/work/report.txt", "rev1", "Ada's report.");
        _dropbox.For(memberId).IsConnected = false;

        // Each account's panel reports its own connection, not the host's.
        Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/providers/dropbox"))
            .GetProperty("connected").GetBoolean());
        Assert.False((await member.GetFromJsonAsync<JsonElement>("/api/providers/dropbox"))
            .GetProperty("connected").GetBoolean());

        (await admin.PostAsJsonAsync("/api/imports", new { remotePath = "/work/report.txt" })).EnsureSuccessStatusCode();
        await Settled(admin);

        Assert.Equal("Ada's report.",
            await File.ReadAllTextAsync(Path.Combine(adminRoot, "Files", "Dropbox", "work", "report.txt")));
        Assert.False(Directory.Exists(Path.Combine(memberRoot, "Files")));
        // The import log is the account's own too, kept in the account's own database.
        Assert.Single(await admin.GetFromJsonAsync<JsonElement[]>("/api/imports") ?? []);
        Assert.Empty(await member.GetFromJsonAsync<JsonElement[]>("/api/imports") ?? []);
    }

    [Fact]
    public async Task One_accounts_import_does_not_hold_up_anothers()
    {
        using var app = CreateApp();
        var (admin, adminRoot, member, memberRoot) = await TwoAccountsAsync(app);
        using var _ = admin;
        using var __ = member;
        _dropbox.For(Path.GetFileName(adminRoot)).Add("/a.txt", "rev1", "A");
        _dropbox.For(Path.GetFileName(memberRoot)).Add("/b.txt", "rev1", "B");

        (await admin.PostAsJsonAsync("/api/imports", new { remotePath = "/a.txt" })).EnsureSuccessStatusCode();
        // "Uncloud is already bringing files home" is about your own import, not the host's.
        (await member.PostAsJsonAsync("/api/imports", new { remotePath = "/b.txt" })).EnsureSuccessStatusCode();
        await Settled(admin);
        await Settled(member);

        Assert.True(File.Exists(Path.Combine(adminRoot, "Files", "Dropbox", "a.txt")));
        Assert.True(File.Exists(Path.Combine(memberRoot, "Files", "Dropbox", "b.txt")));
    }

    [Fact]
    public void A_sealed_token_moved_to_another_account_is_refused_rather_than_shared()
    {
        var protector = new SecretProtector(_config);
        var sealedToken = protector.Seal("refresh-token", "ada:dropbox");

        Assert.Equal("refresh-token", protector.Open(sealedToken, "ada:dropbox"));
        // Lifted into another account's row, or another provider's, it decrypts to nothing.
        Assert.Null(protector.Open(sealedToken, "bo:dropbox"));
        Assert.Null(protector.Open(sealedToken, "ada:googledrive"));
        sealedToken[^1] ^= 0xFF;
        Assert.Null(protector.Open(sealedToken, "ada:dropbox"));
    }

    [Fact]
    public async Task Storage_shows_the_shared_volume_but_only_the_callers_own_usage()
    {
        using var app = CreateApp();
        var (admin, adminRoot, member, memberRoot) = await TwoAccountsAsync(app);
        using var _ = admin;
        using var __ = member;
        await File.WriteAllTextAsync(Path.Combine(memberRoot, "big.txt"), new string('x', 50_000));

        var forAdmin = await admin.GetFromJsonAsync<JsonElement>("/api/storage");
        var forMember = await member.GetFromJsonAsync<JsonElement>("/api/storage");

        // One pool: the same drive, reported the same way to both.
        Assert.Equal(forAdmin.GetProperty("totalBytes").GetInt64(), forMember.GetProperty("totalBytes").GetInt64());
        // Their own share of it is their own.
        Assert.True(forMember.GetProperty("usedBytes").GetInt64() >= 50_000);
        Assert.True(forAdmin.GetProperty("usedBytes").GetInt64() < 50_000);
        Assert.Equal("Ada's diary.", await File.ReadAllTextAsync(Path.Combine(adminRoot, "ada-diary.txt")));
    }

    private static async Task Settled(HttpClient client)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = (await client.GetFromJsonAsync<JsonElement>("/api/imports/job")).GetProperty("job");
            if (job.ValueKind != JsonValueKind.Null && !job.GetProperty("running").GetBoolean()) return;
            await Task.Delay(15);
        }
        throw new TimeoutException("The import never finished.");
    }

    [Fact]
    public async Task One_account_can_neither_see_nor_use_anothers_computers_or_synced_folders()
    {
        const string Laptop = "ZZZZZZZ-YYYYYYY-XXXXXXX-WWWWWWW-VVVVVVV-UUUUUUU-TTTTTTT-SSSSSSS";
        using var app = CreateApp();
        var (admin, adminRoot, member, _) = await TwoAccountsAsync(app);
        using var ___ = admin;
        using var ____ = member;
        Directory.CreateDirectory(Path.Combine(adminRoot, "Diaries"));
        (await admin.PostAsJsonAsync("/api/sync/devices", new { deviceId = Laptop, name = "Ada’s laptop" })).EnsureSuccessStatusCode();
        var shared = await (await admin.PostAsJsonAsync("/api/sync/folders", new { path = "Diaries" })).Content.ReadFromJsonAsync<JsonElement>();
        var folderId = shared.GetProperty("id").GetString()!;
        _syncthing.Offers.Add(new SyncthingOffer("ada-photos", "Photos", Laptop));

        var seen = await member.GetFromJsonAsync<JsonElement>("/api/sync");
        Assert.Empty(seen.GetProperty("devices").EnumerateArray());
        Assert.Empty(seen.GetProperty("folders").EnumerateArray());
        Assert.Empty(seen.GetProperty("offers").EnumerateArray());
        Assert.DoesNotContain(Laptop, seen.GetRawText());

        Assert.Equal(HttpStatusCode.NotFound, (await member.DeleteAsync($"/api/sync/devices/{Laptop}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.DeleteAsync($"/api/sync/folders/{folderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.PostAsJsonAsync("/api/sync/folders",
            new { path = "", deviceIds = new[] { Laptop } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.PostAsJsonAsync("/api/sync/folders/accept",
            new { folderId = "ada-photos", path = "Photos" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await member.PostAsJsonAsync("/api/sync/devices",
            new { deviceId = Laptop })).StatusCode);

        // Everything the administrator set up is exactly as it was.
        Assert.Equal([Laptop], _syncthing.Folders[folderId].DeviceIds);
        Assert.Equal(Path.Combine(adminRoot, "Diaries"), _syncthing.Folders[folderId].Path);
        Assert.Single(_syncthing.Folders);
    }

    [Fact]
    public async Task Disabling_or_deleting_an_account_stops_its_computers_syncing()
    {
        const string Desktop = "QQQQQQQ-RRRRRRR-SSSSSSS-TTTTTTT-UUUUUUU-VVVVVVV-WWWWWWW-XXXXXXX";
        using var app = CreateApp();
        var (admin, _, member, memberRoot) = await TwoAccountsAsync(app);
        using var ___ = admin;
        using var ____ = member;
        var memberId = Path.GetFileName(memberRoot);
        (await member.PostAsJsonAsync("/api/sync/devices", new { deviceId = Desktop })).EnsureSuccessStatusCode();
        (await member.PostAsJsonAsync("/api/sync/folders", new { path = "" })).EnsureSuccessStatusCode();

        (await admin.PatchAsJsonAsync($"/api/users/{memberId}", new { disabled = true })).EnsureSuccessStatusCode();
        Assert.True(_syncthing.Devices[Desktop].Paused);
        (await admin.PatchAsJsonAsync($"/api/users/{memberId}", new { disabled = false })).EnsureSuccessStatusCode();
        Assert.False(_syncthing.Devices[Desktop].Paused);

        (await admin.DeleteAsync($"/api/users/{memberId}")).EnsureSuccessStatusCode();
        Assert.Empty(_syncthing.Devices);
        Assert.Empty(_syncthing.Folders);
        Assert.Equal("Bo's taxes.", await File.ReadAllTextAsync(Path.Combine(memberRoot, "bo-taxes.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }
}
