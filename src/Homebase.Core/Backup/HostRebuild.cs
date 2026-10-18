using System.Text.Json;
using Homebase.Core.Accounts;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Backup;

/// <summary>
/// Rebuilding this host from a backup: how far it has got, and, on a host that was rebuilt, which
/// backup it came from. Counts only, as a check's are: what's in everybody's files isn't the
/// administrator's to read.
/// </summary>
public sealed record BackupRebuild
{
    public bool Running { get; init; }
    /// <summary>
    /// restoring, then preparing, then restarting: everything is ready, and Uncloud is about to start
    /// again as the host in the backup.
    /// </summary>
    public string? Stage { get; init; }
    public DateTimeOffset? Started { get; init; }
    public string? Snapshot { get; init; }
    public DateTimeOffset? SnapshotTime { get; init; }
    /// <summary>Everything in the backup, and how much of it has come back so far.</summary>
    public long? Files { get; init; }
    public long? Bytes { get; init; }
    public long? FilesRestored { get; init; }
    public long? BytesRestored { get; init; }
    /// <summary>Why the last rebuild stopped short, for a person. Nothing on this host had changed.</summary>
    public string? Message { get; init; }
    /// <summary>When this host was rebuilt from a backup, and when that backup was made.</summary>
    public DateTimeOffset? RebuiltAt { get; init; }
    public DateTimeOffset? RebuiltFrom { get; init; }
}

/// <summary>
/// A rebuild made ready, waiting for Uncloud to start again: where this host keeps everybody's files,
/// the folder of them restored from the backup (none, when nobody had any), and where the rest of what
/// was restored is. <paramref name="Step"/> is how far putting it in place has got.
/// </summary>
internal sealed record PendingRebuild(string Root, string? Users, string Staging, string Stamp, string Step);

/// <summary>
/// Putting a host rebuilt from its backup in place of this one. That's everything that makes a host:
/// its database, the host.key everything in the database is sealed with, and everybody's files. The
/// first two are open for as long as Uncloud runs, so they're swapped when it next starts, before
/// anything opens them. What was here goes aside rather than away.
/// </summary>
public static class HostRebuild
{
    /// <summary>In the config directory: says a rebuild is ready, and how far putting it in place has got.</summary>
    internal const string MarkerName = "rebuild.json";
    /// <summary>In the config directory: the database and host.key from the backup, made ready.</summary>
    internal const string ReadyName = "rebuild";
    /// <summary>In the host's folder: everything restored from the backup, on its way into place.</summary>
    internal const string StagingName = ".uncloud-rebuild";
    /// <summary>The host setting that says, on a rebuilt host, which backup it was rebuilt from.</summary>
    internal const string RebuiltKey = "backup_rebuilt";

    private static readonly string[] Database = ["homebase.db", "homebase.db-wal", "homebase.db-shm"];
    private static readonly string[] Host = [.. Database, "host.key"];

    /// <summary>When and from which backup this host was rebuilt, as kept in its settings.</summary>
    internal sealed record Rebuilt(DateTimeOffset At, DateTimeOffset From, string Snapshot);

    /// <summary>
    /// Puts a rebuild that's ready in place. Called as Uncloud starts, before anything opens the
    /// database or host.key it replaces. Each step is written down once it's done, so a start that's
    /// interrupted carries on from where it got to; anything that goes wrong stops Uncloud starting,
    /// rather than leave it running as half one host and half the other. So does the host's folder not
    /// being there, as an external drive isn't for a moment as a computer starts: the accounts from
    /// the backup without everybody's files would be no use, and whatever starts Uncloud again tries
    /// again until the drive is back. Says whether there was one.
    /// </summary>
    public static bool ApplyPending(string configDirectory, ILogger logger)
    {
        var marker = Path.Combine(configDirectory, MarkerName);
        if (!File.Exists(marker)) return false;
        var pending = JsonSerializer.Deserialize<PendingRebuild>(File.ReadAllText(marker))
            ?? throw new InvalidDataException($"{marker} doesn't say what to rebuild.");
        var ready = Path.Combine(configDirectory, ReadyName);
        var aside = Path.Combine(configDirectory, $"before-rebuild-{pending.Stamp}");
        var users = Path.Combine(pending.Root, UserPaths.UsersDirectory);
        var usersAside = Path.Combine(pending.Root, $".uncloud-before-rebuild-{pending.Stamp}", UserPaths.UsersDirectory);
        logger.LogWarning("Putting this Uncloud's rebuild from its backup in place (from {Step})", pending.Step);
        if (pending.Step != "placed" && !Directory.Exists(pending.Root))
        {
            logger.LogError("The folder this Uncloud keeps everybody's files in ({Root}) isn't there, so the rebuild from the backup waits for it", pending.Root);
            throw new DirectoryNotFoundException(
                $"The folder this Uncloud keeps everybody's files in ({pending.Root}) isn't there, so the rebuild from the backup can't be put in place yet.");
        }

        if (pending.Step == "ready")
        {
            if (!File.Exists(Path.Combine(ready, "homebase.db")) || !File.Exists(Path.Combine(ready, "host.key"))
                || (pending.Users is { } restored && !Directory.Exists(restored)))
            {
                // Nothing has changed yet, and without these nothing can: this host starts as it was.
                logger.LogError("The rebuild from the backup isn't all there any more, so it was given up; this Uncloud starts as it was");
                File.Delete(marker);
                return false;
            }
            // This host's place in Uncloud Backup goes on in the rebuilt one: the place in the backup
            // was the lost host's, which taking its place revoked.
            var carried = File.Exists(Path.Combine(configDirectory, "homebase.db")) && BackupAccount.CarryOver(
                new ControlDatabase(configDirectory), new SecretProtector(configDirectory),
                new ControlDatabase(ready), new SecretProtector(ready));
            if (!carried) new ControlDatabase(ready).RemoveSetting(BackupAccount.Key);
            logger.LogInformation(carried
                ? "This Uncloud's place in Uncloud Backup carries on in the rebuilt one"
                : "This Uncloud had no place in Uncloud Backup to carry on in the rebuilt one");
            pending = Advance(marker, pending, "carried");
        }
        if (pending.Step == "carried")
        {
            Directory.CreateDirectory(aside);
            Private(aside);
            foreach (var name in Host) Move(Path.Combine(configDirectory, name), Path.Combine(aside, name));
            pending = Advance(marker, pending, "aside");
        }
        if (pending.Step == "aside")
        {
            foreach (var name in Host) Move(Path.Combine(ready, name), Path.Combine(configDirectory, name));
            pending = Advance(marker, pending, "in");
        }
        if (pending.Step == "in")
        {
            if (Directory.Exists(users))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(usersAside)!);
                Private(Path.GetDirectoryName(usersAside)!);
                Directory.Move(users, usersAside);
            }
            pending = Advance(marker, pending, "users aside");
        }
        if (pending.Step == "users aside")
        {
            if (pending.Users is { } restored)
            {
                // The accounts are already this host's. Starting without the files that belong to them
                // would be the half-finished rebuild this refuses, so a folder that isn't there yet waits.
                if (!Directory.Exists(restored))
                {
                    logger.LogError("The folder of everybody's files from the backup ({Users}) isn't there, so the rebuild waits for it", restored);
                    throw new DirectoryNotFoundException(
                        $"The folder of everybody's files from the backup ({restored}) isn't there, so the rebuild can't be put in place yet.");
                }
                Directory.Move(restored, users);
            }
            pending = Advance(marker, pending, "placed");
        }
        if (pending.Step != "placed")
            throw new InvalidDataException($"{marker} says the rebuild got to “{pending.Step}”, which isn't a step.");

        // What's left of what was restored is only what the backup held of the host itself, now in
        // place. Not worth failing to start over.
        foreach (var leftover in new[] { pending.Staging, ready })
        {
            try
            {
                if (!Directory.Exists(leftover)) continue;
                BackupService.MakeDeletable(leftover);
                Directory.Delete(leftover, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(error, "Couldn't tidy away {Folder} after the rebuild", leftover);
            }
        }
        File.Delete(marker);
        logger.LogWarning("This Uncloud is now the one in its backup. What it had before is in {Aside}", aside);
        return true;
    }

    /// <summary>Says a rebuild is ready, for Uncloud to put in place when it next starts.</summary>
    internal static void MarkReady(string configDirectory, PendingRebuild pending) =>
        Write(Path.Combine(configDirectory, MarkerName), pending with { Step = "ready" });

    private static PendingRebuild Advance(string marker, PendingRebuild pending, string step)
    {
        var next = pending with { Step = step };
        Write(marker, next);
        return next;
    }

    /// <summary>Written whole or not at all: a marker half written would leave nobody knowing how far this got.</summary>
    private static void Write(string marker, PendingRebuild pending)
    {
        var temporary = $"{marker}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(pending));
        Private(temporary);
        File.Move(temporary, marker, overwrite: true);
    }

    /// <summary>Moves a file that's there to where nothing is yet. Done again, it does nothing.</summary>
    private static void Move(string from, string to)
    {
        if (File.Exists(from)) File.Move(from, to);
    }

    private static void Private(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, Directory.Exists(path)
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
