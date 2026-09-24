namespace Homebase.Server;

/// <summary>
/// Ties this server's life to whoever started it, without asking them to say goodbye. A process
/// that goes away closes everything it held, including its end of a pipe, and that is the one
/// signal that arrives however it went.
/// </summary>
public static class ParentWatch
{
    /// <summary>Calls <paramref name="stop"/> once <paramref name="input"/> reaches its end.</summary>
    public static Task StopWhenClosed(TextReader input, Action stop) => Task.Run(async () =>
    {
        try
        {
            // Nothing is ever sent. Anything that is, is read and ignored: only the end means go.
            while (await input.ReadLineAsync() is not null) { }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // A pipe that broke rather than closed has lost its other end all the same.
        }
        stop();
    });
}
