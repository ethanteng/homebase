using System.ComponentModel;
using System.Text.Json;
using Homebase.Core.Accounts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Backup;

public sealed partial class BackupService
{
    private readonly Lock _rebuildLock = new();
    private BackupRebuild _rebuild = new();

    public BackupRebuild LastRebuild
    {
        get
        {
            BackupRebuild current;
            lock (_rebuildLock) current = _rebuild;
            var rebuilt = Rebuilt();
            return current with { RebuiltAt = rebuilt?.At, RebuiltFrom = rebuilt?.From };
        }
    }

    /// <summary>
    /// Starts rebuilding this host from one of its backups, in the background, unless a rebuild is
    /// under way already. Everybody's accounts and files come back as they were in it, and once all of
    /// it is here and ready, <paramref name="restart"/> is asked to stop Uncloud, which starts again
    /// as that host. No backup is made from here on: one now would be of the host being replaced.
    /// </summary>
    public bool TryStartRebuild(string snapshot, Action restart, CancellationToken stopping)
    {
        lock (_rebuildLock)
        {
            if (_rebuild.Running) return false;
            if (!IsConfigured) throw new LibraryException("Backups aren't set up on this Uncloud.", "backup_not_configured");
            if (!_gate.Wait(0))
                throw new LibraryException("A backup is being made. Rebuild once it has finished.", "conflict");
            _rebuild = new BackupRebuild { Running = true, Stage = "restoring", Started = _time.GetUtcNow() };
        }
        _ = RebuildInBackground(snapshot, restart, stopping);
        return true;
    }

    private async Task RebuildInBackground(string snapshot, Action restart, CancellationToken stopping)
    {
        if (await RebuildOrSayWhyNotAsync(snapshot, stopping) is { } message)
        {
            // Nothing has been put in place, and backing up can go on. What came back from the backup
            // is kept, out of everybody's way, for trying again to carry on from.
            try
            {
                Clear(Path.Combine(_configDirectory, HostRebuild.ReadyName));
                if (_host.RootPath is { } root && Directory.Exists(Path.Combine(root, HostRebuild.StagingName))
                    && Directory.EnumerateFileSystemEntries(Path.Combine(root, HostRebuild.StagingName)).Any())
                    message += " What came back so far is kept, so trying again carries on from there.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Got ready for a rebuild nobody will put in place: nothing to stop backing up for.
                _logger.LogWarning(error, "Couldn't tidy away a rebuild that didn't finish");
            }
            Report(rebuild => rebuild with { Running = false, Stage = null, Message = message });
            _gate.Release();
            return;
        }
        // Ready, and said to be: from here it's put in place whenever Uncloud next starts, whatever
        // stops it. The backup stays held until then.
        Report(rebuild => rebuild with { Stage = "restarting" });
        _logger.LogWarning("Everything from the backup is ready. Uncloud stops now, to start again as the host in it");
        restart();
    }

    /// <summary>
    /// Takes away what a rebuild that didn't finish got back, once it isn't wanted: this host is
    /// backing up as itself instead. In the background, since it can be all of a household's files.
    /// </summary>
    public void ForgetRebuild()
    {
        lock (_rebuildLock)
        {
            if (_rebuild.Running) return;
            _rebuild = new BackupRebuild();
        }
        if (_host.RootPath is not { } root || !Directory.Exists(Path.Combine(root, HostRebuild.StagingName))) return;
        _ = Task.Run(() =>
        {
            try { Clear(Path.Combine(root, HostRebuild.StagingName)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(error, "Couldn't take away what a rebuild that didn't finish got back");
            }
        });
    }

    /// <summary>Gets the rebuild ready, or says why it couldn't be, for a person.</summary>
    private async Task<string?> RebuildOrSayWhyNotAsync(string snapshot, CancellationToken stopping)
    {
        string message;
        try
        {
            await RebuildAsync(snapshot, stopping);
            return null;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            message = "Uncloud was stopped before the rebuild was finished. Nothing here has changed.";
        }
        catch (Win32Exception error)
        {
            _logger.LogWarning(error, "restic couldn't be started");
            message = $"Uncloud couldn't start restic ({_binary}). Nothing here has changed.";
        }
        catch (LibraryException error)
        {
            message = error.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or SqliteException)
        {
            // What went wrong can name somebody's file.
            _logger.LogWarning(error, "The rebuild from the backup stopped short");
            message = "Uncloud couldn't finish getting everything ready from the backup. Nothing here has changed. Uncloud's log says why.";
        }
        catch (Exception error)
        {
            _logger.LogError(error, "The rebuild from the backup stopped with something unexpected");
            message = "Uncloud couldn't finish getting everything ready from the backup. Nothing here has changed. Uncloud's log says why.";
        }
        return message;
    }

    /// <summary>
    /// Restores the whole of one backup into the host's folder, out of everybody's way, and gets the
    /// database and host.key in it ready to be put in place: they're the lost host's, but this one's
    /// folder, and nothing of the computers that synced with that host, which this one isn't.
    /// </summary>
    private async Task RebuildAsync(string snapshot, CancellationToken cancellationToken)
    {
        var root = _host.RootPath
            ?? throw new LibraryException("This Uncloud doesn't have a folder yet. Choose one, then rebuild.", "not_configured");
        var restic = Open();
        var staging = Path.Combine(root, HostRebuild.StagingName);
        BackupSnapshot chosen;
        string host, target;
        string? users;
        // Held from choosing the backup until it's restored, as a restore is: tidying the backup
        // meanwhile could take away what's being read.
        await _maintenance.WaitAsync(cancellationToken);
        try
        {
            var ours = (await SnapshotsAsync(restic, cancellationToken))
                .Where(known => known.Id == snapshot || (snapshot.Length >= 8 && known.Id.StartsWith(snapshot, StringComparison.Ordinal)))
                .ToList();
            if (ours.Count != 1) throw new LibraryException("That isn’t one of this Uncloud’s backups.", "not_found");
            chosen = ours[0];
            // Where the host's own things were staged when this backup was made (StageHost).
            host = chosen.Paths.Select(path => path.TrimEnd('/')).FirstOrDefault(path =>
                    Path.GetFileName(path) == "host" && Path.GetFileName(Path.GetDirectoryName(path)) == "backup")
                ?? throw new LibraryException(
                    "This backup is from before Uncloud kept its accounts in its backups, so it can’t rebuild this Uncloud. "
                    + "Get back what you need from Backups instead.", "not_found");
            users = chosen.Paths.Select(path => path.TrimEnd('/')).FirstOrDefault(path => Path.GetFileName(path) == UserPaths.UsersDirectory);

            var measured = await restic.RunAsync(["--no-lock", "stats", "--json", "--mode", "restore-size", chosen.Id], _policy.ForgetLimit, cancellationToken);
            if (!measured.Succeeded) throw Unreadable(measured, "measure the backup", _policy.ForgetLimit);
            // What came back from another backup is no use to this one; what came back from this one, on
            // a go that didn't finish, is where this one carries on from.
            target = Path.Combine(staging, chosen.Id);
            if (Directory.Exists(staging))
                foreach (var other in Directory.EnumerateFileSystemEntries(staging).Where(entry => entry != target).ToList())
                {
                    if (Directory.Exists(other)) Clear(other);
                    else File.Delete(other);
                }
            using (var stats = JsonDocument.Parse(string.Join('\n', measured.Output)))
            {
                var bytes = stats.RootElement.GetProperty("total_size").GetInt64();
                var files = stats.RootElement.GetProperty("total_file_count").GetInt64();
                Report(rebuild => rebuild with { Snapshot = chosen.Id, SnapshotTime = chosen.Time, Files = files, Bytes = bytes });
                var back = Directory.Exists(target)
                    ? new DirectoryInfo(target).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(file => file.Length)
                    : 0;
                var free = Storage.For(root)?.FreeBytes;
                if (!Storage.Fits(Math.Max(0, bytes - back), free))
                    throw new LibraryException(
                        $"There isn’t room for everything in the backup: it needs {Storage.Describe(bytes)}, and "
                        + $"{Storage.Describe(Math.Max(0, Storage.Room(free) ?? 0))} is free on the drive this Uncloud keeps its files on.",
                        "insufficient_storage");
            }

            foreach (var folder in new[] { staging, target })
            {
                Directory.CreateDirectory(folder);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            // A file already back as it is in the backup, by its size and time, isn't fetched again.
            var restored = await restic.RunAsync(
                ["--no-lock", "restore", "--json", "--overwrite", "if-changed", chosen.Id, "--target", target],
                _policy.RebuildLimit, cancellationToken, Progress);
            if (!restored.Succeeded) throw Unreadable(restored, "restore from the backup", _policy.RebuildLimit);
        }
        finally
        {
            _maintenance.Release();
        }

        Report(rebuild => rebuild with { Stage = "preparing" });
        var stagedHost = Restored(target, host);
        var key = Path.Combine(stagedHost, "host.key");
        if (!File.Exists(Path.Combine(stagedHost, "homebase.db")) || !File.Exists(key) || new FileInfo(key).Length != 32)
            throw new LibraryException(
                "This backup doesn’t have everything a rebuild needs: the accounts or the key that opens them are missing from it.", "not_found");

        // Beside the database it replaces, so putting it in place is only a rename.
        var ready = Path.Combine(_configDirectory, HostRebuild.ReadyName);
        Clear(ready);
        Directory.CreateDirectory(ready);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(ready, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var name in new[] { "homebase.db", "host.key" })
        {
            File.Copy(Path.Combine(stagedHost, name), Path.Combine(ready, name));
            Private(Path.Combine(ready, name));
        }
        var rebuilt = new ControlDatabase(ready);
        using (var connection = rebuilt.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM users WHERE is_admin = 1 AND disabled_at IS NULL";
            if (Convert.ToInt64(command.ExecuteScalar()) == 0)
                throw new LibraryException("This backup has nobody in it who could look after this Uncloud, so it can’t rebuild it.", "not_found");
            // Paired computers are paired with Syncthing on the lost host, which a backup doesn't hold:
            // each is paired again with this one. Codes made there for pairing go with them.
            command.CommandText = "DELETE FROM sync_folders; DELETE FROM sync_devices; DELETE FROM pairing_codes;";
            command.ExecuteNonQuery();
        }
        rebuilt.SetSetting(HostService.RootKey, root);
        rebuilt.SetSetting(HostRebuild.RebuiltKey, JsonSerializer.Serialize(new HostRebuild.Rebuilt(_time.GetUtcNow(), chosen.Time, chosen.Id)));

        var restoredUsers = users is null ? null : Restored(target, users);
        HostRebuild.MarkReady(_configDirectory, new PendingRebuild(
            root, restoredUsers is not null && Directory.Exists(restoredUsers) ? restoredUsers : null, staging,
            _time.GetUtcNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture), "ready"));
        _logger.LogWarning("Rebuild from backup {Snapshot} of {Time} is ready", chosen.Id, chosen.Time);
    }

    /// <summary>How far restic has got restoring, as it says.</summary>
    private void Progress(string line)
    {
        try
        {
            using var status = JsonDocument.Parse(line);
            var root = status.RootElement;
            long? Number(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
            Report(rebuild => rebuild with
            {
                FilesRestored = Number("files_restored") ?? rebuild.FilesRestored,
                BytesRestored = Number("bytes_restored") ?? rebuild.BytesRestored
            });
        }
        catch (JsonException)
        {
            // Only progress: the restore goes on without it.
        }
    }

    private void Report(Func<BackupRebuild, BackupRebuild> change)
    {
        lock (_rebuildLock) _rebuild = change(_rebuild);
    }

    private HostRebuild.Rebuilt? Rebuilt()
    {
        var saved = _database.Setting(HostRebuild.RebuiltKey);
        if (saved is null) return null;
        try { return JsonSerializer.Deserialize<HostRebuild.Rebuilt>(saved); }
        catch (JsonException) { return null; }
    }
}
