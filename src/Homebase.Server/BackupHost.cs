using Homebase.Core;
using Homebase.Core.Backup;

namespace Homebase.Server;

/// <summary>
/// Gets a host that has joined Uncloud Backup its household's storage, asking as soon as it joins
/// and as often as the service says until it's there. Then backs the host up every night, and soon
/// after it starts if it missed a night.
/// </summary>
public sealed class BackupHost(BackupService backups, BackupAccount account, IConfiguration configuration, ILogger<BackupHost> logger)
    : BackgroundService
{
    /// <summary>How long a host with nowhere to back up waits before looking again, unless it joins first.</summary>
    private static readonly TimeSpan Unconfigured = TimeSpan.FromMinutes(1);
    /// <summary>How long after the service couldn't be reached it's asked again.</summary>
    private static readonly TimeSpan Unreachable = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Turning nightly backups off leaves backing up by hand, so a host that has joined still
        // gets its storage: only the schedule is off.
        var nightly = configuration.GetValue("Homebase:Backup:Nightly", true);
        // Two in the morning unless somebody chose otherwise: the house is asleep and the line is free.
        var hour = Math.Clamp(configuration.GetValue("Homebase:Backup:Hour", 2), 0, 23);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!backups.IsConfigured)
                {
                    await account.NapAsync(account.Joined ? await AskForStorageAsync(stoppingToken) : Unconfigured, stoppingToken);
                    continue;
                }
                if (!nightly) return;
                // A host that opened a lost one's backup adds nothing to it until it's resumed.
                if (account.Paused)
                {
                    await account.NapAsync(Unconfigured, stoppingToken);
                    continue;
                }
                var status = backups.Status;
                var now = DateTimeOffset.Now;
                var next = BackupSchedule.Next(now, TimeZoneInfo.Local, hour, status.LastSucceeded, status.LastStarted);
                logger.LogInformation("The next backup is at {Next}", next);
                await Task.Delay(next - now, stoppingToken);
                // One already running, started from Settings, is the one for tonight.
                try { await backups.RunAsync(stoppingToken); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogError(error, "The nightly backup stopped with something unexpected");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Asks once, and says how long to wait before looking again.</summary>
    private async Task<TimeSpan> AskForStorageAsync(CancellationToken stoppingToken)
    {
        try
        {
            var answer = await account.RefreshStorageAsync(stoppingToken);
            if (answer.Storage is not null)
            {
                logger.LogInformation("Uncloud Backup has set up this household's storage");
                return TimeSpan.Zero;
            }
            return answer.RetryAfter < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : answer.RetryAfter;
        }
        catch (LibraryException error)
        {
            logger.LogWarning("Couldn't get this host's backup storage: {Message}", error.Message);
            return Unreachable;
        }
    }
}
