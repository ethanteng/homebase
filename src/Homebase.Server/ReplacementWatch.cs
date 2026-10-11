namespace Homebase.Server;

/// <summary>
/// Stops this server once a newer copy of it has been put where it was started from. Run by launchd
/// in an account of its own, it isn't started by the app any more, so updating the app would
/// otherwise leave the old server running until the Mac restarts. launchd starts it again at once,
/// from the new copy.
/// </summary>
public static class ReplacementWatch
{
    /// <summary>
    /// Calls <paramref name="stop"/> once <paramref name="path"/> is a different file from the one
    /// it was when this began, and has stayed the same file for one look after that — an update
    /// copies over a while, and a server started from half of it wouldn't start at all.
    /// </summary>
    public static Task StopWhenReplaced(string path, Action stop, TimeSpan interval, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var started = Identity(path);
            string? changed = null;
            try
            {
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    var now = Identity(path);
                    if (now == started) continue;
                    // Gone, or still arriving: looked at again until it holds still.
                    if (now is not null && now == changed)
                    {
                        stop();
                        return;
                    }
                    changed = now;
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

    /// <summary>
    /// Which file this is, as far as anybody can tell without opening it: a copy put in its place has
    /// its own creation time, even when it carries the modification time of the build it came from.
    /// Null when there's nothing there.
    /// </summary>
    private static string? Identity(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists
                ? $"{file.CreationTimeUtc.Ticks}:{file.LastWriteTimeUtc.Ticks}:{file.Length}"
                : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
