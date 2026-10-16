using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Backup;

/// <summary>
/// How the last check of the backup went: what was restored from it, and how that compared with
/// what's here. Only counts, never names: what's in everybody's files isn't the administrator's to
/// read, and checking the backup mustn't make it so.
/// </summary>
public sealed record BackupCheck
{
    public bool Running { get; init; }
    public DateTimeOffset? Started { get; init; }
    public DateTimeOffset? Finished { get; init; }
    /// <summary>Whether everything restored came back as it was. Null when there was nothing to check.</summary>
    public bool? Passed { get; init; }
    /// <summary>What happened, for a person, when there's anything to say.</summary>
    public string? Message { get; init; }
    public string? Snapshot { get; init; }
    public DateTimeOffset? SnapshotTime { get; init; }
    /// <summary>Everybody's files in the backup, and how many of them this check restored.</summary>
    public long? FilesInBackup { get; init; }
    public long? BytesInBackup { get; init; }
    public long? Files { get; init; }
    public long? Bytes { get; init; }
    /// <summary>Whether only a sample was restored, the backup being bigger than a check restores.</summary>
    public bool Sampled { get; init; }
    /// <summary>Restored, and the same byte for byte as the file here.</summary>
    public long? Matched { get; init; }
    /// <summary>Changed here since the backup, so there's nothing the same to compare with.</summary>
    public long? ChangedSince { get; init; }
    /// <summary>Gone from here since the backup.</summary>
    public long? GoneSince { get; init; }
    /// <summary>Restored, but not the same as a file here that hasn't changed since: the backup is wrong.</summary>
    public long? Mismatched { get; init; }
    /// <summary>In the backup, asked for, and not restored.</summary>
    public long? Missing { get; init; }
    /// <summary>Whether host.key came back as it is: a host rebuilt from the backup needs it to open its secrets.</summary>
    public bool? HostKeyMatched { get; init; }
}

public sealed partial class BackupService
{
    private const string CheckKey = "backup_check";
    private readonly SemaphoreSlim _checking = new(1, 1);
    private volatile bool _checkRunning;

    /// <summary>One file in a snapshot, as restic lists it.</summary>
    private sealed record SnapshotFile(string Path, long Size, DateTimeOffset Modified);

    public BackupCheck LastCheck => LoadCheck() with { Running = _checkRunning };

    /// <summary>Starts a check in the background unless one is running. Says whether it started one.</summary>
    public bool TryStartCheck(CancellationToken stopping)
    {
        if (!IsConfigured || !_checking.Wait(0)) return false;
        _checkRunning = true;
        _ = CheckInBackground(stopping);
        return true;
    }

    /// <summary>Checks the backup now and waits for it, unless a check is running already, which isn't waited for.</summary>
    public Task<BackupCheck> CheckAsync(CancellationToken cancellationToken) =>
        IsConfigured && _checking.Wait(0) ? CheckHeldAsync(cancellationToken) : Task.FromResult(LastCheck);

    private async Task CheckInBackground(CancellationToken stopping)
    {
        try { await CheckHeldAsync(stopping); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            _logger.LogError(error, "The backup check stopped with something unexpected");
        }
    }

    private async Task<BackupCheck> CheckHeldAsync(CancellationToken cancellationToken)
    {
        _checkRunning = true;
        try
        {
            var started = _time.GetUtcNow();
            BackupCheck result;
            try { result = await RunCheckAsync(started, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = new BackupCheck { Started = started, Passed = false, Message = "Uncloud was stopped during the check." };
            }
            catch (Win32Exception error)
            {
                _logger.LogWarning(error, "restic couldn't be started");
                result = new BackupCheck { Started = started, Passed = false, Message = $"Uncloud couldn't start restic ({_binary})." };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or LibraryException or JsonException)
            {
                _logger.LogWarning(error, "The backup couldn't be checked");
                result = new BackupCheck { Started = started, Passed = false, Message = error.Message };
            }
            result = result with { Finished = _time.GetUtcNow() };
            SaveCheck(result);
            _logger.LogInformation("Backup check finished: {Passed}. {Matched} matched, {Mismatched} different, {Missing} missing",
                result.Passed, result.Matched, result.Mismatched, result.Missing);
            return result;
        }
        finally
        {
            _checkRunning = false;
            _checking.Release();
        }
    }

    /// <summary>
    /// Restores the newest backup, or a sample of it, somewhere of its own, and compares what comes
    /// back with what's here, byte for byte. What's restored is taken away again however it ends.
    /// </summary>
    private async Task<BackupCheck> RunCheckAsync(DateTimeOffset started, CancellationToken cancellationToken)
    {
        var restic = Open();
        var nothing = new BackupCheck { Started = started, Message = "There's no backup to check yet." };
        var opened = await restic.RunAsync(["--no-lock", "cat", "config"], _policy.OpenLimit, cancellationToken);
        if (opened.NoRepository) return nothing;
        if (!opened.Succeeded) throw new LibraryException(Explain(opened, "open the backup", _policy.OpenLimit), "backup");
        var snapshots = await SnapshotsAsync(restic, cancellationToken);
        if (snapshots.Count == 0) return nothing;
        var snapshot = snapshots[^1];

        var listed = await restic.RunAsync(["--no-lock", "ls", "--json", snapshot.Id], _policy.ForgetLimit, cancellationToken);
        if (!listed.Succeeded) throw new LibraryException(Explain(listed, "read what's in the backup", _policy.ForgetLimit), "backup");
        var files = SnapshotFiles(listed.Output);
        var staged = Path.Combine(_directory, "host");
        var hostKey = files.FirstOrDefault(file => file.Path == Path.Combine(staged, "host.key"));
        var everyone = files.Where(file => !Under(file.Path, staged)).ToList();
        var total = everyone.Sum(file => file.Size);
        var whole = everyone.Count <= _policy.CheckFiles && total <= _policy.CheckBytes;
        var chosen = whole ? everyone : Sample(everyone);
        var result = new BackupCheck
        {
            Started = started, Snapshot = snapshot.Id, SnapshotTime = snapshot.Time,
            FilesInBackup = everyone.Count, BytesInBackup = total,
            Files = chosen.Count, Bytes = chosen.Sum(file => file.Size), Sampled = !whole
        };

        var scratch = Path.Combine(_directory, "check");
        var patterns = Path.Combine(_directory, "check-include.txt");
        Clear(scratch);
        Directory.CreateDirectory(scratch);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            // Room for all of it, and a little over: a check mustn't be what fills the drive.
            var needed = (whole ? files.Sum(file => file.Size) : result.Bytes!.Value + (hostKey?.Size ?? 0)) + 64L * 1024 * 1024;
            if (new DriveInfo(Path.GetPathRoot(scratch)!).AvailableFreeSpace < needed)
                return result with { Passed = false, Message = "There isn't room on this computer to restore what the check needs. Free some space, then try again." };

            await _maintenance.WaitAsync(cancellationToken);
            try
            {
                List<string> arguments = ["--no-lock", "restore", snapshot.Id, "--target", scratch];
                if (!whole)
                {
                    var asked = hostKey is null ? chosen : chosen.Append(hostKey);
                    File.WriteAllLines(patterns, asked.Select(file => Pattern(file.Path)));
                    arguments.AddRange(["--include-file", patterns]);
                }
                var restored = await restic.RunAsync(arguments, _policy.RestoreLimit, cancellationToken);
                if (!restored.Succeeded)
                    return result with { Passed = false, Message = Explain(restored, "restore from the backup", _policy.RestoreLimit) };
            }
            finally
            {
                _maintenance.Release();
                if (File.Exists(patterns)) File.Delete(patterns);
            }

            long matched = 0, changed = 0, gone = 0, mismatched = 0, missing = 0;
            foreach (var file in chosen)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var back = Restored(scratch, file.Path);
                var here = new FileInfo(file.Path);
                if (!File.Exists(back))
                {
                    missing++;
                    _logger.LogWarning("The backup check didn't get back {Path}", file.Path);
                }
                else if (!here.Exists) gone++;
                else if (here.Length != file.Size || (here.LastWriteTimeUtc - file.Modified.UtcDateTime).Duration() > TimeSpan.FromMilliseconds(1)) changed++;
                else if (await SameAsync(back, here.FullName, cancellationToken)) matched++;
                else
                {
                    mismatched++;
                    _logger.LogWarning("The backup check got back {Path} different from the file here, which hasn't changed since", file.Path);
                }
            }
            bool? keyMatched = hostKey is null ? null
                : File.Exists(Path.Combine(_configDirectory, "host.key")) && File.Exists(Restored(scratch, hostKey.Path))
                  && await SameAsync(Restored(scratch, hostKey.Path), Path.Combine(_configDirectory, "host.key"), cancellationToken);

            result = result with
            {
                Matched = matched, ChangedSince = changed, GoneSince = gone, Mismatched = mismatched, Missing = missing,
                HostKeyMatched = keyMatched,
                Passed = mismatched == 0 && missing == 0 && keyMatched != false
            };
            return keyMatched == false
                ? result with { Message = "host.key didn't come back as it is, so a host rebuilt from this backup couldn't open its own secrets." }
                : result;
        }
        finally
        {
            Clear(scratch);
        }
    }

    /// <summary>
    /// Files the check can ask for by name, at random, within the policy. A name restic would read
    /// as something else in a pattern file isn't asked for: one with a $ in it is taken for a variable.
    /// </summary>
    private List<SnapshotFile> Sample(List<SnapshotFile> files)
    {
        var candidates = files.Where(file => Askable(file.Path)).ToArray();
        RandomNumberGenerator.Shuffle(candidates.AsSpan());
        var chosen = new List<SnapshotFile>();
        long bytes = 0;
        foreach (var file in candidates)
        {
            if (chosen.Count >= _policy.CheckFiles) break;
            if (bytes + file.Size > _policy.CheckBytes) continue;
            chosen.Add(file);
            bytes += file.Size;
        }
        return chosen;
    }

    private static bool Askable(string path) =>
        !path.Contains('$') && !path.Contains('\n') && !path.Contains('\r') && path == path.Trim();

    /// <summary>A path as a restic pattern that matches it and nothing else.</summary>
    private static string Pattern(string path) => Regex.Replace(path, @"[\\*?\[]", @"\$0");

    /// <summary>Where restic puts a backed-up path when restoring into <paramref name="target"/>: the whole path, beneath it.</summary>
    private static string Restored(string target, string path) => Path.Combine(target, path.TrimStart('/'));

    private static bool Under(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static List<SnapshotFile> SnapshotFiles(IReadOnlyList<string> lines)
    {
        var files = new List<SnapshotFile>();
        foreach (var line in lines)
        {
            if (!line.Contains("\"struct_type\":\"node\"", StringComparison.Ordinal)) continue;
            using var node = JsonDocument.Parse(line);
            var root = node.RootElement;
            if (root.GetProperty("type").GetString() != "file") continue;
            files.Add(new SnapshotFile(
                root.GetProperty("path").GetString()!,
                root.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                Time(root.GetProperty("mtime").GetString()!)));
        }
        return files;
    }

    /// <summary>restic's times carry nanoseconds, two places more than .NET reads.</summary>
    private static DateTimeOffset Time(string text) =>
        DateTimeOffset.Parse(Regex.Replace(text, @"(\.\d{7})\d+", "$1"), CultureInfo.InvariantCulture);

    private static async Task<bool> SameAsync(string one, string other, CancellationToken cancellationToken)
    {
        if (new FileInfo(one).Length != new FileInfo(other).Length) return false;
        await using var first = File.OpenRead(one);
        await using var second = File.OpenRead(other);
        var a = await SHA256.HashDataAsync(first, cancellationToken);
        var b = await SHA256.HashDataAsync(second, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static void Clear(string folder)
    {
        if (!Directory.Exists(folder)) return;
        // restic restores files and folders as they were, read-only ones included, which Delete
        // would refuse.
        if (!OperatingSystem.IsWindows())
            foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, recursive: true);
    }

    private BackupCheck LoadCheck()
    {
        var saved = _database.Setting(CheckKey);
        if (saved is null) return new BackupCheck();
        try { return JsonSerializer.Deserialize<BackupCheck>(saved, Json) ?? new BackupCheck(); }
        catch (JsonException) { return new BackupCheck(); }
    }

    private void SaveCheck(BackupCheck check) =>
        _database.SetSetting(CheckKey, JsonSerializer.Serialize(check with { Running = false }, Json));
}
