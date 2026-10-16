using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Homebase.Core;
using Homebase.Core.Sync;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task One_that_wont_start_again_is_tried_until_it_does()
    {
        if (OperatingSystem.IsWindows()) return;
        // Goes the first time, and can't be run again until it's put right: a moment when there's
        // nothing to start is no reason never to try again.
        var syncthing = FakeSyncthing("""
            if [ ! -f "$started.first" ]; then touch "$started.first"; chmod -x "$0"; exit 1; fi
            exec sleep 60
            """);
        using var process = new SyncthingProcess(Home(), syncthing, FreePort(), NullLogger.Instance, new HttpClient())
        {
            RestartAfter = [TimeSpan.FromMilliseconds(50)]
        };

        await process.StartAsync(CancellationToken.None);
        await Until(() => process.Unavailable?.Contains("couldn’t start Syncthing again") == true);
        Assert.Equal(1, Starts);

        File.SetUnixFileMode(syncthing, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Until(() => Starts == 2);
        await process.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Only_this_syncthing_on_this_home_is_taken_for_one_left_running()
    {
        const string binary = "/Applications/Uncloud.app/Contents/MacOS/server/syncthing";
        const string home = "/Users/someone/Library/Application Support/Uncloud/syncthing";
        SyncthingProcess.RunningProcess[] running =
        [
            // Left by an earlier copy of the app: Syncthing's monitor, and Syncthing proper under it.
            new(100, 1, $"{binary} serve --home {home} --no-browser"),
            new(101, 100, $"{binary} serve --home {home} --no-browser"),
            // The host's own, from the same program on a home of its own.
            new(200, 1, $"{binary} serve --home /Users/someone/Library/Application Support/Uncloud/server/syncthing --no-browser"),
            // One somebody runs themselves.
            new(300, 1, "/opt/homebrew/Cellar/syncthing/2.0.12/bin/syncthing serve --home /Users/someone/.config/syncthing"),
            // Another Syncthing on this home is not this one, and a home that begins like it is another home.
            new(400, 1, $"/opt/homebrew/bin/syncthing serve --home {home}"),
            new(500, 1, $"{binary} serve --home {home}-old --no-browser"),
            new(600, 1, $"vim {home}/config.xml")
        ];

        Assert.Equal([100, 101], SyncthingProcess.Leftovers(running, binary, home).Select(process => process.Id));
    }

    [Fact]
    public async Task A_syncthing_left_running_on_this_home_is_stopped_before_another_starts()
    {
        if (OperatingSystem.IsWindows()) return;
        // Not exec'd, so the script stays what is running, as Syncthing does.
        var syncthing = FakeSyncthing("while :; do sleep 0.1; done");
        var home = Home();
        using var leftover = Serving(syncthing, home);
        using var other = Serving(syncthing, home + "-other");
        try
        {
            await Until(() => Starts == 2);
            using var process = new SyncthingProcess(home, syncthing, FreePort(), NullLogger.Instance, new HttpClient());

            await process.StartAsync(CancellationToken.None);

            Assert.True(leftover.WaitForExit(5000), "The one left running is still running.");
            await Until(() => Starts == 3);
            Assert.False(other.HasExited);
            await process.StopAsync(CancellationToken.None);
        }
        finally
        {
            ProcessTree.Kill(leftover);
            ProcessTree.Kill(other);
        }
    }

    [Fact]
    public async Task A_syncthing_that_was_already_answering_is_not_taken_for_the_one_started()
    {
        if (OperatingSystem.IsWindows()) return;
        // Answered with this home's key by one that started an hour ago, the way one nothing could
        // stop does, until the one started answers.
        var port = FreePort();
        var status = Status(DateTimeOffset.Now.AddHours(-1));
        using var api = Answer(port, () => Volatile.Read(ref status));
        var syncthing = FakeSyncthing("exec sleep 60");
        using var process = new SyncthingProcess(Home(), syncthing, port, NullLogger.Instance, new HttpClient());

        await process.StartAsync(CancellationToken.None);
        await Task.Delay(2500);
        Assert.False(process.IsReady);

        Volatile.Write(ref status, Status(DateTimeOffset.Now));
        await process.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.IsReady);
        await process.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_last_a_syncthing_said_is_logged_when_it_stops_by_itself()
    {
        if (OperatingSystem.IsWindows()) return;
        var syncthing = FakeSyncthing("""
            for line in $(seq -w 1 30); do echo "said $line"; done
            echo "ERR Failed to acquire lock: is another Syncthing instance already running?"
            exit 1
            """);
        var log = new Warnings();
        using var process = new SyncthingProcess(Home(), syncthing, FreePort(), log, new HttpClient())
        {
            RestartAfter = [TimeSpan.FromMinutes(10)]
        };

        await process.StartAsync(CancellationToken.None);
        await Until(() => log.Said.Any(warning => warning.Contains("stopped by itself")));

        var warning = Assert.Single(log.Said, warning => warning.Contains("stopped by itself"));
        Assert.Contains("Failed to acquire lock", warning);
        Assert.Contains("said 30", warning);
        Assert.Contains($"said {31 - SyncthingProcess.SaidKept + 1:00}", warning);
        Assert.DoesNotContain($"said {31 - SyncthingProcess.SaidKept:00}", warning);
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

    private static HttpListener Answer(int port, Func<string>? body = null)
    {
        var listener = new HttpListener { Prefixes = { $"http://127.0.0.1:{port}/" } };
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    context.Response.StatusCode = 200;
                    if (body is not null) await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(body()));
                    context.Response.Close();
                }
                catch (Exception) { return; }
            }
        });
        return listener;
    }

    /// <summary>What Syncthing says of itself, as far as which one it is goes.</summary>
    private static string Status(DateTimeOffset started) => $$"""{"startTime":"{{started:yyyy-MM-ddTHH:mm:sszzz}}"}""";

    /// <summary><paramref name="syncthing"/> serving <paramref name="home"/>, started by something other than Uncloud.</summary>
    private static Process Serving(string syncthing, string home) =>
        Process.Start(new ProcessStartInfo(syncthing)
        {
            ArgumentList = { "serve", "--home", home, "--no-browser" },
            UseShellExecute = false
        })!;

    /// <summary>What was logged as a warning.</summary>
    private sealed class Warnings : ILogger
    {
        private readonly ConcurrentQueue<string> _warnings = new();
        public IReadOnlyList<string> Said => [.. _warnings];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) _warnings.Enqueue(formatter(state, exception));
        }
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
