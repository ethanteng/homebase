using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Backup;

/// <summary>
/// Where a host's backups go: a restic repository, the password that opens it, and the key for the
/// storage it's in, when it's in S3-compatible storage rather than a folder.
/// </summary>
public sealed record BackupTarget(
    string Repository, string Password, string? AccessKeyId = null, string? SecretAccessKey = null, string? Region = null)
{
    public bool IsS3 => Repository.StartsWith("s3:", StringComparison.Ordinal);
}

/// <summary>What one restic command did.</summary>
public sealed record ResticResult(int ExitCode, IReadOnlyList<string> Output, string Error, bool TimedOut, TimeSpan Took)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    /// <summary>restic's own exit code for a repository that isn't there yet.</summary>
    public bool NoRepository => !TimedOut && ExitCode == 10;

    /// <summary>
    /// The storage said no on purpose. It doesn't say why: an account over its quota answers this
    /// way, but so does a key that no longer works, so nothing here guesses which.
    /// </summary>
    public bool Refused => !TimedOut && (Error.Contains("403", StringComparison.Ordinal)
        || Error.Contains("AccessDenied", StringComparison.Ordinal)
        || Error.Contains("Access Denied", StringComparison.OrdinalIgnoreCase));

    /// <summary>The last few things restic said, on one line, for a message.</summary>
    public string Said(int lines = 2)
    {
        var said = Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(lines);
        var text = string.Join(" / ", said);
        return text.Length > 0 ? text : $"exit {ExitCode}";
    }
}

/// <summary>
/// restic, pointed at one repository. Each command runs to a time limit, because some answers
/// from storage make restic retry for many minutes before it gives up (iDrive's 405 for an object
/// behind a delete marker, for one), and a backup that waits forever is worse than one that fails.
/// </summary>
public sealed class Restic(string binary, BackupTarget target, string cacheDirectory, ILogger logger)
{
    /// <summary>
    /// A path somebody configured, then the copy Uncloud ships beside itself, then whatever is on the
    /// PATH, the same order Syncthing is found in.
    /// </summary>
    public static string Binary(string? configured, string applicationDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var bundled = Path.Combine(applicationDirectory, OperatingSystem.IsWindows() ? "restic.exe" : "restic");
        return File.Exists(bundled) ? bundled : "restic";
    }

    public string Executable => binary;

    public string Repository => target.Repository;

    /// <summary>
    /// Runs restic with <paramref name="arguments"/> and waits up to <paramref name="limit"/>. Past
    /// it, restic and anything it started are stopped and the result says it timed out. A
    /// cancellation from the caller stops restic too, and is passed on.
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">There's no restic to run.</exception>
    public async Task<ResticResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Nothing in the environment Uncloud was started from decides which repository or which key
        // this uses: a RESTIC_PASSWORD_FILE or AWS_PROFILE left in somebody's shell would otherwise win.
        foreach (var name in start.Environment.Keys
                     .Where(name => name.StartsWith("RESTIC_", StringComparison.Ordinal) || name.StartsWith("AWS_", StringComparison.Ordinal))
                     .ToList())
            start.Environment.Remove(name);
        start.Environment["RESTIC_REPOSITORY"] = target.Repository;
        start.Environment["RESTIC_PASSWORD"] = target.Password;
        if (target.AccessKeyId is not null) start.Environment["AWS_ACCESS_KEY_ID"] = target.AccessKeyId;
        if (target.SecretAccessKey is not null) start.Environment["AWS_SECRET_ACCESS_KEY"] = target.SecretAccessKey;

        start.ArgumentList.Add("--cache-dir");
        start.ArgumentList.Add(cacheDirectory);
        if (target.IsS3)
        {
            // Path-style addressing works with every S3-compatible store; virtual hosts don't.
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("s3.bucket-lookup=path");
            if (!string.IsNullOrWhiteSpace(target.Region))
            {
                start.ArgumentList.Add("-o");
                start.ArgumentList.Add($"s3.region={target.Region}");
            }
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        var output = new List<string>();
        var error = new StringBuilder();
        // Both pipes are drained as restic writes, or a chatty restic fills one and stops.
        process.OutputDataReceived += (_, line) =>
        {
            // --json reports progress several times a second through a long backup; none of it is kept.
            if (line.Data is { Length: > 0 } text && !text.StartsWith("{\"message_type\":\"status\"", StringComparison.Ordinal))
                lock (output) output.Add(text);
        };
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is null) return;
            lock (error)
            {
                error.AppendLine(line.Data);
                // Only the end is ever read; a restic stuck retrying mustn't grow this without bound.
                if (error.Length > 64 * 1024) error.Remove(0, error.Length - 32 * 1024);
            }
        };

        var clock = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        logger.LogDebug("restic {Command}", arguments.FirstOrDefault(argument => !argument.StartsWith('-')));

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(limit);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timer.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { ProcessTree.Kill(process); }
            catch (Exception stopping) when (stopping is InvalidOperationException or SystemException)
            {
                logger.LogWarning(stopping, "restic didn't stop when told to");
            }
            await process.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) throw;
        }
        // Waits for the last of both pipes, so nothing restic said is lost.
        process.WaitForExit();

        string said;
        lock (error) said = error.ToString();
        List<string> lines;
        lock (output) lines = [.. output];
        return new ResticResult(timedOut ? -1 : process.ExitCode, lines, said, timedOut, clock.Elapsed);
    }
}
