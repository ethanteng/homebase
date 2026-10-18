using System.Text.Json;
using System.Text.Json.Serialization;
using Homebase.Core.Accounts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Backup;

public enum BackupOutcome
{
    /// <summary>Everything was backed up.</summary>
    Succeeded,
    /// <summary>A backup was made, but some files couldn't be read and aren't in it.</summary>
    Partial,
    /// <summary>The storage said no. Why isn't known here: full, or a key that no longer works.</summary>
    Refused,
    /// <summary>restic was stopped at its time limit.</summary>
    TimedOut,
    Failed
}

/// <summary>How the host's backups stand: the last run, and the last one that worked.</summary>
public sealed record BackupStatus
{
    public bool Configured { get; init; }
    public bool Running { get; init; }
    public DateTimeOffset? LastStarted { get; init; }
    public DateTimeOffset? LastFinished { get; init; }
    public BackupOutcome? LastOutcome { get; init; }
    /// <summary>What happened, for a person, when there's anything to say.</summary>
    public string? Message { get; init; }
    public DateTimeOffset? LastSucceeded { get; init; }
    /// <summary>The snapshot the last successful run made.</summary>
    public string? Snapshot { get; init; }
    public long? FilesNew { get; init; }
    public long? FilesChanged { get; init; }
    /// <summary>What the last run added to the storage, after deduplication and compression.</summary>
    public long? BytesAdded { get; init; }
    public long? TotalFiles { get; init; }
    public long? TotalBytes { get; init; }
    public DateTimeOffset? LastPruned { get; init; }
}

/// <summary>One snapshot in the backup.</summary>
public sealed record BackupSnapshot(string Id, DateTimeOffset Time, IReadOnlyList<string> Paths);

/// <summary>How much is kept, how often it's tidied, and how long each step may take.</summary>
public sealed record BackupPolicy
{
    public int KeepDaily { get; init; } = 7;
    public int KeepWeekly { get; init; } = 4;
    public int KeepMonthly { get; init; } = 12;
    /// <summary>
    /// Pruning rewrites part of the repository, and under object lock every object it rewrites
    /// stays behind, locked and billed. So it's rare, and restic's own tolerance of unused space
    /// (5%) is left alone rather than forcing rewrites.
    /// </summary>
    public TimeSpan PruneEvery { get; init; } = TimeSpan.FromDays(7);
    /// <summary>Opening the repository, unlocking it, making it. Short, so a store that keeps restic retrying is noticed.</summary>
    public TimeSpan OpenLimit { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>A first backup of a big library over a slow connection takes hours; restic carries on from where a stopped one got to.</summary>
    public TimeSpan BackupLimit { get; init; } = TimeSpan.FromHours(12);
    public TimeSpan ForgetLimit { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan PruneLimit { get; init; } = TimeSpan.FromHours(2);
    public TimeSpan RestoreLimit { get; init; } = TimeSpan.FromHours(12);
    /// <summary>
    /// A rebuild restores all of a household's backup over a home connection, which for a big library
    /// is days. Tried again, it carries on from what has come back already.
    /// </summary>
    public TimeSpan RebuildLimit { get; init; } = TimeSpan.FromDays(4);
    /// <summary>
    /// How much a check of the backup restores and compares. A backup within both is restored whole;
    /// a bigger one is checked by a random sample within them, a different one each time.
    /// </summary>
    public int CheckFiles { get; init; } = 2_000;
    public long CheckBytes { get; init; } = 2L * 1024 * 1024 * 1024;
}

/// <summary>
/// Backs the host up with restic: everybody's files, and a consistent copy of what makes the host
/// itself (the control database and host.key), into the repository it's given. One backup runs at
/// a time; asking for another while one runs gets the one that's running.
/// </summary>
/// <remarks>
/// Where backups go is asked for each time, since a host that joins Uncloud Backup gets somewhere
/// while it runs. Without anywhere, nothing here does anything.
/// </remarks>
public sealed partial class BackupService
{
    // restic picks the snapshot a backup builds on by host and paths. A fixed name keeps that working
    // when the computer is renamed, and the tag tells these from anything else in the repository.
    internal const string HostName = "uncloud";
    internal const string Tag = "uncloud";
    private const string StatusKey = "backup_status";

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly ControlDatabase _database;
    private readonly HostService _host;
    private readonly string _configDirectory;
    private readonly string _directory;
    private readonly Func<BackupTarget?> _target;
    private readonly string _binary;
    private readonly BackupPolicy _policy;
    private readonly ILogger<BackupService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // A restore takes no lock in the repository, so restic can't keep a prune from rewriting what it's
    // reading. On this host, this does: forgetting and pruning wait for a restore, and a restore for
    // them. A backup adding to the repository needs neither, and goes on beside a restore.
    private readonly SemaphoreSlim _maintenance = new(1, 1);
    private volatile bool _running;

    public BackupService(
        ControlDatabase database, HostService host, string configDirectory, BackupTarget? target, string resticBinary,
        BackupPolicy policy, ILogger<BackupService> logger, TimeProvider? time = null)
        : this(database, host, configDirectory, () => target, resticBinary, policy, logger, time)
    {
    }

    public BackupService(
        ControlDatabase database, HostService host, string configDirectory, Func<BackupTarget?> target, string resticBinary,
        BackupPolicy policy, ILogger<BackupService> logger, TimeProvider? time = null)
    {
        _database = database;
        _host = host;
        _configDirectory = configDirectory;
        _directory = Path.Combine(configDirectory, "backup");
        _target = target;
        _binary = resticBinary;
        _policy = policy;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public bool IsConfigured => _target() is not null;

    public BackupStatus Status => Load() with { Configured = IsConfigured, Running = _running };

    /// <summary>Starts a backup in the background unless one is running. Says whether it started one.</summary>
    public bool TryStart(CancellationToken stopping)
    {
        if (!IsConfigured || !_gate.Wait(0)) return false;
        // Said before returning, so whoever asked sees it running straight away.
        _running = true;
        _ = InBackground(stopping);
        return true;
    }

    private async Task InBackground(CancellationToken stopping)
    {
        // Nobody waits on this, so nothing it throws would be heard any other way.
        try { await RunHeldAsync(stopping); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            _logger.LogError(error, "The backup stopped with something unexpected");
        }
    }

    /// <summary>Backs up now and waits for it, unless one is running already, which isn't waited for.</summary>
    public Task<BackupStatus> RunAsync(CancellationToken cancellationToken) =>
        IsConfigured && _gate.Wait(0) ? RunHeldAsync(cancellationToken) : Task.FromResult(Status);

    /// <summary>
    /// This Uncloud's snapshots in the backup, oldest first, and nobody else's that may share the
    /// repository. Read without a lock, so a read-only account still answers.
    /// </summary>
    public Task<IReadOnlyList<BackupSnapshot>> SnapshotsAsync(CancellationToken cancellationToken) =>
        SnapshotsAsync(Open(), cancellationToken);

    private async Task<IReadOnlyList<BackupSnapshot>> SnapshotsAsync(Restic restic, CancellationToken cancellationToken)
    {
        var result = await restic.RunAsync(
            ["--no-lock", "snapshots", "--json", "--host", HostName, "--tag", Tag], _policy.OpenLimit, cancellationToken);
        if (!result.Succeeded) throw new LibraryException(Explain(result, "list the backups", _policy.OpenLimit), "backup");
        using var document = JsonDocument.Parse(string.Join('\n', result.Output));
        return document.RootElement.EnumerateArray()
            .Select(snapshot => new BackupSnapshot(
                snapshot.GetProperty("id").GetString()!,
                snapshot.GetProperty("time").GetDateTimeOffset(),
                snapshot.GetProperty("paths").EnumerateArray().Select(path => path.GetString()!).ToArray()))
            .OrderBy(snapshot => snapshot.Time)
            .ToArray();
    }

    /// <summary>
    /// Restores <paramref name="snapshot"/>, or only the paths in <paramref name="include"/>, into
    /// <paramref name="target"/>, beneath which restic recreates each backed-up path in full. Without a
    /// lock: taking one is a write, and an account that's read-only refuses it.
    /// </summary>
    public async Task RestoreAsync(string snapshot, string target, IReadOnlyList<string>? include, CancellationToken cancellationToken)
    {
        var restic = Open();
        await _maintenance.WaitAsync(cancellationToken);
        try
        {
            // Only one of this Uncloud's own, named in full or by an unambiguous prefix of at least
            // the eight characters restic shows: never another client's that shares the repository.
            var ours = (await SnapshotsAsync(restic, cancellationToken))
                .Where(known => known.Id == snapshot || (snapshot.Length >= 8 && known.Id.StartsWith(snapshot, StringComparison.Ordinal)))
                .ToList();
            if (ours.Count != 1)
                throw new LibraryException("That isn't one of this Uncloud's backups.", "backup_snapshot");
            List<string> arguments = ["--no-lock", "restore", ours[0].Id, "--target", target];
            // Each is a pattern to restic, so a name with [ or * in it is escaped to mean only itself.
            foreach (var path in include ?? []) arguments.AddRange(["--include", Pattern(path)]);
            var result = await restic.RunAsync(arguments, _policy.RestoreLimit, cancellationToken);
            if (!result.Succeeded) throw new LibraryException(Explain(result, "restore from the backup", _policy.RestoreLimit), "backup");
        }
        finally
        {
            _maintenance.Release();
        }
    }

    private async Task<BackupStatus> RunHeldAsync(CancellationToken cancellationToken)
    {
        _running = true;
        try
        {
            var previous = Load();
            var started = _time.GetUtcNow();
            Save(previous with { LastStarted = started });
            _logger.LogInformation("Backing up to {Repository}", _target()?.Repository);
            BackupStatus finished;
            try { finished = await BackUpAsync(previous with { LastStarted = started }, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                finished = previous with
                {
                    LastStarted = started, LastOutcome = BackupOutcome.Failed,
                    Message = "Uncloud was stopped during the backup. The next one carries on from where it got to."
                };
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                _logger.LogWarning(error, "restic couldn't be started");
                finished = previous with
                {
                    LastStarted = started, LastOutcome = BackupOutcome.Failed,
                    Message = _binary == "restic"
                        ? "This build of Uncloud doesn't include restic, and there isn't one installed. Run scripts/fetch-restic.sh."
                        : $"Uncloud couldn't start restic ({_binary})."
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or LibraryException)
            {
                _logger.LogWarning(error, "The backup couldn't be made");
                finished = previous with { LastStarted = started, LastOutcome = BackupOutcome.Failed, Message = error.Message };
            }
            finished = finished with { LastFinished = _time.GetUtcNow() };
            Save(finished);
            _logger.LogInformation("Backup finished: {Outcome}. {Message}", finished.LastOutcome, finished.Message);
            return Status;
        }
        finally
        {
            _running = false;
            _gate.Release();
        }
    }

    private async Task<BackupStatus> BackUpAsync(BackupStatus status, CancellationToken cancellationToken)
    {
        if (_host.RootPath is not { } root)
            return status with { LastOutcome = BackupOutcome.Failed, Message = "There's nothing to back up yet: this Uncloud has no folder." };
        var restic = Open();

        // A lock left by a backup that was stopped would hold up this one. unlock only takes away
        // locks that are stale, so it's safe to ask every time.
        var unlocked = await restic.RunAsync(["unlock"], _policy.OpenLimit, cancellationToken);
        if (!unlocked.Succeeded && !unlocked.NoRepository) return Failure(status, unlocked, "open the backup", _policy.OpenLimit);

        var opened = await restic.RunAsync(["cat", "config"], _policy.OpenLimit, cancellationToken);
        if (opened.NoRepository)
        {
            var made = await restic.RunAsync(["init"], _policy.OpenLimit, cancellationToken);
            if (!made.Succeeded) return Failure(status, made, "make the backup", _policy.OpenLimit);
            _logger.LogInformation("Made a new backup repository at {Repository}", restic.Repository);
        }
        else if (!opened.Succeeded)
        {
            return Failure(status, opened, "open the backup", _policy.OpenLimit);
        }

        var host = StageHost();
        var now = _time.GetUtcNow();
        List<string> arguments =
        [
            "backup", "--json", "--host", HostName, "--tag", Tag,
            // Stamped with the same clock as the status, which restic reads as local time.
            "--time", now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            // What's rebuilt from the files themselves, and uploads still arriving, which are
            // either finished into place by the next backup or thrown away.
            "--exclude", "**/.homebase/index.db", "--exclude", "**/.homebase/index.db-*",
            "--exclude", "**/.homebase/uploads",
            // What's being restored from a backup into somebody's folder, on its way into place.
            "--exclude", "**/.homebase/restoring"
        ];
        var users = Path.Combine(root, UserPaths.UsersDirectory);
        if (Directory.Exists(users)) arguments.Add(users);
        arguments.Add(host);
        var backup = await restic.RunAsync(arguments, _policy.BackupLimit, cancellationToken);
        // restic's 3 means it made a snapshot but couldn't read everything.
        if (!backup.Succeeded && backup.ExitCode != 3) return Failure(status, backup, "back up", _policy.BackupLimit);

        var summary = Summary(backup);
        var unread = backup.Output.Count(line => line.Contains("\"message_type\":\"error\"", StringComparison.Ordinal));
        status = status with
        {
            LastOutcome = backup.ExitCode == 3 ? BackupOutcome.Partial : BackupOutcome.Succeeded,
            Message = backup.ExitCode == 3
                ? $"Backed up, but {unread} {(unread == 1 ? "file" : "files")} couldn't be read and aren't in it. Uncloud's log says which."
                : null,
            LastSucceeded = now,
            Snapshot = Text(summary, "snapshot_id"),
            FilesNew = Number(summary, "files_new"),
            FilesChanged = Number(summary, "files_changed"),
            BytesAdded = Number(summary, "data_added_packed") ?? Number(summary, "data_added"),
            TotalFiles = Number(summary, "total_files_processed"),
            TotalBytes = Number(summary, "total_bytes_processed")
        };
        if (unread > 0)
            foreach (var line in backup.Output.Where(line => line.Contains("\"message_type\":\"error\"", StringComparison.Ordinal)))
                _logger.LogWarning("restic couldn't read: {Line}", line);

        await _maintenance.WaitAsync(cancellationToken);
        try { return await TidyAsync(restic, status, now, cancellationToken); }
        finally { _maintenance.Release(); }
    }

    /// <summary>
    /// Forgets snapshots the policy no longer keeps and, weekly, gives back the space they took. Worth
    /// doing, but not worth failing a backup that's been made over.
    /// </summary>
    private async Task<BackupStatus> TidyAsync(Restic restic, BackupStatus status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var forget = await restic.RunAsync(
        [
            "forget", "--host", HostName, "--tag", Tag,
            "--keep-daily", $"{_policy.KeepDaily}", "--keep-weekly", $"{_policy.KeepWeekly}",
            "--keep-monthly", $"{_policy.KeepMonthly}"
        ], _policy.ForgetLimit, cancellationToken);
        if (!forget.Succeeded)
            return status with { Message = Join(status.Message, $"Older backups weren't tidied away: {Explain(forget, "tidy older backups", _policy.ForgetLimit)}") };

        if (status.LastPruned is { } pruned && now - pruned < _policy.PruneEvery) return status;
        var prune = await restic.RunAsync(["prune"], _policy.PruneLimit, cancellationToken);
        return prune.Succeeded
            ? status with { LastPruned = now }
            : status with { Message = Join(status.Message, $"The space older backups took wasn't given back: {Explain(prune, "give back space", _policy.PruneLimit)}") };
    }

    /// <summary>
    /// What makes this host itself, put where the backup can take it: a copy of the control database
    /// made by SQLite, so it's consistent however busy the host is, and the key its secrets are sealed
    /// with. A new host rebuilt from the backup needs both.
    /// </summary>
    private string StageHost()
    {
        var host = Path.Combine(_directory, "host");
        if (Directory.Exists(host)) Directory.Delete(host, recursive: true);
        Directory.CreateDirectory(host);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var copy = Path.Combine(host, "homebase.db");
        using (var source = _database.Open())
        using (var destination = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }
        Private(copy);
        foreach (var name in new[] { "host.key", "settings.json" })
        {
            var from = Path.Combine(_configDirectory, name);
            if (!File.Exists(from)) continue;
            File.Copy(from, Path.Combine(host, name));
            Private(Path.Combine(host, name));
        }
        return host;
    }

    private static void Private(string file)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private Restic Open()
    {
        var target = _target() ?? throw new LibraryException("Backups aren't set up on this Uncloud.", "backup_not_configured");
        var cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(cache);
        return new Restic(_binary, target, cache, _logger);
    }

    private BackupStatus Failure(BackupStatus status, ResticResult result, string doing, TimeSpan limit)
    {
        // The message is for a person and stays general; what restic said is for whoever reads the log.
        _logger.LogWarning("restic couldn't {Doing} (exit {Code}{TimedOut}): {Said}",
            doing, result.ExitCode, result.TimedOut ? ", timed out" : "", result.Said(5));
        return status with
        {
            LastOutcome = result.TimedOut ? BackupOutcome.TimedOut : result.Refused ? BackupOutcome.Refused : BackupOutcome.Failed,
            Message = Explain(result, doing, limit)
        };
    }

    private static string Explain(ResticResult result, string doing, TimeSpan limit) =>
        result.TimedOut
            ? $"Trying to {doing} took longer than {Describe(limit)}, so it was stopped. The backup storage may not be answering."
            : result.Refused
                ? $"The backup storage refused to let Uncloud {doing}. It may be full, or this Uncloud's key may no longer work."
                : $"Uncloud couldn't {doing}: {result.Said()}";

    private static string Describe(TimeSpan limit) =>
        limit.TotalHours >= 1 ? $"{limit.TotalHours:0.#} hours" : limit.TotalMinutes >= 1 ? $"{limit.TotalMinutes:0.#} minutes" : $"{limit.TotalSeconds:0.#} seconds";

    private static string? Join(string? first, string second) => first is null ? second : $"{first} {second}";

    private static JsonElement? Summary(ResticResult result)
    {
        foreach (var line in result.Output.Reverse())
        {
            if (!line.Contains("\"message_type\":\"summary\"", StringComparison.Ordinal)) continue;
            try { return JsonDocument.Parse(line).RootElement.Clone(); }
            catch (JsonException) { }
        }
        return null;
    }

    private static string? Text(JsonElement? element, string name) =>
        element is { } value && value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    private static long? Number(JsonElement? element, string name) =>
        element is { } value && value.TryGetProperty(name, out var number) && number.TryGetInt64(out var result) ? result : null;

    private BackupStatus Load()
    {
        var saved = _database.Setting(StatusKey);
        if (saved is null) return new BackupStatus();
        try { return JsonSerializer.Deserialize<BackupStatus>(saved, Json) ?? new BackupStatus(); }
        catch (JsonException) { return new BackupStatus(); }
    }

    private void Save(BackupStatus status) =>
        _database.SetSetting(StatusKey, JsonSerializer.Serialize(status with { Configured = false, Running = false }, Json));
}

/// <summary>When the next backup is due.</summary>
public static class BackupSchedule
{
    /// <summary>
    /// The next <paramref name="hour"/> o'clock in <paramref name="zone"/>, nightly. Sooner if the
    /// last backup that worked is more than a day old, so a host that was off overnight catches up
    /// soon after it's back, but not if one was tried in the last few hours, so a backup that keeps
    /// failing isn't retried over and over.
    /// </summary>
    public static DateTimeOffset Next(
        DateTimeOffset now, TimeZoneInfo zone, int hour, DateTimeOffset? lastSucceeded, DateTimeOffset? lastStarted)
    {
        var overdue = lastSucceeded is null || now - lastSucceeded.Value > TimeSpan.FromHours(26);
        var triedLately = lastStarted is { } started && now - started < TimeSpan.FromHours(6);
        if (overdue && !triedLately) return now + TimeSpan.FromMinutes(5);

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var tonight = new DateTime(local.Year, local.Month, local.Day, hour, 0, 0, DateTimeKind.Unspecified);
        var next = new DateTimeOffset(tonight, zone.GetUtcOffset(tonight));
        if (next <= now) next = new DateTimeOffset(tonight.AddDays(1), zone.GetUtcOffset(tonight.AddDays(1)));
        return next;
    }
}
