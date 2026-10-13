using System.Net;
using Homebase.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homebase.Tests;

/// <summary>
/// Which Syncthing Uncloud runs — the one it ships, unless somebody said otherwise — and that it
/// keeps running.
/// </summary>
public sealed class SyncthingProcessTests : IDisposable
{
    private readonly string _application = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"))).FullName;

    private string Bundled => Path.Combine(_application, OperatingSystem.IsWindows() ? "syncthing.exe" : "syncthing");

    [Fact]
    public void The_copy_beside_the_application_is_used_ahead_of_the_path()
    {
        File.WriteAllText(Bundled, "");

        Assert.Equal(Bundled, SyncthingProcess.Binary(null, _application));
        Assert.Equal(Bundled, SyncthingProcess.Binary(" ", _application));
    }

    [Fact]
    public void A_configured_path_wins_even_over_the_bundled_copy()
    {
        File.WriteAllText(Bundled, "");

        Assert.Equal("/opt/syncthing/bin/syncthing", SyncthingProcess.Binary("/opt/syncthing/bin/syncthing", _application));
    }

    [Fact]
    public void Without_a_bundled_copy_the_path_is_asked()
    {
        Assert.Equal("syncthing", SyncthingProcess.Binary(null, _application));
    }

    [Fact]
    public async Task A_syncthing_that_stops_by_itself_is_started_again()
    {
        if (OperatingSystem.IsWindows()) return;
        // Goes the first time, the way a crash does, and stays the second.
        var syncthing = FakeSyncthing("""
            if [ ! -f "$started.first" ]; then touch "$started.first"; exit 1; fi
            exec sleep 60
            """);
        using var process = new SyncthingProcess(Home(), syncthing, FreePort(), NullLogger.Instance, new HttpClient())
        {
            RestartAfter = [TimeSpan.FromMilliseconds(50)]
        };

        await process.StartAsync(CancellationToken.None);
        await Until(() => Starts == 2);

        Assert.False(process.IsReady);
        await process.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_syncthing_that_stops_is_not_taken_for_running_until_it_answers_again()
    {
        if (OperatingSystem.IsWindows()) return;
        // Syncthing's API, answering whichever Syncthing is running at the moment.
        var port = FreePort();
        using var api = Answer(port);
        // The first goes when told to; the one started after it stays.
        var syncthing = FakeSyncthing("""
            if [ ! -f "$started.first" ]; then
              touch "$started.first"
              while [ ! -f "$started.go" ]; do sleep 0.05; done
              exit 1
            fi
            exec sleep 60
            """);
        using var process = new SyncthingProcess(Home(), syncthing, port, NullLogger.Instance, new HttpClient())
        {
            RestartAfter = [TimeSpan.FromSeconds(3)]
        };

        await process.StartAsync(CancellationToken.None);
        await process.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.IsReady);

        File.WriteAllText(Started + ".go", "");
        await Until(() => !process.IsReady);
        Assert.Equal("Syncthing stopped, and Uncloud is starting it again.", process.Unavailable);

        await Until(() => Starts == 2 && process.IsReady);
        Assert.Null(process.Unavailable);
        await process.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_syncthing_stopped_on_purpose_stays_stopped()
    {
        if (OperatingSystem.IsWindows()) return;
        var syncthing = FakeSyncthing("exec sleep 60");
        using var process = new SyncthingProcess(Home(), syncthing, FreePort(), NullLogger.Instance, new HttpClient())
        {
            RestartAfter = [TimeSpan.FromMilliseconds(50)]
        };

        await process.StartAsync(CancellationToken.None);
        await Until(() => Starts == 1);
        await process.StopAsync(CancellationToken.None);
        await Task.Delay(500);

        Assert.Equal(1, Starts);
    }

    [Fact]
    public async Task One_that_keeps_stopping_is_tried_less_and_less_often()
    {
        if (OperatingSystem.IsWindows()) return;
        var syncthing = FakeSyncthing("exit 1");
        using var process = new SyncthingProcess(Home(), syncthing, FreePort(), NullLogger.Instance, new HttpClient())
        {
            RestartAfter = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(10)]
        };

        await process.StartAsync(CancellationToken.None);
        await Until(() => Starts == 3);
        await Until(() => process.Unavailable?.Contains("keeps stopping") == true);
        await Task.Delay(500);

        // The third wait is ten minutes, so there's no fourth start yet.
        Assert.Equal(3, Starts);
        await process.StopAsync(CancellationToken.None);
    }

    private string Started => Path.Combine(_application, "started");
    private int Starts => File.Exists(Started) ? File.ReadAllLines(Started).Length : 0;

    /// <summary>A home Syncthing has already been set up in, so only serving is left to do.</summary>
    private string Home()
    {
        var home = Directory.CreateDirectory(Path.Combine(_application, "home")).FullName;
        File.WriteAllText(Path.Combine(home, "config.xml"),
            "<configuration><gui><address>127.0.0.1:1</address><apikey>key</apikey></gui><options></options></configuration>");
        return home;
    }

    /// <summary>A Syncthing that notes each time it is started to serve, then does <paramref name="serving"/>.</summary>
    private string FakeSyncthing(string serving)
    {
        var path = Path.Combine(_application, "fake-syncthing");
        File.WriteAllText(path, $"""
            #!/bin/sh
            started='{Started}'
            echo serve >> "$started"
            {serving.Replace("\r\n", "\n")}

            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "It never happened.");
            await Task.Delay(50);
        }
    }

    private static HttpListener Answer(int port)
    {
        var listener = new HttpListener { Prefixes = { $"http://127.0.0.1:{port}/" } };
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try { var context = await listener.GetContextAsync(); context.Response.StatusCode = 200; context.Response.Close(); }
                catch (Exception) { return; }
            }
        });
        return listener;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public void Dispose()
    {
        try { Directory.Delete(_application, true); } catch (IOException) { }
    }
}
