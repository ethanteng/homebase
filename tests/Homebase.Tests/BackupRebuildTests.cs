using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core.Accounts;
using Homebase.Core.Backup;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homebase.Tests;

/// <summary>
/// A host rebuilt from a lost one's backup: everybody's accounts and files as they were, in this
/// host's folder, with what was here kept aside. Run against the real restic and a repository in a
/// folder, the lost host and the new one each with folders of their own.
/// </summary>
public sealed class BackupRebuildTests : IDisposable
{
    private readonly string _temporary = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"))).FullName;

    private string Repository => Path.Combine(_temporary, "repository");

    private TestHost Host(string name) => new(Path.Combine(_temporary, name, "config"), settings: new Dictionary<string, string?>
    {
        ["Homebase:Backup:Repository"] = Repository,
        ["Homebase:Backup:Password"] = "correct horse battery",
        ["Homebase:Backup:Restic"] = TestRestic.Path,
        ["Homebase:Backup:Nightly"] = "false"
    });

    private string Root(string name) => Directory.CreateDirectory(Path.Combine(_temporary, name, "root")).FullName;

    [Fact]
    public async Task A_host_rebuilt_from_a_lost_ones_backup_is_that_host_in_its_own_folder()
    {
        // The host that's lost: two people, their files, and a computer one of them synced.
        string snapshot, ownerId, memberId, lostOwnerRoot;
        using (var lost = Host("lost"))
        {
            var owner = await lost.SignUpAsync("owner");
            await TestHost.SetHostRootAsync(owner, Root("lost"));
            var member = await lost.AddUserAsync(owner, "member");
            var ownerRoot = await TestHost.UserRootAsync(owner);
            var memberRoot = await TestHost.UserRootAsync(member);
            (ownerId, memberId, lostOwnerRoot) = (Path.GetFileName(ownerRoot), Path.GetFileName(memberRoot), ownerRoot);
            File.WriteAllText(Path.Combine(ownerRoot, "taxes.txt"), "filed");
            Directory.CreateDirectory(Path.Combine(memberRoot, "Photos", "2024"));
            File.WriteAllText(Path.Combine(memberRoot, "Photos", "2024", "beach [1].jpg"), "sand");
            using (var connection = new ControlDatabase(Path.Combine(_temporary, "lost", "config")).Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO sync_devices (device_id, user_id, name, added_at) VALUES ('DEVICE', $user, 'Laptop', '2026-10-01')";
                command.Parameters.AddWithValue("$user", memberId);
                command.ExecuteNonQuery();
            }
            (await owner.PostAsync("/api/host/backup", null)).EnsureSuccessStatusCode();
            await Until(async () => !(await owner.GetFromJsonAsync<JsonElement>("/api/host/backup")).GetProperty("running").GetBoolean());
            snapshot = (await owner.GetFromJsonAsync<JsonElement>("/api/host/backup")).GetProperty("snapshot").GetString()!;
        }

        // The new one, set up by somebody who needed an account to get as far as Settings.
        var config = Path.Combine(_temporary, "new", "config");
        string standInId, root;
        using (var fresh = Host("new"))
        {
            var standIn = await fresh.SignUpAsync("stand-in");
            await TestHost.SetHostRootAsync(standIn, Root("new"));
            var standInRoot = await TestHost.UserRootAsync(standIn);
            standInId = Path.GetFileName(standInRoot);
            // The folder as the host has it, any link on the way to it followed.
            root = Path.GetDirectoryName(Path.GetDirectoryName(standInRoot))!;
            File.WriteAllText(Path.Combine(standInRoot, "scratch.txt"), "made while setting up");
            Assert.False((await standIn.GetFromJsonAsync<JsonElement>("/api/host/backup/rebuild")).GetProperty("running").GetBoolean());
            // Left by goes that didn't finish: one from another backup, and one from this one that got
            // partway through a file. The first is no use; the second is carried on from.
            var other = Directory.CreateDirectory(Path.Combine(root, ".uncloud-rebuild", "another-backup")).FullName;
            File.WriteAllText(Path.Combine(other, "left.txt"), "from another backup");
            var partway = Path.Combine(root, ".uncloud-rebuild", snapshot, lostOwnerRoot.TrimStart('/'), "taxes.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(partway)!);
            File.WriteAllText(partway, "fi");

            var unasked = await standIn.PostAsJsonAsync("/api/host/backup/rebuild", new { snapshot });
            Assert.Equal(HttpStatusCode.BadRequest, unasked.StatusCode);
            var started = await standIn.PostAsJsonAsync("/api/host/backup/rebuild", new { snapshot, confirm = true });
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

            // Ready, then Uncloud stops itself to start again as the host in the backup.
            await Until(async () =>
            {
                try
                {
                    var rebuild = await standIn.GetFromJsonAsync<JsonElement>("/api/host/backup/rebuild");
                    Assert.True(rebuild.GetProperty("running").GetBoolean(), rebuild.GetProperty("message").ToString());
                    return rebuild.GetProperty("stage").GetString() == "restarting";
                }
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException or ObjectDisposedException)
                {
                    return File.Exists(Path.Combine(config, "rebuild.json"));
                }
            });
            Assert.True(File.Exists(Path.Combine(config, "rebuild.json")));
            // Nothing is in place until it starts again: the stand-in's files are where they were.
            Assert.True(File.Exists(Path.Combine(standInRoot, "scratch.txt")));
        }

        using var rebuilt = Host("new");
        var back = await rebuilt.SignInAsync("owner");
        var memberBack = await rebuilt.SignInAsync("member");
        var refused = await rebuilt.Anonymous().PostAsJsonAsync("/api/session", new { username = "stand-in", password = TestHost.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // Everybody's files, in this host's folder rather than the lost one's.
        Assert.Equal(Path.Combine(root, "users", ownerId), await TestHost.UserRootAsync(back));
        Assert.Equal("filed", File.ReadAllText(Path.Combine(root, "users", ownerId, "taxes.txt")));
        Assert.Equal("sand", File.ReadAllText(Path.Combine(root, "users", memberId, "Photos", "2024", "beach [1].jpg")));
        Assert.Equal(Path.Combine(root, "users", memberId), await TestHost.UserRootAsync(memberBack));
        // What was here before, aside rather than gone.
        var kept = Assert.Single(Directory.GetDirectories(root, ".uncloud-before-rebuild-*"));
        Assert.Equal("made while setting up", File.ReadAllText(Path.Combine(kept, "users", standInId, "scratch.txt")));
        Assert.True(File.Exists(Path.Combine(Assert.Single(Directory.GetDirectories(config, "before-rebuild-*")), "homebase.db")));
        // And nothing left over from getting there.
        Assert.False(File.Exists(Path.Combine(config, "rebuild.json")));
        Assert.False(Directory.Exists(Path.Combine(config, "rebuild")));
        Assert.False(Directory.Exists(Path.Combine(root, ".uncloud-rebuild")));

        var said = await back.GetFromJsonAsync<JsonElement>("/api/host/backup/rebuild");
        Assert.False(said.GetProperty("running").GetBoolean());
        Assert.Equal(JsonValueKind.String, said.GetProperty("rebuiltAt").ValueKind);
        var made = (await back.GetFromJsonAsync<JsonElement>("/api/host/backup/snapshots")).GetProperty("snapshots")[0];
        Assert.Equal(made.GetProperty("time").GetDateTimeOffset(), said.GetProperty("rebuiltFrom").GetDateTimeOffset());
        // The computer was paired with the lost host's Syncthing: it's paired again with this one.
        using (var connection = new ControlDatabase(config).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sync_devices";
            Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
        }
        // Backing up goes on, as the host it now is.
        Assert.Equal(HttpStatusCode.Accepted, (await back.PostAsync("/api/host/backup", null)).StatusCode);
        await Until(async () => !(await back.GetFromJsonAsync<JsonElement>("/api/host/backup")).GetProperty("running").GetBoolean());
        Assert.Equal("Succeeded", (await back.GetFromJsonAsync<JsonElement>("/api/host/backup")).GetProperty("lastOutcome").GetString());
    }

    [Fact]
    public async Task A_rebuild_that_cant_be_done_changes_nothing_and_backing_up_goes_on()
    {
        using var app = Host("one");
        var admin = await app.SignUpAsync();
        var root = Root("one");
        await TestHost.SetHostRootAsync(admin, root);
        File.WriteAllText(Path.Combine(await TestHost.UserRootAsync(admin), "notes.txt"), "mine");
        (await admin.PostAsync("/api/host/backup", null)).EnsureSuccessStatusCode();
        await Until(async () => !(await admin.GetFromJsonAsync<JsonElement>("/api/host/backup")).GetProperty("running").GetBoolean());

        var started = await admin.PostAsJsonAsync("/api/host/backup/rebuild", new { snapshot = "0123456789abcdef", confirm = true });

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        JsonElement rebuild = default;
        await Until(async () =>
        {
            rebuild = await admin.GetFromJsonAsync<JsonElement>("/api/host/backup/rebuild");
            return !rebuild.GetProperty("running").GetBoolean();
        });
        Assert.Contains("isn’t one of this Uncloud’s backups", rebuild.GetProperty("message").GetString());
        Assert.False(File.Exists(Path.Combine(_temporary, "one", "config", "rebuild.json")));
        Assert.False(Directory.Exists(Path.Combine(root, ".uncloud-rebuild")));
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/host/backup", null)).StatusCode);
        Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/host/backup")).GetProperty("running").GetBoolean());
        await Until(async () => !(await admin.GetFromJsonAsync<JsonElement>("/api/host/backup")).GetProperty("running").GetBoolean());
    }

    [Fact]
    public void A_start_interrupted_while_putting_a_rebuild_in_place_carries_on_from_there()
    {
        var config = Directory.CreateDirectory(Path.Combine(_temporary, "config")).FullName;
        var root = Root("host");
        var staging = Directory.CreateDirectory(Path.Combine(root, ".uncloud-rebuild")).FullName;
        var restored = Directory.CreateDirectory(Path.Combine(staging, "old", "root", "users", "person")).FullName;
        File.WriteAllText(Path.Combine(restored, "notes.txt"), "from the backup");
        Directory.CreateDirectory(Path.Combine(root, "users", "stand-in"));
        // The database and key here were already moved aside; the ones from the backup weren't yet in.
        var aside = Directory.CreateDirectory(Path.Combine(config, "before-rebuild-20261018-120000")).FullName;
        File.WriteAllText(Path.Combine(aside, "homebase.db"), "the stand-in's database");
        File.WriteAllText(Path.Combine(aside, "host.key"), "the stand-in's key");
        var ready = Directory.CreateDirectory(Path.Combine(config, "rebuild")).FullName;
        File.WriteAllText(Path.Combine(ready, "homebase.db"), "the lost host's database");
        File.WriteAllText(Path.Combine(ready, "host.key"), "the lost host's key");
        File.WriteAllText(Path.Combine(config, "rebuild.json"), JsonSerializer.Serialize(
            new { Root = root, Users = Path.GetDirectoryName(restored), Staging = staging, Stamp = "20261018-120000", Step = "aside" }));

        Assert.True(HostRebuild.ApplyPending(config, NullLogger.Instance));

        Assert.Equal("the lost host's database", File.ReadAllText(Path.Combine(config, "homebase.db")));
        Assert.Equal("the lost host's key", File.ReadAllText(Path.Combine(config, "host.key")));
        Assert.Equal("the stand-in's database", File.ReadAllText(Path.Combine(aside, "homebase.db")));
        Assert.Equal("from the backup", File.ReadAllText(Path.Combine(root, "users", "person", "notes.txt")));
        Assert.True(Directory.Exists(Path.Combine(root, ".uncloud-before-rebuild-20261018-120000", "users", "stand-in")));
        Assert.False(File.Exists(Path.Combine(config, "rebuild.json")));
        Assert.False(Directory.Exists(staging));
        // Done, so starting again does nothing more.
        Assert.False(HostRebuild.ApplyPending(config, NullLogger.Instance));
    }

    [Fact]
    public void A_rebuild_waits_for_the_drive_with_everybodys_files_before_changing_anything()
    {
        var config = Directory.CreateDirectory(Path.Combine(_temporary, "config")).FullName;
        // On a drive that isn't there yet as the computer starts.
        var root = Path.Combine(_temporary, "external", "root");
        var staging = Path.Combine(root, ".uncloud-rebuild");
        var restored = Path.Combine(staging, "snapshot", "old", "root", "users");
        new ControlDatabase(config).SetSetting("whose", "this host's");
        var ready = Directory.CreateDirectory(Path.Combine(config, "rebuild")).FullName;
        new ControlDatabase(ready).SetSetting("whose", "the lost host's");
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(Path.Combine(ready, "host.key"), key);
        File.WriteAllText(Path.Combine(config, "rebuild.json"), JsonSerializer.Serialize(
            new { Root = root, Users = restored, Staging = staging, Stamp = "20261018-120000", Step = "ready" }));

        Assert.Throws<DirectoryNotFoundException>(() => HostRebuild.ApplyPending(config, NullLogger.Instance));

        Assert.Equal("this host's", new ControlDatabase(config).Setting("whose"));
        Assert.True(File.Exists(Path.Combine(ready, "homebase.db")));
        Assert.True(File.Exists(Path.Combine(config, "rebuild.json")));

        // The drive is back, and the rebuild goes in, files and all.
        Directory.CreateDirectory(Path.Combine(restored, "person"));
        File.WriteAllText(Path.Combine(restored, "person", "notes.txt"), "from the backup");

        Assert.True(HostRebuild.ApplyPending(config, NullLogger.Instance));

        Assert.Equal("the lost host's", new ControlDatabase(config).Setting("whose"));
        Assert.Equal(key, File.ReadAllBytes(Path.Combine(config, "host.key")));
        Assert.Equal("from the backup", File.ReadAllText(Path.Combine(root, "users", "person", "notes.txt")));
    }

    [Fact]
    public void A_rebuild_no_longer_all_there_is_given_up_before_anything_changes()
    {
        var config = Directory.CreateDirectory(Path.Combine(_temporary, "config")).FullName;
        var root = Root("host");
        Directory.CreateDirectory(Path.Combine(root, "users", "person"));
        File.WriteAllText(Path.Combine(config, "homebase.db"), "this host's database");
        File.WriteAllText(Path.Combine(config, "rebuild.json"), JsonSerializer.Serialize(
            new { Root = root, Users = (string?)null, Staging = Path.Combine(root, ".uncloud-rebuild"), Stamp = "20261018-120000", Step = "ready" }));

        Assert.False(HostRebuild.ApplyPending(config, NullLogger.Instance));

        Assert.Equal("this host's database", File.ReadAllText(Path.Combine(config, "homebase.db")));
        Assert.True(Directory.Exists(Path.Combine(root, "users", "person")));
        Assert.False(File.Exists(Path.Combine(config, "rebuild.json")));
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Still waiting after a minute.");
            await Task.Delay(100);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
