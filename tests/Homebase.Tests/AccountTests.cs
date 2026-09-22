using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Server;
using Microsoft.Extensions.Configuration;

namespace Homebase.Tests;

public sealed class AccountTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;

    public AccountTests()
    {
        Directory.CreateDirectory(_temporary);
        // Resolved the way the host will resolve it: on macOS /var is a link into /private/var,
        // so a raw temporary path never equals the one the server answers with.
        _host = PathPolicy.NormalizeRoot(
            Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
    }

    private TestHost CreateApp() => new(_config);

    private async Task<HttpClient> OwnerAsync(TestHost app)
    {
        var admin = await app.SignUpAsync("ada");
        await TestHost.SetHostRootAsync(admin, _host);
        return admin;
    }

    [Fact]
    public async Task The_first_account_is_the_administrator_and_setup_closes_behind_it()
    {
        using var app = CreateApp();
        using var admin = await app.SignUpAsync("ada");

        var session = await admin.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.False(session.GetProperty("setupNeeded").GetBoolean());
        Assert.True(session.GetProperty("user").GetProperty("isAdmin").GetBoolean());

        // Nobody can make themselves a second administrator through the open setup door.
        using var stranger = app.Anonymous();
        var response = await stranger.PostAsJsonAsync("/api/setup",
            new { username = "mallory", password = TestHost.Password });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Signing_in_needs_the_right_password_and_says_nothing_about_who_exists()
    {
        using var app = CreateApp();
        using var admin = await OwnerAsync(app);
        using var client = app.Anonymous();

        var wrongPassword = await client.PostAsJsonAsync("/api/session", new { username = "ada", password = "not it at all" });
        var noSuchUser = await client.PostAsJsonAsync("/api/session", new { username = "nobody", password = "not it at all" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noSuchUser.StatusCode);
        Assert.Equal(await Detail(wrongPassword), await Detail(noSuchUser));
    }

    [Fact]
    public async Task Repeated_wrong_passwords_are_refused_before_they_become_guessing()
    {
        using var app = CreateApp();
        using var admin = await OwnerAsync(app);
        using var client = app.Anonymous();

        HttpResponseMessage last = null!;
        for (var attempt = 0; attempt < 11; attempt++)
            last = await client.PostAsJsonAsync("/api/session", new { username = "ada", password = "wrong one here" });

        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        // And the right password is refused too while the window is open, rather than letting a
        // guesser interleave attempts against an account they happen to know the password for.
        var correct = await client.PostAsJsonAsync("/api/session", new { username = "ada", password = TestHost.Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, correct.StatusCode);
    }

    [Fact]
    public async Task Signing_out_ends_the_session_it_was_made_with()
    {
        using var app = CreateApp();
        using var admin = await OwnerAsync(app);
        (await admin.GetAsync("/api/files")).EnsureSuccessStatusCode();

        (await admin.DeleteAsync("/api/session")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/files")).StatusCode);
    }

    [Fact]
    public async Task Changing_a_password_needs_the_old_one_and_signs_out_everywhere_else()
    {
        using var app = CreateApp();
        using var here = await OwnerAsync(app);
        using var elsewhere = await app.SignInAsync("ada");
        (await elsewhere.GetAsync("/api/files")).EnsureSuccessStatusCode();

        var guessed = await here.PostAsJsonAsync("/api/account/password",
            new { currentPassword = "not the old one", newPassword = "a whole new password" });
        Assert.Equal(HttpStatusCode.Unauthorized, guessed.StatusCode);

        var tooShort = await here.PostAsJsonAsync("/api/account/password",
            new { currentPassword = TestHost.Password, newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        (await here.PostAsJsonAsync("/api/account/password",
            new { currentPassword = TestHost.Password, newPassword = "a whole new password" })).EnsureSuccessStatusCode();

        // The browser that made the change stays signed in; every other one doesn't.
        (await here.GetAsync("/api/files")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/files")).StatusCode);
        using var again = await app.SignInAsync("ada", "a whole new password");
        (await again.GetAsync("/api/files")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task An_administrator_adds_an_account_and_it_gets_a_folder_of_its_own()
    {
        using var app = CreateApp();
        using var admin = await OwnerAsync(app);
        using var member = await app.AddUserAsync(admin, "bo");

        var root = await TestHost.UserRootAsync(member);
        Assert.True(Directory.Exists(root));
        Assert.False((await member.GetFromJsonAsync<JsonElement>("/api/session"))
            .GetProperty("user").GetProperty("isAdmin").GetBoolean());

        var listed = await admin.GetFromJsonAsync<JsonElement[]>("/api/users");
        Assert.Equal(["ada", "bo"], listed!.Select(user => user.GetProperty("username").GetString()));
    }

    [Fact]
    public async Task A_username_is_taken_once_and_a_weak_password_is_refused()
    {
        using var app = CreateApp();
        using var admin = await OwnerAsync(app);
        using var _ = await app.AddUserAsync(admin, "bo");

        var duplicate = await admin.PostAsJsonAsync("/api/users",
            new { username = "BO", password = TestHost.Password });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var weak = await admin.PostAsJsonAsync("/api/users", new { username = "cy", password = "hunter2" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var odd = await admin.PostAsJsonAsync("/api/users",
            new { username = "../escape", password = TestHost.Password });
        Assert.Equal(HttpStatusCode.BadRequest, odd.StatusCode);
    }

    [Fact]
    public async Task Disabling_an_account_signs_it_out_at_once_and_deleting_it_keeps_its_files()
    {
        using var app = CreateApp();
        using var admin = await OwnerAsync(app);
        using var member = await app.AddUserAsync(admin, "bo");
        var root = await TestHost.UserRootAsync(member);
        await File.WriteAllTextAsync(Path.Combine(root, "bo-taxes.txt"), "Bo's taxes.");
        var id = (await admin.GetFromJsonAsync<JsonElement[]>("/api/users"))!
            .Single(user => user.GetProperty("username").GetString() == "bo").GetProperty("id").GetString();

        (await admin.PatchAsJsonAsync($"/api/users/{id}", new { disabled = true })).EnsureSuccessStatusCode();

        // Not at the next expiry: now.
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/files")).StatusCode);
        using var refused = app.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await refused.PostAsJsonAsync("/api/session", new { username = "bo", password = TestHost.Password })).StatusCode);

        var deleted = await admin.DeleteAsync($"/api/users/{id}");
        deleted.EnsureSuccessStatusCode();
        // Somebody's files are not Uncloud's to throw away along with their account.
        Assert.Equal("Bo's taxes.", await File.ReadAllTextAsync(Path.Combine(root, "bo-taxes.txt")));
        Assert.Contains(id!, (await deleted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("filesRemainAt").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_last_administrator_cannot_be_removed_disabled_or_demoted()
    {
        using var app = CreateApp();
        using var admin = await OwnerAsync(app);
        using var member = await app.AddUserAsync(admin, "bo");
        var users = (await admin.GetFromJsonAsync<JsonElement[]>("/api/users"))!;
        var adminId = users.Single(user => user.GetProperty("username").GetString() == "ada").GetProperty("id").GetString();
        var memberId = users.Single(user => user.GetProperty("username").GetString() == "bo").GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/users/{adminId}", new { isAdmin = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/users/{adminId}", new { disabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/users/{adminId}")).StatusCode);

        // With somebody else holding the keys, stepping down is allowed.
        (await admin.PatchAsJsonAsync($"/api/users/{memberId}", new { isAdmin = true })).EnsureSuccessStatusCode();
        (await admin.PatchAsJsonAsync($"/api/users/{adminId}", new { isAdmin = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task A_v0_library_is_adopted_as_the_host_folder_and_its_files_can_be_claimed()
    {
        // What an upgrade finds: a v0 root recorded in settings.json, with files sitting in it.
        await File.WriteAllTextAsync(Path.Combine(_host, "old-notes.txt"), "From before.");
        Directory.CreateDirectory(Path.Combine(_host, "Photos"));
        await new SettingsStore(_config).SaveAsync(_host, CancellationToken.None);

        using var app = CreateApp();
        using var admin = await app.SignUpAsync("ada");

        // The folder is already chosen, so an upgrade doesn't land on the setup screen.
        Assert.Equal(_host, (await admin.GetFromJsonAsync<JsonElement>("/api/host")).GetProperty("rootPath").GetString());
        var root = await TestHost.UserRootAsync(admin);
        // Nothing was moved without being asked, so the account's folder starts empty.
        Assert.Empty((await admin.GetFromJsonAsync<DirectoryListing>("/api/files"))!.Entries);
        // Listed by name; the file browser is the thing that puts folders first.
        Assert.Equal(["old-notes.txt", "Photos"],
            (await admin.GetFromJsonAsync<JsonElement>("/api/host/unclaimed")).GetProperty("entries")
                .EnumerateArray().Select(entry => entry.GetString()));

        var claimed = await admin.PostAsync("/api/host/unclaimed", null);
        claimed.EnsureSuccessStatusCode();

        Assert.Equal("From before.", await File.ReadAllTextAsync(Path.Combine(root, "old-notes.txt")));
        Assert.Equal(["Photos", "old-notes.txt"],
            (await admin.GetFromJsonAsync<DirectoryListing>("/api/files"))!.Entries.Select(entry => entry.Name));
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>("/api/host/unclaimed"))
            .GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task Claiming_never_overwrites_something_already_there()
    {
        await File.WriteAllTextAsync(Path.Combine(_host, "notes.txt"), "The older one.");
        await new SettingsStore(_config).SaveAsync(_host, CancellationToken.None);
        using var app = CreateApp();
        using var admin = await app.SignUpAsync("ada");
        var root = await TestHost.UserRootAsync(admin);
        await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "The one I'm using.");

        var response = await admin.PostAsync("/api/host/unclaimed", null);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(result.GetProperty("moved").EnumerateArray());
        Assert.Equal("notes.txt", result.GetProperty("skipped").EnumerateArray().Single().GetProperty("name").GetString());
        Assert.Equal("The one I'm using.", await File.ReadAllTextAsync(Path.Combine(root, "notes.txt")));
        Assert.Equal("The older one.", await File.ReadAllTextAsync(Path.Combine(_host, "notes.txt")));
    }

    [Fact]
    public void Listening_beyond_this_computer_requires_saying_which_names_are_allowed()
    {
        var open = Configuration(new Dictionary<string, string?> { ["Homebase:Bind"] = "0.0.0.0" });
        var error = Assert.Throws<LibraryException>(() => HostBinding.From(open));
        Assert.Contains("Homebase__AllowedHosts", error.Message, StringComparison.Ordinal);

        var named = HostBinding.From(Configuration(new Dictionary<string, string?>
        {
            ["Homebase:Bind"] = "0.0.0.0", ["Homebase:AllowedHosts"] = "uncloud.local, 192.168.1.10"
        }));
        Assert.Contains("uncloud.local", named.AllowedHosts);
        // Loopback always answers, so an administrator at the machine is never locked out.
        Assert.Contains("127.0.0.1", named.AllowedHosts);
        Assert.NotNull(named.Warning);

        // The default is unchanged: this computer only, and nothing to configure.
        var local = HostBinding.From(Configuration(new Dictionary<string, string?>()));
        Assert.True(local.IsLoopback);
        Assert.Null(local.Warning);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static async Task<string?> Detail(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString();

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }
}
