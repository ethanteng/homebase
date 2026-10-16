using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Core.Backup;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homebase.Tests;

/// <summary>
/// A host joining Uncloud Backup: an invite spent for a credential, a password made here, storage
/// asked for until the household's is set up, every secret sealed with host.key, and backups going
/// where the service says without a restart. The service is a stand-in answering as
/// services/backup does.
/// </summary>
public sealed class BackupAccountTests : IDisposable
{
    private const string Invite = "K7QM-3XPD-9TWA-HF2R";
    private static readonly string Code = "^[2-9A-HJ-NP-Z]{5}(-[2-9A-HJ-NP-Z]{5}){5}$";

    private readonly string _temporary = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"))).FullName;

    private string Config => Path.Combine(_temporary, "config");

    private BackupAccount Account(FakeBackupService service, string? config = null) =>
        new(new ControlDatabase(config ?? Config), new SecretProtector(config ?? Config),
            new BackupServiceClient(new HttpClient(service), new Uri("https://backup.test/")),
            NullLogger<BackupAccount>.Instance);

    /// <summary>Everything the host's database holds on disk, journal included.</summary>
    private string Kept() => string.Concat(Directory.GetFiles(Config, "homebase.db*")
        .Select(file => Encoding.UTF8.GetString(File.ReadAllBytes(file))));

    [Fact]
    public async Task Joining_spends_the_invite_for_a_credential_and_makes_a_password_and_storage_follows()
    {
        var service = new FakeBackupService { Pending = 2 };
        var account = Account(service);

        var joined = await account.JoinAsync($"  {Invite} ", "Mac mini", CancellationToken.None);

        var registering = Assert.Single(service.Requests);
        Assert.Equal("/api/hosts", registering.Path);
        Assert.Equal(Invite, registering.Body.GetProperty("invite").GetString());
        Assert.Equal("Mac mini", registering.Body.GetProperty("name").GetString());
        Assert.Matches(Code, joined.Password);
        Assert.True(account.Joined);
        Assert.Null(account.Target);
        Assert.Equal("setting_up", account.Status.Storage);
        Assert.Equal("household-1", account.Status.Household);

        // Asked as often as it takes: twice told it's still being set up, then given it.
        Assert.Null((await account.RefreshStorageAsync(CancellationToken.None)).Storage);
        Assert.Null((await account.RefreshStorageAsync(CancellationToken.None)).Storage);
        var answer = await account.RefreshStorageAsync(CancellationToken.None);

        Assert.NotNull(answer.Storage);
        Assert.All(service.Requests.Skip(1), request =>
        {
            Assert.Equal("/api/host/storage", request.Path);
            Assert.Equal($"Bearer {service.Credential}", request.Authorization);
        });
        Assert.Equal(4, service.Requests.Count);
        Assert.Equal(new BackupTarget(
            "s3:https://s3.example/uncloud-household-1", joined.Password, "AKFAKE1", service.Secrets[0], "us-east-1"), account.Target);
        Assert.Equal("ready", account.Status.Storage);
        Assert.Equal("uncloud-household-1", account.Status.Bucket);
        // Kept sealed: none of the three secrets is anywhere in the database as it is.
        var kept = Kept();
        Assert.DoesNotContain(service.Credential!, kept);
        Assert.DoesNotContain(joined.Password, kept);
        Assert.DoesNotContain(service.Secrets[0], kept);
    }

    [Fact]
    public async Task What_the_host_kept_is_no_use_without_its_host_key()
    {
        var service = new FakeBackupService();
        var account = Account(service);
        await account.JoinAsync(Invite, "Mac mini", CancellationToken.None);
        await account.RefreshStorageAsync(CancellationToken.None);
        Assert.NotNull(account.Target);

        // The database alone, as somebody who took a copy of it without host.key would have it.
        var elsewhere = Path.Combine(_temporary, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        foreach (var file in Directory.GetFiles(Config, "homebase.db*"))
            File.Copy(file, Path.Combine(elsewhere, Path.GetFileName(file)));

        var copied = Account(service, elsewhere);
        Assert.True(copied.Joined);
        Assert.Null(copied.Target);
        var refused = await Assert.ThrowsAsync<LibraryException>(() => copied.RefreshStorageAsync(CancellationToken.None));
        Assert.Equal("host_key", refused.Code);
    }

    [Fact]
    public async Task An_invite_the_service_turns_down_is_said_in_its_words_and_joins_nothing()
    {
        var service = new FakeBackupService();
        var account = Account(service);

        var refused = await Assert.ThrowsAsync<LibraryException>(() => account.JoinAsync("AAAA-AAAA-AAAA-AAAA", null, CancellationToken.None));
        var blank = await Assert.ThrowsAsync<LibraryException>(() => account.JoinAsync("  ", null, CancellationToken.None));

        Assert.Equal("invalid_invite", refused.Code);
        Assert.Equal(FakeBackupService.InvalidInvite, refused.Message);
        Assert.Equal("invalid_invite", blank.Code);
        Assert.False(account.Joined);
        Assert.Single(service.Requests);
    }

    [Fact]
    public async Task A_host_joins_once()
    {
        var service = new FakeBackupService();
        var account = Account(service);
        await account.JoinAsync(Invite, "Mac mini", CancellationToken.None);

        var again = await Assert.ThrowsAsync<LibraryException>(() => account.JoinAsync(Invite, "Mac mini", CancellationToken.None));

        Assert.Equal("conflict", again.Code);
        Assert.Single(service.Requests);
    }

    [Fact]
    public async Task Storage_still_being_set_up_says_how_far_it_has_got_and_when_to_ask_again()
    {
        var service = new FakeBackupService { Pending = int.MaxValue, RetryAfter = TimeSpan.FromSeconds(5) };
        var account = Account(service);
        await account.JoinAsync(Invite, "Mac mini", CancellationToken.None);

        var answer = await account.RefreshStorageAsync(CancellationToken.None);

        Assert.Null(answer.Storage);
        Assert.Equal(("setting_up", "storage"), (answer.State, answer.Step));
        Assert.Equal(TimeSpan.FromSeconds(5), answer.RetryAfter);
        Assert.Equal(2, service.Requests.Count);
        Assert.Equal("storage", account.Status.Step);
    }

    [Fact]
    public async Task A_service_out_of_reach_is_said_plainly()
    {
        var account = Account(new FakeBackupService { Unreachable = true });

        var refused = await Assert.ThrowsAsync<LibraryException>(() => account.JoinAsync(Invite, null, CancellationToken.None));

        Assert.Equal("unavailable", refused.Code);
        Assert.Contains("couldn’t reach Uncloud Backup", refused.Message);
        Assert.False(account.Joined);
    }

    [Fact]
    public async Task Backups_go_where_they_are_sent_once_there_is_somewhere_without_a_restart()
    {
        var database = new ControlDatabase(Config);
        var root = Directory.CreateDirectory(Path.Combine(_temporary, "root")).FullName;
        var host = new HostService(database, initial: root);
        Directory.CreateDirectory(Path.Combine(host.RootPath!, UserPaths.UsersDirectory, "alice"));
        File.WriteAllText(Path.Combine(host.RootPath!, UserPaths.UsersDirectory, "alice", "notes.txt"), "remember the milk");
        BackupTarget? target = null;
        var backups = new BackupService(database, host, Config, () => target, TestRestic.Path, new BackupPolicy(),
            NullLogger<BackupService>.Instance);

        Assert.False(backups.IsConfigured);
        target = new BackupTarget(Path.Combine(_temporary, "repository"), "correct horse battery");

        Assert.True(backups.IsConfigured);
        Assert.Equal(BackupOutcome.Succeeded, (await backups.RunAsync(CancellationToken.None)).LastOutcome);
    }

    [Fact]
    public async Task Only_an_administrator_joins_and_the_password_is_in_the_answer_once()
    {
        // Still being set up when the host joins, so its storage comes after the answer, in the
        // background, and with nightly backups off, which leaves backing up by hand.
        var service = new FakeBackupService { Pending = 1 };
        using var app = new TestHost(Path.Combine(_temporary, "api"), backupService: service,
            settings: new Dictionary<string, string?> { ["Homebase:Backup:Nightly"] = "false" });
        var admin = await app.SignUpAsync();
        var member = await app.AddUserAsync(admin, "member");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/host/backup/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync("/api/host/backup/account", new { invite = Invite })).StatusCode);
        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/host/backup/account")).GetProperty("joined").GetBoolean());
        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/host/backup")).GetProperty("configured").GetBoolean());

        var refused = await admin.PostAsJsonAsync("/api/host/backup/account", new { invite = "AAAA-AAAA-AAAA-AAAA" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(FakeBackupService.InvalidInvite, await refused.Content.ReadAsStringAsync());

        var joined = await admin.PostAsJsonAsync("/api/host/backup/account", new { invite = Invite, name = "Mac mini" });

        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        var body = await joined.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Matches(Code, body.GetProperty("password").GetString());
        Assert.Equal("setting_up", body.GetProperty("account").GetProperty("storage").GetString());

        // Asked for as soon as the host joined, not after the minute a host with nowhere to back
        // up otherwise waits, and again as soon as the service says.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string? storage = null;
        while (storage != "ready" && clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(100);
            storage = (await admin.GetFromJsonAsync<JsonElement>("/api/host/backup/account")).GetProperty("storage").GetString();
        }
        Assert.Equal("ready", storage);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20));
        var status = await admin.GetStringAsync("/api/host/backup/account");
        Assert.Contains("\"joined\":true", status);
        Assert.DoesNotContain(body.GetProperty("password").GetString()!, status);
        Assert.DoesNotContain(service.Secrets[0], status);
        Assert.DoesNotContain(service.Credential!, status);
        Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/host/backup")).GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("/api/host/backup/account", new { invite = Invite })).StatusCode);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Uncloud Backup's service, as far as a host sees it: one invite, then storage that's
/// <see cref="Pending"/> for so many asks before it's ready, each ready answer with a new key.
/// </summary>
internal sealed class FakeBackupService : HttpMessageHandler
{
    public const string InvalidInvite = "That invite code isn't one Uncloud Backup knows, or it has been used, or it has expired.";

    public sealed record Request(string Path, string? Authorization, JsonElement Body);

    public int Pending { get; set; }
    public TimeSpan RetryAfter { get; set; } = TimeSpan.Zero;
    public bool Unreachable { get; set; }
    public string? Credential { get; private set; }
    public List<string> Secrets { get; } = [];
    public List<Request> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Unreachable) throw new HttpRequestException("No such host is known.");
        var body = request.Content is null
            ? default
            : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
        var path = request.RequestUri!.AbsolutePath;
        lock (Requests) Requests.Add(new Request(path, request.Headers.Authorization?.ToString(), body));

        switch (path)
        {
            case "/api/hosts" when body.GetProperty("invite").GetString() == "K7QM-3XPD-9TWA-HF2R" && Credential is null:
                Credential = "credential-" + Guid.NewGuid().ToString("N");
                return Answer(HttpStatusCode.Created, new
                {
                    household = new { id = "household-1" },
                    host = new { id = "host-1", name = body.GetProperty("name").GetString() },
                    credential = Credential
                });
            case "/api/hosts":
                return Answer(HttpStatusCode.Forbidden, new { error = new { code = "invite_invalid", message = InvalidInvite } });
            case "/api/host/storage" when request.Headers.Authorization?.Parameter != Credential || Credential is null:
                return Answer(HttpStatusCode.Unauthorized, new { error = new { code = "not_a_host", message = "That isn't the credential of a host Uncloud Backup knows." } });
            case "/api/host/storage" when Pending > 0:
                Pending--;
                var waiting = Answer(HttpStatusCode.Accepted, new { setup = new { state = "setting_up", step = "storage" } });
                waiting.Headers.RetryAfter = new RetryConditionHeaderValue(RetryAfter);
                return waiting;
            case "/api/host/storage":
                Secrets.Add("secret-" + Guid.NewGuid().ToString("N"));
                return Answer(HttpStatusCode.Created, new
                {
                    storage = new
                    {
                        endpoint = "https://s3.example", region = "us-east-1", bucket = "uncloud-household-1",
                        accessKeyId = $"AKFAKE{Secrets.Count}", secretAccessKey = Secrets[^1]
                    }
                });
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, object body) =>
        new(status) { Content = JsonContent.Create(body) };
}
