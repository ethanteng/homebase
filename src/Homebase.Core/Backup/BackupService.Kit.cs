using Microsoft.Extensions.Logging;

namespace Homebase.Core.Backup;

public sealed partial class BackupService
{
    /// <summary>
    /// Whether the password this host has opens its backup: true, false for one the backup doesn't
    /// take, or null where nothing has been backed up there yet. A host that took a lost one's place
    /// with its recovery kit asks this before it counts itself joined.
    /// </summary>
    public async Task<bool?> OpensAsync(CancellationToken cancellationToken)
    {
        var opened = await Open().RunAsync(["--no-lock", "cat", "config"], _policy.OpenLimit, cancellationToken);
        if (opened.Succeeded) return true;
        if (opened.NoRepository) return null;
        // restic's own code for a password no key in the repository opens.
        if (!opened.TimedOut && opened.ExitCode == 12) return false;
        _logger.LogWarning("restic couldn't open the backup (exit {Code}{TimedOut}): {Said}",
            opened.ExitCode, opened.TimedOut ? ", timed out" : "", opened.Said(5));
        throw new LibraryException(
            opened.TimedOut || opened.Refused ? Explain(opened, "open the backup", _policy.OpenLimit) : "Uncloud couldn’t open the backup. Try again in a minute.",
            "unavailable");
    }
}
