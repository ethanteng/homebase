using Homebase.Core.Backup;

namespace Homebase.Server;

/// <summary>
/// Backs the host up every night, and soon after it starts if it missed a night. Nothing happens
/// unless backups are set up.
/// </summary>
public sealed class BackupHost(BackupService backups, IConfiguration configuration, ILogger<BackupHost> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!backups.IsConfigured || !configuration.GetValue("Homebase:Backup:Nightly", true)) return;
        // Two in the morning unless somebody chose otherwise: the house is asleep and the line is free.
        var hour = Math.Clamp(configuration.GetValue("Homebase:Backup:Hour", 2), 0, 23);
        while (!stoppingToken.IsCancellationRequested)
        {
            var status = backups.Status;
            var now = DateTimeOffset.Now;
            var next = BackupSchedule.Next(now, TimeZoneInfo.Local, hour, status.LastSucceeded, status.LastStarted);
            logger.LogInformation("The next backup is at {Next}", next);
            try { await Task.Delay(next - now, stoppingToken); }
            catch (OperationCanceledException) { return; }
            // One already running, started from Settings, is the one for tonight.
            try { await backups.RunAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                logger.LogError(error, "The nightly backup stopped with something unexpected");
            }
        }
    }
}
