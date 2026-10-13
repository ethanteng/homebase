using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Homebase.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Homebase.Server;

/// <summary>
/// Stops Uncloud when it is still running but has stopped answering, so that whatever runs it —
/// launchd on a Mac host, the Uncloud app, systemd — starts it again. Those only notice a process
/// that has gone. One that is stuck looks fine to them, and the household is left without its
/// files until somebody restarts the computer.
///
/// It asks the way a browser would, over the socket Uncloud listens on, from a thread of its own.
/// Not the thread pool, and nothing that waits on a timer: a pool with no thread free is the usual
/// way a .NET server gets stuck, and the watch must not get stuck along with it.
/// </summary>
public sealed class Watchdog
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);
    /// <summary>How many checks in a row go unanswered before Uncloud is stopped.</summary>
    public const int Misses = 3;

    private readonly Func<bool> _answers;
    private readonly Action<string> _giveUp;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;
    private readonly CancellationToken _stopping;

    internal Watchdog(Func<bool> answers, Action<string> giveUp, ILogger logger, TimeSpan interval, CancellationToken stopping)
    {
        _answers = answers;
        _giveUp = giveUp;
        _logger = logger;
        _interval = interval;
        _stopping = stopping;
    }

    /// <summary>
    /// Starts watching once Uncloud is listening, unless Homebase:Watchdog:Enabled says not to.
    /// Homebase:Watchdog:Interval is how often it asks.
    /// </summary>
    public static void Start(WebApplication app)
    {
        var interval = app.Configuration.GetValue("Homebase:Watchdog:Interval", DefaultInterval);
        if (!app.Configuration.GetValue("Homebase:Watchdog:Enabled", true) || interval <= TimeSpan.Zero) return;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Homebase.Watchdog");
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            // Where Kestrel actually listens. A test host has no socket at all, and nothing to
            // watch: asking the usual port there would find somebody's real Uncloud, or nobody.
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            if (Target(addresses) is not { } target) return;
            var patience = interval / 3;
            new Watchdog(() => Answers(target, patience), reason => GiveUp(reason, logger), logger, interval,
                app.Lifetime.ApplicationStopping).Run();
            logger.LogInformation("Watching that Uncloud keeps answering at {Target}", target);
        });
    }

    /// <summary>
    /// Where to ask: the first address Kestrel listens on, with an address that means "all of
    /// them" read as this computer's own. Null when it isn't listening on a socket.
    /// </summary>
    internal static Uri? Target(IEnumerable<string>? addresses)
    {
        if (addresses?.FirstOrDefault() is not { } first || !Uri.TryCreate(first, UriKind.Absolute, out var listening))
            return null;
        var host = IPAddress.TryParse(listening.DnsSafeHost, out var address)
            ? address.Equals(IPAddress.Any) ? IPAddress.Loopback
            : address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback
            : address
            : IPAddress.Loopback;
        return new UriBuilder(listening.Scheme, host.ToString(), listening.Port).Uri;
    }

    /// <summary>Watches on a thread of its own until Uncloud stops, or stops answering.</summary>
    internal Thread Run()
    {
        var thread = new Thread(Watch) { IsBackground = true, Name = "Uncloud watchdog" };
        thread.Start();
        return thread;
    }

    private void Watch()
    {
        var missed = 0;
        var answered = Stopwatch.GetTimestamp();
        // A wait on the token's handle is the operating system's, so it ends on time however busy
        // the rest of Uncloud is.
        while (!_stopping.WaitHandle.WaitOne(_interval))
        {
            if (_answers())
            {
                _logger.LogDebug("Uncloud answered when it checked on itself");
                if (missed > 0) _logger.LogInformation("Uncloud is answering again");
                missed = 0;
                answered = Stopwatch.GetTimestamp();
                continue;
            }
            // Not answering while it stops is how stopping goes.
            if (_stopping.IsCancellationRequested) return;
            missed++;
            if (missed < Misses)
            {
                _logger.LogWarning("Uncloud didn’t answer when it checked on itself ({Missed} of {Misses})", missed, Misses);
                continue;
            }
            // Somebody stepping through it in a debugger isn't a reason to take it away from them.
            if (Debugger.IsAttached)
            {
                _logger.LogWarning("Uncloud isn’t answering, but a debugger is attached, so it carries on");
                missed = 0;
                continue;
            }
            _giveUp($"Uncloud hadn’t answered for {Stopwatch.GetElapsedTime(answered).TotalSeconds:0} seconds, so it was stopped to start again.");
            return;
        }
    }

    /// <summary>
    /// Whether anything answers at <paramref name="target"/> as a web server would. Any reply at
    /// all is an answer: this is about being stuck, and a server that says something is not. One
    /// that answers with an error would answer the same way after starting again.
    /// </summary>
    internal static bool Answers(Uri target, TimeSpan patience)
    {
        var milliseconds = (int)Math.Max(1, patience.TotalMilliseconds);
        try
        {
            var address = IPAddress.Parse(target.DnsSafeHost);
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
                SendTimeout = milliseconds,
                ReceiveTimeout = milliseconds
            };
            // Connecting is the one step a socket has no timeout of its own for, and one that isn't
            // accepting its connections can keep a caller waiting for over a minute.
            if (!socket.ConnectAsync(address, target.Port).Wait(milliseconds)) return false;
            using var network = new NetworkStream(socket, ownsSocket: false);
            using var tls = target.Scheme == Uri.UriSchemeHttps
                ? new SslStream(network, leaveInnerStreamOpen: true)
                : null;
            Stream stream = network;
            if (tls is not null)
            {
                tls.AuthenticateAsClient(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    // Its own certificate, issued for the names people use rather than for this
                    // computer's. Nothing is sent that it would matter to send anybody else, and
                    // all that is read back is whether something answered.
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                });
                stream = tls;
            }
            // localhost is a name every Uncloud answers to, whatever else it was told to.
            stream.Write("GET /api/health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"u8);
            stream.Flush();
            var reply = new byte[16];
            int read = 0, count;
            while (read < reply.Length && (count = stream.Read(reply, read, reply.Length - read)) > 0)
                read += count;
            return Encoding.ASCII.GetString(reply, 0, read).StartsWith("HTTP/", StringComparison.Ordinal);
        }
        catch (Exception failure) when (failure is SocketException or IOException or AuthenticationException
            or AggregateException or FormatException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Says why, stops Syncthing and the tunnel, and stops Uncloud there and then. Nothing is asked
    /// to stop politely, because whatever is stuck would be the thing asked; and not by exiting,
    /// which waits on everything shutting down. Its children are stopped first, since without it
    /// they would hold their ports against the Uncloud that comes next.
    /// </summary>
    private static void GiveUp(string reason, ILogger logger)
    {
        // Writing the log, or finding the children, could be stuck behind the same thing. It goes
        // regardless, a little later.
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(10));
            Environment.FailFast(reason);
        }) { IsBackground = true, Name = "Uncloud watchdog deadline" }.Start();
        logger.LogCritical("{Reason}", reason);
        ProcessTree.KillDescendants(Environment.ProcessId);
        Environment.FailFast(reason);
    }
}
