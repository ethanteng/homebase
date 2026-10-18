using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Core.Backup;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homebase.Tests;

/// <summary>
/// The host's backups: what goes in, what's left out, what comes back, and what happens when restic
/// or the storage misbehaves. Most run the real restic against a repository in a folder, which
/// behaves as one in S3-compatible storage does for everything here but the network.
/// </summary>
public sealed class BackupTests : IDisposable
{
    private const string Password = "correct horse battery";

    private readonly string _temporary = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"))).FullName;

    private string Config => Path.Combine(_temporary, "config");
    private string Root => Path.Combine(_temporary, "root");
    private string Repository => Path.Combine(_temporary, "repository");
    private string Restored => Path.Combine(_temporary, "restored");
    // The host's folder as the host keeps it, links resolved (on a Mac, /var is /private/var), which
    // is the path restic records.
    private string? _hostRoot;

    private BackupService Backups(
        BackupTarget? target = null, bool configured = true, string? restic = null, BackupPolicy? policy = null,
        TimeProvider? time = null)
    {
        var database = new ControlDatabase(Config);
        Directory.CreateDirectory(Root);
        var host = new HostService(database, initial: Root);
        _hostRoot = host.RootPath;
        return new BackupService(
            database, host, Config, configured ? target ?? new BackupTarget(Repository, Password) : null,
            restic ?? TestRestic.Path, policy ?? new BackupPolicy(), NullLogger<BackupService>.Instance, time);
    }

    /// <summary>An account's folder under the host's, with what's in it.</summary>
    private string Account(string id) =>
        Directory.CreateDirectory(Path.Combine(_hostRoot ?? Root, UserPaths.UsersDirectory, id)).FullName;

    /// <summary>Where a backed-up path comes back to: restic recreates it in full beneath the target.</summary>
    private string Back(string path) => Path.Combine(Restored, path.TrimStart(Path.DirectorySeparatorChar));

    [Fact]
    public async Task A_backup_holds_everyones_files_and_the_host_itself_but_not_what_can_be_rebuilt()
    {
        var backups = Backups();
        new ControlDatabase(Config).SetSetting("probe", "from before the backup");
        _ = new SecretProtector(Config);
        var alice = Account("alice");
        File.WriteAllText(Path.Combine(alice, "notes.txt"), "remember the milk");
        Directory.CreateDirectory(Path.Combine(alice, "Photos"));
        var photo = RandomNumberGenerator.GetBytes(256 * 1024);
        File.WriteAllBytes(Path.Combine(alice, "Photos", "beach.jpg"), photo);
        var bob = Account("bob");
        File.WriteAllText(Path.Combine(bob, "plans.txt"), "a trip");
        // The Bin is kept: something deleted last week is exactly what a backup is for.
        Directory.CreateDirectory(Path.Combine(alice, ".homebase", "bin", "deletion"));
        File.WriteAllText(Path.Combine(alice, ".homebase", "bin", "deletion", "old.txt"), "deleted yesterday");
        // Not these: one is rebuilt from the files, the other is still arriving.
        File.WriteAllText(Path.Combine(alice, ".homebase", "index.db"), "rebuilt from the files");
        File.WriteAllText(Path.Combine(alice, ".homebase", "index.db-wal"), "and its journal");
        Directory.CreateDirectory(Path.Combine(alice, ".homebase", "uploads", "upload"));
        File.WriteAllText(Path.Combine(alice, ".homebase", "uploads", "upload", "half.bin"), "arriving");

        var status = await backups.RunAsync(CancellationToken.None);

        Assert.Equal(BackupOutcome.Succeeded, status.LastOutcome);
        Assert.Null(status.Message);
        Assert.NotNull(status.LastSucceeded);
        Assert.NotNull(status.LastPruned);
        Assert.True(status.TotalFiles >= 4);
        var snapshot = Assert.Single(await backups.SnapshotsAsync(CancellationToken.None));
        Assert.Equal(status.Snapshot, snapshot.Id);

        await backups.RestoreAsync(snapshot.Id, Restored, null, CancellationToken.None);

        Assert.Equal("remember the milk", File.ReadAllText(Back(Path.Combine(alice, "notes.txt"))));
        Assert.Equal(photo, File.ReadAllBytes(Back(Path.Combine(alice, "Photos", "beach.jpg"))));
        Assert.Equal("a trip", File.ReadAllText(Back(Path.Combine(bob, "plans.txt"))));
        Assert.True(File.Exists(Back(Path.Combine(alice, ".homebase", "bin", "deletion", "old.txt"))));
        Assert.False(File.Exists(Back(Path.Combine(alice, ".homebase", "index.db"))));
        Assert.False(File.Exists(Back(Path.Combine(alice, ".homebase", "index.db-wal"))));
        Assert.False(Directory.Exists(Back(Path.Combine(alice, ".homebase", "uploads"))));
        // What a new host needs to be this one again.
        var host = Back(Path.Combine(Config, "backup", "host"));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Config, "host.key")), File.ReadAllBytes(Path.Combine(host, "host.key")));
        Assert.Equal("from before the backup", new ControlDatabase(host).Setting("probe"));
    }

    [Fact]
    public async Task Each_backup_builds_on_the_last_and_older_ones_are_thinned_out()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-10-01T02:00:00Z"));
        var backups = Backups(time: clock, policy: new BackupPolicy { KeepDaily = 2, KeepWeekly = 0, KeepMonthly = 0 });
        var alice = Account("alice");
        var made = new List<BackupStatus>();
        foreach (var night in new[] { "first", "second", "third" })
        {
            File.WriteAllText(Path.Combine(alice, "notes.txt"), night);
            made.Add(await backups.RunAsync(CancellationToken.None));
            clock.Now = clock.Now.AddDays(1);
        }

        Assert.All(made, status => Assert.Equal(BackupOutcome.Succeeded, status.LastOutcome));
        Assert.Equal(0, made[^1].FilesNew);
        // Three nights, two kept: the first night's goes.
        var kept = await backups.SnapshotsAsync(CancellationToken.None);
        Assert.Equal(new[] { made[1].Snapshot!, made[2].Snapshot! }, kept.Select(snapshot => snapshot.Id));
        Assert.Equal(DateTimeOffset.Parse("2026-10-03T02:00:00Z"), kept[^1].Time);
        await backups.RestoreAsync(kept[^1].Id, Restored, null, CancellationToken.None);
        Assert.Equal("third", File.ReadAllText(Back(Path.Combine(alice, "notes.txt"))));
    }

    [Fact]
    public async Task Another_clients_snapshots_in_the_repository_are_neither_listed_nor_restored()
    {
        var backups = Backups();
        File.WriteAllText(Path.Combine(Account("alice"), "notes.txt"), "ours");
        var ours = await backups.RunAsync(CancellationToken.None);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_temporary, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "theirs.txt"), "somebody else's");
        var other = new Restic(TestRestic.Path, new BackupTarget(Repository, Password),
            Path.Combine(_temporary, "other-cache"), NullLogger.Instance);
        var made = await other.RunAsync(
            ["backup", "--json", "--host", "somebody-else", elsewhere], TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(made.Succeeded, made.Error);
        var theirs = JsonDocument.Parse(made.Output.Last(line => line.Contains("\"message_type\":\"summary\"")))
            .RootElement.GetProperty("snapshot_id").GetString()!;

        var listed = await backups.SnapshotsAsync(CancellationToken.None);

        Assert.Equal(ours.Snapshot, Assert.Single(listed).Id);
        var refused = await Assert.ThrowsAsync<LibraryException>(
            () => backups.RestoreAsync(theirs, Restored, null, CancellationToken.None));
        Assert.Equal("backup_snapshot", refused.Code);
        Assert.False(Directory.Exists(Restored));
    }

    [Fact]
    public async Task A_restore_waits_while_space_is_being_given_back()
    {
        if (OperatingSystem.IsWindows()) return;
        var marks = Directory.CreateDirectory(Path.Combine(_temporary, "marks")).FullName;
        var restic = FakeRestic($$"""
            case "$command" in
              prune) touch '{{marks}}/prune.started'; sleep 2; touch '{{marks}}/prune.finished' ;;
              snapshots) echo '[{"id":"0123456789abcdef0123456789abcdef","time":"2026-10-14T00:00:00Z","paths":["/x"]}]' ;;
              restore) if [ -f '{{marks}}/prune.finished' ]; then echo after; else echo during; fi > '{{marks}}/restore' ;;
            esac
            exit 0
            """);
        var backups = Backups(restic: restic);

        // The first backup gives space back, so this one reaches a prune and stays in it a while.
        Assert.True(backups.TryStart(CancellationToken.None));
        await Until(() => File.Exists(Path.Combine(marks, "prune.started")));
        await backups.RestoreAsync("01234567", Restored, null, CancellationToken.None);

        Assert.Equal("after", File.ReadAllText(Path.Combine(marks, "restore")).Trim());
    }

    [Fact]
    public async Task Space_is_given_back_weekly_not_nightly()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-10-01T02:00:00Z"));
        var backups = Backups(time: clock);
        File.WriteAllText(Path.Combine(Account("alice"), "notes.txt"), "kept");

        var first = await backups.RunAsync(CancellationToken.None);
        clock.Now = clock.Now.AddDays(1);
        var second = await backups.RunAsync(CancellationToken.None);
        clock.Now = clock.Now.AddDays(7);
        var third = await backups.RunAsync(CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-10-01T02:00:00Z"), first.LastPruned);
        Assert.Equal(first.LastPruned, second.LastPruned);
        Assert.Equal(clock.Now, third.LastPruned);
    }

    [Fact]
    public async Task A_backup_in_storage_that_takes_no_more_writes_can_still_be_restored()
    {
        // Permissions are what make the folder read-only here, and they don't stop root.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var backups = Backups();
        var alice = Account("alice");
        File.WriteAllText(Path.Combine(alice, "notes.txt"), "still mine");
        await backups.RunAsync(CancellationToken.None);

        ReadOnly(Repository, true);
        try
        {
            var snapshot = Assert.Single(await backups.SnapshotsAsync(CancellationToken.None));
            await backups.RestoreAsync(snapshot.Id, Restored, null, CancellationToken.None);
            Assert.Equal("still mine", File.ReadAllText(Back(Path.Combine(alice, "notes.txt"))));

            // While a backup can't be made, and says so rather than claiming it was.
            var refused = await backups.RunAsync(CancellationToken.None);
            Assert.NotEqual(BackupOutcome.Succeeded, refused.LastOutcome);
            Assert.NotNull(refused.Message);
        }
        finally
        {
            ReadOnly(Repository, false);
        }
    }

    [Fact]
    public async Task A_restic_that_doesnt_answer_is_stopped_at_its_time_limit()
    {
        if (OperatingSystem.IsWindows()) return;
        var restic = FakeRestic("exec sleep 60");
        var backups = Backups(restic: restic, policy: new BackupPolicy { OpenLimit = TimeSpan.FromMilliseconds(500) });
        var clock = Stopwatch.StartNew();

        var status = await backups.RunAsync(CancellationToken.None);

        Assert.Equal(BackupOutcome.TimedOut, status.LastOutcome);
        Assert.Contains("took longer than", status.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"Took {clock.Elapsed}");
    }

    [Fact]
    public async Task Storage_that_says_no_is_reported_as_saying_no_without_guessing_why()
    {
        if (OperatingSystem.IsWindows()) return;
        var restic = FakeRestic("""
            case "$command" in
              backup) echo 'Fatal: unable to save snapshot: client.PutObject: Access Denied. (403)' >&2; exit 1 ;;
              *) exit 0 ;;
            esac
            """);

        var status = await Backups(restic: restic).RunAsync(CancellationToken.None);

        Assert.Equal(BackupOutcome.Refused, status.LastOutcome);
        // Full, or a key that no longer works: both answer 403, and only the backup service can tell.
        Assert.Contains("may be full", status.Message);
        Assert.Contains("may no longer work", status.Message);
        Assert.Null(status.LastSucceeded);
    }

    [Fact]
    public async Task Restic_settings_in_Uncloud_s_environment_dont_change_the_repository_or_key()
    {
        if (OperatingSystem.IsWindows()) return;
        var seen = Path.Combine(_temporary, "seen");
        var restic = FakeRestic($"""
            env | grep -E '^(RESTIC_|AWS_)' | sort >> '{seen}'
            echo "args: $all" >> '{seen}'
            exit 0
            """);
        var target = new BackupTarget("s3:https://storage.example/bucket/restic", "secret", "KEYID", "keysecret", "us-west-4");
        Environment.SetEnvironmentVariable("RESTIC_PASSWORD_FILE", "/somewhere/else");
        Environment.SetEnvironmentVariable("AWS_PROFILE", "somebody");
        try
        {
            await Backups(target: target, restic: restic).RunAsync(CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RESTIC_PASSWORD_FILE", null);
            Environment.SetEnvironmentVariable("AWS_PROFILE", null);
        }

        var lines = File.ReadAllLines(seen).Distinct().ToArray();
        Assert.Contains("RESTIC_REPOSITORY=s3:https://storage.example/bucket/restic", lines);
        Assert.Contains("RESTIC_PASSWORD=secret", lines);
        Assert.Contains("AWS_ACCESS_KEY_ID=KEYID", lines);
        Assert.Contains("AWS_SECRET_ACCESS_KEY=keysecret", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("RESTIC_PASSWORD_FILE", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("AWS_PROFILE", StringComparison.Ordinal));
        Assert.All(lines.Where(line => line.StartsWith("args:", StringComparison.Ordinal)), line =>
        {
            Assert.Contains("-o s3.bucket-lookup=path", line);
            Assert.Contains("-o s3.region=us-west-4", line);
        });
    }

    [Fact]
    public async Task Without_somewhere_to_back_up_to_nothing_runs()
    {
        var backups = Backups(configured: false);

        Assert.False(backups.IsConfigured);
        Assert.False(backups.TryStart(CancellationToken.None));
        var status = await backups.RunAsync(CancellationToken.None);
        Assert.False(status.Configured);
        Assert.Null(status.LastStarted);
    }

    [Fact]
    public async Task Only_one_backup_runs_at_a_time()
    {
        if (OperatingSystem.IsWindows()) return;
        var restic = FakeRestic("""
            case "$command" in backup) sleep 2 ;; esac
            exit 0
            """);
        var backups = Backups(restic: restic);

        Assert.True(backups.TryStart(CancellationToken.None));
        Assert.True(backups.Status.Running);
        Assert.False(backups.TryStart(CancellationToken.None));
        var meanwhile = await backups.RunAsync(CancellationToken.None);

        Assert.True(meanwhile.Running);
        await Until(() => !backups.Status.Running);
        Assert.Equal(BackupOutcome.Succeeded, backups.Status.LastOutcome);
    }

    [Fact]
    public void Backups_are_nightly_and_a_missed_night_is_caught_up_soon_but_not_over_and_over()
    {
        var utc = TimeZoneInfo.Utc;
        var now = DateTimeOffset.Parse("2026-10-14T15:00:00Z");
        var soon = now + TimeSpan.FromMinutes(5);

        // Never backed up: soon.
        Assert.Equal(soon, BackupSchedule.Next(now, utc, 2, null, null));
        // Backed up last night: tonight's is tomorrow at two.
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T02:00:00Z"),
            BackupSchedule.Next(now, utc, 2, now.AddHours(-13), now.AddHours(-13)));
        // At one in the morning, after last night's: two o'clock is still to come today.
        var early = DateTimeOffset.Parse("2026-10-14T01:00:00Z");
        Assert.Equal(DateTimeOffset.Parse("2026-10-14T02:00:00Z"),
            BackupSchedule.Next(early, utc, 2, early.AddHours(-23), early.AddHours(-23)));
        // Three days without one, and one tried an hour ago didn't work: tonight, not again now.
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T02:00:00Z"),
            BackupSchedule.Next(now, utc, 2, now.AddDays(-3), now.AddHours(-1)));
        // Three days without one, and none tried today: soon.
        Assert.Equal(soon, BackupSchedule.Next(now, utc, 2, now.AddDays(-3), now.AddHours(-8)));
    }

    [Fact]
    public void The_restic_beside_the_application_is_used_ahead_of_the_path()
    {
        var application = Directory.CreateDirectory(Path.Combine(_temporary, "application")).FullName;
        var bundled = Path.Combine(application, OperatingSystem.IsWindows() ? "restic.exe" : "restic");

        Assert.Equal("restic", Restic.Binary(null, application));
        File.WriteAllText(bundled, "");
        Assert.Equal(bundled, Restic.Binary(null, application));
        Assert.Equal("/opt/restic", Restic.Binary("/opt/restic", application));
    }

    [Fact]
    public async Task Only_an_administrator_sees_the_hosts_backups_or_starts_one()
    {
        using var app = new TestHost(Path.Combine(_temporary, "api"), settings: new Dictionary<string, string?>
        {
            ["Homebase:Backup:Repository"] = Repository,
            ["Homebase:Backup:Password"] = Password,
            ["Homebase:Backup:Restic"] = TestRestic.Path,
            ["Homebase:Backup:Nightly"] = "false"
        });
        var admin = await app.SignUpAsync();
        Directory.CreateDirectory(Root);
        await TestHost.SetHostRootAsync(admin, Root);
        var member = await app.AddUserAsync(admin, "member");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/host/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync("/api/host/backup", null)).StatusCode);

        var before = await admin.GetFromJsonAsync<JsonElement>("/api/host/backup");
        Assert.True(before.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("lastOutcome").ValueKind);

        var started = await admin.PostAsync("/api/host/backup", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.True((await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("running").GetBoolean());

        JsonElement after = default;
        await Until(async () =>
        {
            after = await admin.GetFromJsonAsync<JsonElement>("/api/host/backup");
            return !after.GetProperty("running").GetBoolean();
        });
        Assert.Equal("Succeeded", after.GetProperty("lastOutcome").GetString());
        Assert.False(string.IsNullOrEmpty(after.GetProperty("snapshot").GetString()));
    }

    [Fact]
    public async Task A_host_without_backups_set_up_says_so()
    {
        using var app = new TestHost(Path.Combine(_temporary, "api"));
        var admin = await app.SignUpAsync();

        var status = await admin.GetFromJsonAsync<JsonElement>("/api/host/backup");
        var started = await admin.PostAsync("/api/host/backup", null);

        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, started.StatusCode);
    }

    [Fact]
    public async Task A_check_restores_the_backup_somewhere_of_its_own_and_finds_it_the_same_as_here()
    {
        var backups = Backups();
        _ = new SecretProtector(Config);
        var alice = Account("alice");
        File.WriteAllText(Path.Combine(alice, "notes.txt"), "remember the milk");
        // Names restic would read as patterns, or as a variable, come back like any other.
        File.WriteAllText(Path.Combine(alice, "a[1].txt"), "bracketed");
        File.WriteAllText(Path.Combine(alice, "star*.txt"), "starred");
        File.WriteAllText(Path.Combine(alice, "cost$HOME.txt"), "dollar");
        Directory.CreateDirectory(Path.Combine(alice, "Photos"));
        File.WriteAllBytes(Path.Combine(alice, "Photos", "beach.jpg"), RandomNumberGenerator.GetBytes(300 * 1024));
        File.WriteAllText(Path.Combine(Account("bob"), "plans.txt"), "a trip");
        Assert.Equal(BackupOutcome.Succeeded, (await backups.RunAsync(CancellationToken.None)).LastOutcome);

        var check = await backups.CheckAsync(CancellationToken.None);

        Assert.True(check.Passed, check.Message);
        Assert.False(check.Sampled);
        Assert.Equal(6, check.FilesInBackup);
        Assert.Equal(6, check.Files);
        Assert.Equal(6, check.Matched);
        Assert.Equal((0L, 0L, 0L, 0L), (check.ChangedSince!.Value, check.GoneSince!.Value, check.Mismatched!.Value, check.Missing!.Value));
        Assert.True(check.HostKeyMatched);
        Assert.Equal(backups.Status.Snapshot, check.Snapshot?[..backups.Status.Snapshot!.Length]);
        // Nothing restored is left behind, and how it went is kept.
        Assert.False(Directory.Exists(Path.Combine(Config, "backup", "check")));
        Assert.Equal(check, backups.LastCheck);
    }

    [Fact]
    public async Task A_check_tells_files_changed_or_gone_since_from_ones_that_came_back_wrong()
    {
        var backups = Backups();
        var alice = Account("alice");
        foreach (var name in new[] { "changed.txt", "gone.txt", "kept.txt", "wrong.txt" })
            File.WriteAllText(Path.Combine(alice, name), $"what {name} said at the time");
        await backups.RunAsync(CancellationToken.None);

        File.WriteAllText(Path.Combine(alice, "changed.txt"), "said something else later");
        File.Delete(Path.Combine(alice, "gone.txt"));
        // Different bytes in a file that looks untouched since: the same size and time.
        var wrong = Path.Combine(alice, "wrong.txt");
        var when = File.GetLastWriteTimeUtc(wrong);
        File.WriteAllText(wrong, "WHAT wrong.txt said at the time");
        File.SetLastWriteTimeUtc(wrong, when);

        var check = await backups.CheckAsync(CancellationToken.None);

        Assert.False(check.Passed);
        Assert.Equal((1L, 1L, 1L, 1L, 0L),
            (check.Matched!.Value, check.ChangedSince!.Value, check.GoneSince!.Value, check.Mismatched!.Value, check.Missing!.Value));
    }

    [Fact]
    public async Task A_backup_bigger_than_a_check_restores_is_checked_by_a_sample()
    {
        var backups = Backups(policy: new BackupPolicy { CheckFiles = 3 });
        var alice = Account("alice");
        // Every name one a pattern would misread unless it's written as a pattern should be, and
        // one with a $ in it, which a pattern file can't ask for at all.
        for (var i = 1; i <= 6; i++) File.WriteAllText(Path.Combine(alice, $"photo[{i}]*.txt"), $"photo {i}");
        File.WriteAllText(Path.Combine(alice, "cost$HOME.txt"), "dollar");
        await backups.RunAsync(CancellationToken.None);

        var check = await backups.CheckAsync(CancellationToken.None);

        Assert.True(check.Passed, check.Message);
        Assert.True(check.Sampled);
        Assert.Equal(7, check.FilesInBackup);
        Assert.Equal(3, check.Files);
        Assert.Equal(3, check.Matched);
        Assert.True(check.HostKeyMatched ?? true);
    }

    [Fact]
    public async Task A_check_waits_for_older_backups_to_be_tidied_away_before_it_picks_one()
    {
        if (OperatingSystem.IsWindows()) return;
        var marks = Directory.CreateDirectory(Path.Combine(_temporary, "marks")).FullName;
        // Only the check lists snapshots, so where it does so says when it chose one.
        var restic = FakeRestic($$"""
            case "$command" in
              prune) touch '{{marks}}/prune.started'; sleep 2; touch '{{marks}}/prune.finished' ;;
              snapshots)
                if [ -f '{{marks}}/prune.finished' ]; then echo after; else echo during; fi > '{{marks}}/chose'
                echo '[{"id":"0123456789abcdef0123456789abcdef","time":"2026-10-14T00:00:00Z","paths":["/x"]}]' ;;
            esac
            exit 0
            """);
        var backups = Backups(restic: restic);

        Assert.True(backups.TryStart(CancellationToken.None));
        await Until(() => File.Exists(Path.Combine(marks, "prune.started")));
        await backups.CheckAsync(CancellationToken.None);

        Assert.Equal("after", File.ReadAllText(Path.Combine(marks, "chose")).Trim());
    }

    [Fact]
    public async Task A_check_that_fails_says_so_without_naming_anybodys_files()
    {
        if (OperatingSystem.IsWindows()) return;
        var restic = FakeRestic("""
            case "$command" in
              snapshots) echo '[{"id":"0123456789abcdef0123456789abcdef","time":"2026-10-14T00:00:00Z","paths":["/x"]}]' ;;
              ls) echo "error: lstat /Users/alice/Uncloud/users/alice/diary-for-nobody.txt: permission denied" >&2; exit 1 ;;
            esac
            exit 0
            """);
        var backups = Backups(restic: restic);

        var check = await backups.CheckAsync(CancellationToken.None);

        Assert.False(check.Passed);
        Assert.Equal("Uncloud couldn't read what's in the backup. Uncloud's log says why.", check.Message);
        Assert.DoesNotContain("diary", JsonSerializer.Serialize(backups.LastCheck));
    }

    [Fact]
    public async Task Only_the_backups_own_password_opens_it()
    {
        Assert.Null(await Backups().OpensAsync(CancellationToken.None));
        await Backups().RunAsync(CancellationToken.None);

        Assert.True(await Backups().OpensAsync(CancellationToken.None));
        Assert.False(await Backups(target: new BackupTarget(Repository, "not the password")).OpensAsync(CancellationToken.None));
    }

    [Fact]
    public async Task With_no_backup_yet_there_is_nothing_to_check()
    {
        var backups = Backups();

        var check = await backups.CheckAsync(CancellationToken.None);

        Assert.Null(check.Passed);
        Assert.Equal("There's no backup to check yet.", check.Message);
    }

    [Fact]
    public async Task Only_an_administrator_lists_the_backups_or_checks_one()
    {
        using var app = new TestHost(Path.Combine(_temporary, "api"), settings: new Dictionary<string, string?>
        {
            ["Homebase:Backup:Repository"] = Repository,
            ["Homebase:Backup:Password"] = Password,
            ["Homebase:Backup:Restic"] = TestRestic.Path,
            ["Homebase:Backup:Nightly"] = "false"
        });
        var admin = await app.SignUpAsync();
        Directory.CreateDirectory(Root);
        await TestHost.SetHostRootAsync(admin, Root);
        var member = await app.AddUserAsync(admin, "member");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/host/backup/check")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync("/api/host/backup/check", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/host/backup/snapshots")).StatusCode);

        await admin.PostAsync("/api/host/backup", null);
        await Until(async () => !(await admin.GetFromJsonAsync<JsonElement>("/api/host/backup")).GetProperty("running").GetBoolean());
        var listed = await admin.GetFromJsonAsync<JsonElement>("/api/host/backup/snapshots");
        Assert.Equal(1, listed.GetProperty("snapshots").GetArrayLength());

        var started = await admin.PostAsync("/api/host/backup/check", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        JsonElement check = default;
        await Until(async () =>
        {
            check = await admin.GetFromJsonAsync<JsonElement>("/api/host/backup/check");
            return !check.GetProperty("running").GetBoolean() && check.GetProperty("finished").ValueKind != JsonValueKind.Null;
        });
        Assert.True(check.GetProperty("passed").GetBoolean());
        Assert.True(check.GetProperty("hostKeyMatched").GetBoolean());
    }

    /// <summary>
    /// A stand-in for restic: <paramref name="body"/> runs with <c>$command</c> set to the restic
    /// command it was asked for and <c>$all</c> to every argument.
    /// </summary>
    private string FakeRestic(string body)
    {
        var path = Path.Combine(_temporary, $"restic-{Guid.NewGuid():N}");
        File.WriteAllText(path, $"""
            #!/bin/sh
            all="$*"
            while [ "$1" = "--cache-dir" ] || [ "$1" = "-o" ] || [ "$1" = "--no-lock" ]; do
              if [ "$1" = "--no-lock" ]; then shift; else shift 2; fi
            done
            command="$1"
            {body}
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>Takes away, or gives back, the right to write anything in <paramref name="folder"/>.</summary>
    private static void ReadOnly(string folder, bool readOnly)
    {
        if (OperatingSystem.IsWindows()) return;
        var folders = Directory.GetDirectories(folder, "*", SearchOption.AllDirectories).Prepend(folder);
        foreach (var directory in folders)
            File.SetUnixFileMode(directory, readOnly
                ? UnixFileMode.UserRead | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task Until(Func<bool> condition) => await Until(() => Task.FromResult(condition()));

    private static async Task Until(Func<Task<bool>> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Still waiting after a minute.");
            await Task.Delay(100);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        if (Directory.Exists(Repository) && !OperatingSystem.IsWindows()) ReadOnly(Repository, false);
        try { Directory.Delete(_temporary, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The restic the backup tests run: one named in HOMEBASE_TEST_RESTIC, else the copy
/// scripts/fetch-restic.sh puts beside the server (check.sh fetches it), else one on the PATH.
/// </summary>
internal static class TestRestic
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        if (Environment.GetEnvironmentVariable("HOMEBASE_TEST_RESTIC") is { Length: > 0 } named) return named;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (!File.Exists(System.IO.Path.Combine(folder.FullName, "Homebase.slnx"))) continue;
            var fetched = System.IO.Path.Combine(folder.FullName, "src", "Homebase.Server", "bin", "Debug", "net10.0", "restic");
            if (File.Exists(fetched)) return fetched;
            break;
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            var candidate = System.IO.Path.Combine(directory, "restic");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException(
            "The backup tests need restic. Run scripts/fetch-restic.sh, or set HOMEBASE_TEST_RESTIC to one.");
    }
}
