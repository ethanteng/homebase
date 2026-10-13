using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Homebase.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homebase.Tests;

/// <summary>
/// Uncloud stopping itself when it is still running but has stopped answering, so that whatever
/// runs it starts it again — and never when it is answering, which would take everybody's files
/// away from them every minute and a half.
/// </summary>
public sealed class WatchdogTests : IDisposable
{
    private static readonly TimeSpan Quickly = TimeSpan.FromMilliseconds(10);

    private readonly string _temporary = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Three_unanswered_checks_in_a_row_stop_it()
    {
        var asked = 0;
        string? why = null;
        using var stopping = new CancellationTokenSource();
        var thread = new Watchdog(() => { Interlocked.Increment(ref asked); return false; },
            reason => why = reason, NullLogger.Instance, Quickly, stopping.Token).Run();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(Watchdog.Misses, asked);
        Assert.Contains("hadn’t answered for", why);
    }

    [Fact]
    public void An_answer_in_between_starts_the_count_again()
    {
        // Two misses, an answer, two misses, an answer: never three in a row, so never stopped.
        var asked = 0;
        var gaveUp = false;
        using var stopping = new CancellationTokenSource();
        var thread = new Watchdog(() =>
        {
            if (++asked == 15) stopping.Cancel();
            return asked % 3 == 0;
        }, _ => gaveUp = true, NullLogger.Instance, Quickly, stopping.Token).Run();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.False(gaveUp);
        Assert.Equal(15, asked);
    }

    [Fact]
    public void Not_answering_while_it_stops_is_not_held_against_it()
    {
        // Uncloud stopping on purpose stops answering on its way out, and stops the watch with it.
        var gaveUp = false;
        using var stopping = new CancellationTokenSource();
        var thread = new Watchdog(() => { stopping.Cancel(); return false; },
            _ => gaveUp = true, NullLogger.Instance, Quickly, stopping.Token).Run();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.False(gaveUp);
    }

    [Fact]
    public void Nothing_listening_is_not_an_answer()
    {
        var port = FreePort();

        Assert.False(Watchdog.Answers(new Uri($"http://127.0.0.1:{port}"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_server_that_takes_the_connection_and_says_nothing_is_not_an_answer()
    {
        // What a stuck Uncloud looks like from outside: the system accepts the connection on its
        // behalf, and then nothing comes back.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var clock = Stopwatch.StartNew();

            Assert.False(Watchdog.Answers(new Uri($"http://127.0.0.1:{Port(listener)}"), TimeSpan.FromMilliseconds(300)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"It waited {clock.Elapsed}.");
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Any_reply_at_all_is_an_answer()
    {
        // An error is still an answer: starting again wouldn't change what it says.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var request = Reply(listener, "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n");

            Assert.True(Watchdog.Answers(new Uri($"http://127.0.0.1:{Port(listener)}"), TimeSpan.FromSeconds(5)));
            var asked = await request;
            Assert.StartsWith("GET /api/health HTTP/1.1\r\n", asked);
            // A name every Uncloud answers to, whichever others it was given.
            Assert.Contains("Host: localhost\r\n", asked);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Something_that_isnt_a_web_server_is_not_an_answer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var request = Reply(listener, "SSH-2.0-OpenSSH_9.6\r\n");

            Assert.False(Watchdog.Answers(new Uri($"http://127.0.0.1:{Port(listener)}"), TimeSpan.FromSeconds(5)));
            await request;
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData("http://127.0.0.1:5210", "http://127.0.0.1:5210/")]
    [InlineData("http://0.0.0.0:5210", "http://127.0.0.1:5210/")]
    [InlineData("http://[::]:5210", "http://[::1]:5210/")]
    [InlineData("https://192.168.1.10:8443", "https://192.168.1.10:8443/")]
    [InlineData("http://localhost:5210", "http://127.0.0.1:5210/")]
    public void It_asks_where_Uncloud_listens(string listening, string asked)
    {
        Assert.Equal(asked, Watchdog.Target([listening])?.ToString());
    }

    [Fact]
    public void Nothing_is_watched_without_a_socket()
    {
        Assert.Null(Watchdog.Target(null));
        Assert.Null(Watchdog.Target([]));
    }

    [Fact]
    public void A_test_host_is_never_watched()
    {
        // It has no socket. Were it watched, the watch would ask the usual port, find nobody — or
        // somebody's real Uncloud — and stop the test run.
        using var host = new TestHost(Path.Combine(_temporary, "TestHost"));
        using var client = host.CreateClient();

        var server = host.Services.GetRequiredService<IServer>();
        Assert.Null(Watchdog.Target(server.Features.Get<IServerAddressesFeature>()?.Addresses));
    }

    [Fact]
    public async Task The_real_server_answers_its_own_checks()
    {
        // Were the check to go wrong against the real thing — a name it won't answer to, say — this
        // would stop Uncloud on every host every minute and a half. Here it asks every second, and
        // is answered.
        if (OperatingSystem.IsWindows()) return;
        await AssertKeepsAnsweringAsync([]);
    }

    [Fact]
    public async Task The_real_server_answers_its_own_checks_over_its_own_https()
    {
        if (OperatingSystem.IsWindows()) return;
        var certificate = Path.Combine(_temporary, "uncloud.pfx");
        await File.WriteAllBytesAsync(certificate, SelfSigned());

        await AssertKeepsAnsweringAsync([("Homebase__Certificate__Path", certificate)]);
    }

    private async Task AssertKeepsAnsweringAsync(IEnumerable<(string Name, string Value)> extra)
    {
        var said = new ConcurrentQueue<string>();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Homebase.Server"))
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["Homebase__Port"] = FreePort().ToString();
        start.Environment["Homebase__ConfigDirectory"] = Path.Combine(_temporary, "HostConfig");
        start.Environment["Homebase__Syncthing__Enabled"] = "false";
        start.Environment["Homebase__Announce"] = "false";
        start.Environment["Homebase__StopWhenInputCloses"] = "true";
        start.Environment["Homebase__Watchdog__Interval"] = "00:00:01";
        // Every check it answers, said.
        start.Environment["Logging__LogLevel__Homebase.Watchdog"] = "Debug";
        foreach (var (name, value) in extra) start.Environment[name] = value;
        using var server = Process.Start(start)!;
        server.OutputDataReceived += (_, line) => { if (line.Data is { } text) said.Enqueue(text); };
        server.ErrorDataReceived += (_, line) => { if (line.Data is { } text) said.Enqueue(text); };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        try
        {
            // Three misses in a row would stop it. The very first check may miss, while a server
            // nobody has asked anything yet gets ready to answer — which takes far less than the
            // ten seconds a real host waits, but can take more than the third of a second here.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (said.Count(line => line.Contains("Uncloud answered when it checked on itself")) < 3)
            {
                Assert.False(server.HasExited, "Uncloud stopped itself while answering:\n" + string.Join('\n', said));
                Assert.True(DateTimeOffset.UtcNow < deadline, "Uncloud didn’t answer its own checks:\n" + string.Join('\n', said));
                await Task.Delay(100);
            }
            Assert.Contains(said, line => line.Contains("Watching that Uncloud keeps answering"));
        }
        finally
        {
            if (!server.HasExited) server.Kill();
            server.WaitForExit(10_000);
        }
    }

    /// <summary>Takes one connection, reads the request, and answers it with <paramref name="reply"/>.</summary>
    private static Task<string> Reply(TcpListener listener, string reply) => Task.Run(async () =>
    {
        using var connection = await listener.AcceptTcpClientAsync();
        var stream = connection.GetStream();
        var request = new StringBuilder();
        var buffer = new byte[1024];
        while (!request.ToString().Contains("\r\n\r\n"))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
        return request.ToString();
    });

    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return Port(listener); }
        finally { listener.Stop(); }
    }

    private static byte[] SelfSigned()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=uncloud.local", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }
}
