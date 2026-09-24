using System.Diagnostics;
using System.Text.RegularExpressions;
using Homebase.Core;

namespace Homebase.Server;

/// <summary>Which service carries traffic from the public internet to this host.</summary>
public enum RemoteAccessProvider { None, Builtin, Tailscale, Cloudflare }

/// <summary>
/// How this host is reached from outside the house. A tunnel is an outbound connection to a
/// service that already owns a name and a certificate, so nothing is forwarded at the router,
/// no port is opened to the internet, and no certificate has to be obtained here or renewed.
/// The tunnel is also where TLS ends, which is why turning this on trusts loopback as a proxy:
/// the tunnel client runs on this machine and connects to Uncloud over it.
/// </summary>
public sealed record RemoteAccessOptions(
    RemoteAccessProvider Provider,
    string? Hostname,
    string? Tunnel,
    string? Command,
    string? Arguments,
    string StateDirectory,
    TimeSpan Timeout)
{
    public bool IsEnabled => Provider is not RemoteAccessProvider.None;

    /// <summary>
    /// Whether this tunnel will say nothing about where it ended up. Only a Cloudflare tunnel
    /// named beforehand: its address lives in Cloudflare's configuration rather than in anything
    /// it prints.
    /// </summary>
    public bool AnnouncesNothing =>
        Provider is RemoteAccessProvider.Cloudflare && Tunnel is { Length: > 0 };

    /// <summary>The address the tunnel is expected to announce, so another URL in the same
    /// output — a documentation link in a banner — is never mistaken for this host's.</summary>
    public Regex Announcement => Provider switch
    {
        RemoteAccessProvider.Builtin => BuiltinAddress,
        RemoteAccessProvider.Tailscale => TailscaleAddress,
        RemoteAccessProvider.Cloudflare => CloudflareAddress,
        _ => throw new InvalidOperationException("Remote access is off.")
    };

    /// <summary>
    /// A link somebody has to follow before this tunnel can carry anything, when the program
    /// says so. Only Uncloud's own tunnel does: the others are signed in before Uncloud ever
    /// runs them, and have nobody to ask.
    /// </summary>
    public Regex? SignInPrompt => Provider is RemoteAccessProvider.Builtin ? BuiltinSignIn : null;

    // Uncloud's own tunnel says what it means rather than being read between the lines.
    private static readonly Regex BuiltinAddress =
        new(@"^uncloud-tunnel: url=https://([^\s/]+)/?$", RegexOptions.Multiline);
    private static readonly Regex BuiltinSignIn =
        new(@"^uncloud-tunnel: signin=(\S+)$", RegexOptions.Multiline);
    private static readonly Regex TailscaleAddress =
        new(@"https://([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.ts\.net)", RegexOptions.IgnoreCase);
    private static readonly Regex CloudflareAddress =
        new(@"https://([A-Za-z0-9-]+\.trycloudflare\.com)", RegexOptions.IgnoreCase);

    public string Executable => Command is { Length: > 0 } ? Command : Provider switch
    {
        // Beside the application, because Uncloud ships it: there is nothing for anybody to
        // install and nothing to find on the PATH.
        RemoteAccessProvider.Builtin => Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "uncloud-tunnel.exe" : "uncloud-tunnel"),
        RemoteAccessProvider.Tailscale => "tailscale",
        RemoteAccessProvider.Cloudflare => "cloudflared",
        _ => throw new InvalidOperationException("Remote access is off.")
    };

    /// <summary>
    /// What to run. Overridable in full, because these are other people's command lines and a
    /// version that wants different flags must not mean waiting for a new Uncloud.
    /// </summary>
    public string ArgumentsFor(int port)
    {
        if (Arguments is { Length: > 0 } custom) return custom.Replace("{port}", port.ToString());
        return Provider switch
        {
            RemoteAccessProvider.Builtin =>
                $"-target http://127.0.0.1:{port} -state \"{StateDirectory}\" -hostname {Hostname ?? "uncloud"}",
            RemoteAccessProvider.Tailscale => $"funnel {port}",
            RemoteAccessProvider.Cloudflare when Tunnel is { Length: > 0 } tunnel =>
                $"tunnel --url http://127.0.0.1:{port} run {tunnel}",
            RemoteAccessProvider.Cloudflare => $"tunnel --url http://127.0.0.1:{port}",
            _ => throw new InvalidOperationException("Remote access is off.")
        };
    }

    public static RemoteAccessOptions From(IConfiguration configuration, string stateDirectory)
    {
        var name = (configuration["Homebase:RemoteAccess:Provider"] ?? "none").Trim();
        if (name.Length == 0) name = "none";
        if (!Enum.TryParse<RemoteAccessProvider>(name, ignoreCase: true, out var provider))
            throw new LibraryException(
                "Homebase__RemoteAccess__Provider must be none, builtin, tailscale or cloudflare, "
                + $"not “{name}”.");

        // People paste the whole address; only the name in it is a host header.
        var hostname = configuration["Homebase:RemoteAccess:Hostname"]?.Trim().TrimEnd('/');
        if (hostname is { Length: > 0 } && hostname.Contains("//", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(hostname, UriKind.Absolute, out var url))
                throw new LibraryException(
                    $"Homebase__RemoteAccess__Hostname isn’t an address Uncloud can read: “{hostname}”.");
            hostname = url.Host;
        }
        if (hostname is { Length: 0 }) hostname = null;

        var tunnel = configuration["Homebase:RemoteAccess:Tunnel"]?.Trim();
        if (tunnel is { Length: 0 }) tunnel = null;
        // A named Cloudflare tunnel carries its hostname in Cloudflare's own configuration, and
        // announces nothing on startup. Uncloud has to be told, or it would refuse every request
        // arriving under a name it doesn't answer to.
        if (provider is RemoteAccessProvider.Cloudflare && tunnel is not null && hostname is null)
            throw new LibraryException(
                "A named Cloudflare tunnel already has a hostname of its own, and Uncloud has to "
                + "know it to answer to it. Set Homebase__RemoteAccess__Hostname to the address "
                + "people will open, such as Homebase__RemoteAccess__Hostname=files.example.com.",
                "not_configured");

        var seconds = configuration.GetValue("Homebase:RemoteAccess:TimeoutSeconds", 60);
        if (seconds < 1)
            throw new LibraryException("Homebase__RemoteAccess__TimeoutSeconds must be at least 1.");

        return new RemoteAccessOptions(provider, hostname, tunnel,
            configuration["Homebase:RemoteAccess:Command"]?.Trim(),
            configuration["Homebase:RemoteAccess:Arguments"],
            // Beside the accounts and the host key, because it is the same kind of thing: what
            // this installation is, rather than anything belonging to the files.
            Path.Combine(stateDirectory, "tunnel"),
            TimeSpan.FromSeconds(seconds));
    }
}

/// <summary>
/// One run of a tunnel. Opening answers with the public hostname it now carries; <see cref="Closed"/>
/// completes when it stops carrying anything, which is the signal to open another.
/// </summary>
public interface ITunnel : IAsyncDisposable
{
    Task<string> OpenAsync(int port, CancellationToken cancellationToken);
    Task Closed { get; }

    /// <summary>
    /// Completes with a link somebody has to follow before this tunnel can carry anything. A
    /// tunnel with nobody to ask leaves this alone, and it never completes.
    /// </summary>
    Task<string> SignInRequired => Tunnels.NobodyToAsk;
}

public static class Tunnels
{
    /// <summary>A sign-in that is never asked for, shared because it never happens.</summary>
    public static readonly Task<string> NobodyToAsk = new TaskCompletionSource<string>().Task;
}

/// <summary>What the administrator is shown, and what the host check consults.</summary>
public sealed record RemoteAccessState(
    string Provider, string? Hostname, string? Url, string Status, string? Detail, string? SignInUrl);

/// <summary>
/// Keeps a tunnel open for as long as Uncloud is running. The hostname is settled before the
/// first request is served, so the ordinary host allowlist, public URL and proxy trust are all
/// built from it; afterwards this is what lets a tunnel that reconnects under a different name
/// — an unnamed Cloudflare tunnel does — keep being answered.
/// </summary>
public sealed class RemoteAccess(
    RemoteAccessOptions options,
    ILogger logger,
    Func<RemoteAccessOptions, ITunnel>? tunnels = null) : IAsyncDisposable
{
    /// <summary>
    /// Replaced by the test suite, which must not run anybody's tunnel program. It replaces the
    /// whole of this rather than the configuration it is read from, because a test host's
    /// settings are added while the application is being built — after the address has had to be
    /// settled, and so too late to be read here.
    /// </summary>
    internal static Func<IConfiguration, RemoteAccess>? Override { get; set; }

    public static RemoteAccess From(IConfiguration configuration, string stateDirectory, ILogger logger) =>
        Override?.Invoke(configuration)
        ?? new RemoteAccess(RemoteAccessOptions.From(configuration, stateDirectory), logger);

    private readonly Func<RemoteAccessOptions, ITunnel> _tunnels = tunnels ?? (each => new ProcessTunnel(each, logger));
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private ITunnel? _tunnel;
    private string? _hostname;
    private string _status = "off";
    private string? _detail;
    private string? _signIn;
    // An opening that is waiting on a person rather than on a program. Startup does not hold
    // the host offline for it, so it is kept here for the watch to finish.
    private Task<string>? _pending;
    // Read on every request, written when a tunnel settles: a field rather than the lock below,
    // so the host check costs nothing on a host nobody is reaching from outside.
    private volatile string? _answering;

    public RemoteAccessOptions Options => options;
    public bool IsEnabled => options.IsEnabled;

    public RemoteAccessState State
    {
        get
        {
            lock (_gate)
                return new RemoteAccessState(
                    options.Provider.ToString().ToLowerInvariant(),
                    _hostname,
                    _hostname is null ? null : $"https://{_hostname}",
                    _status,
                    _detail,
                    _signIn);
        }
    }

    /// <summary>Whether a request arriving under this name is one the tunnel brought in.</summary>
    public bool Answers(string host) =>
        _answering is { } announced && announced.Equals(host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens the tunnel and waits for its address. Blocking, and deliberately so: everything
    /// downstream — which names this host answers to, where Dropbox returns the browser — is
    /// decided from the address, and deciding it after requests had started would mean serving
    /// some of them under rules that were about to change.
    ///
    /// Answers null when the tunnel is waiting to be allowed by a person instead. That is not a
    /// failure and must not hold the host offline: everyone on the network is still waiting for
    /// their files while somebody goes to find their phone. The address arrives later, through
    /// <see cref="Watch"/>, and is answered to from the moment it does.
    /// </summary>
    public string? Open(int port)
    {
        var tunnel = _tunnels(options);
        lock (_gate) { _tunnel = tunnel; _status = "opening"; }
        var opening = tunnel.OpenAsync(port, _stopping.Token);
        var signIn = tunnel.SignInRequired;
        try
        {
            // Startup is single-threaded and has no synchronisation context, so waiting here
            // cannot deadlock, and there is nothing else for this thread to be doing yet.
            var settled = Task.WhenAny(opening, signIn, Task.Delay(options.Timeout, _stopping.Token))
                .GetAwaiter().GetResult();

            if (ReferenceEquals(settled, opening))
            {
                var hostname = opening.GetAwaiter().GetResult();
                Settle(hostname, "on", null);
                logger.LogInformation("Uncloud is reachable from anywhere at https://{Hostname}", hostname);
                return hostname;
            }

            if (ReferenceEquals(settled, signIn))
            {
                var link = signIn.GetAwaiter().GetResult();
                lock (_gate)
                {
                    _pending = opening;
                    _signIn = link;
                    _status = "needs_sign_in";
                    _detail = "Uncloud is waiting to be allowed onto the internet.";
                }
                logger.LogInformation(
                    "Uncloud is waiting to be allowed onto the internet. Open {Link} to allow it, "
                    + "or find the same link under Settings.", link);
                return null;
            }

            throw new LibraryException(
                $"{options.Executable} didn’t report an address within {options.Timeout.TotalSeconds:0} "
                + "seconds, so Uncloud doesn’t know what name to answer to. Check that it is signed "
                + "in and can reach the internet, or set Homebase__RemoteAccess__Provider=none to "
                + "start without remote access.", "not_configured");
        }
        catch (Exception failure)
        {
            // Uncloud is about to stop, and nothing else will come back for this: a tunnel
            // program left running would outlive the host it was started for and keep a name
            // pointed at a port with nothing behind it.
            lock (_gate) { _tunnel = null; _pending = null; _signIn = null; _status = "off"; }
            tunnel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (failure is LibraryException) throw;
            if (failure is OperationCanceledException && !_stopping.IsCancellationRequested)
                throw new LibraryException(
                    $"{options.Executable} stopped before it opened a tunnel.", "unavailable");
            throw;
        }
    }

    /// <summary>
    /// Reopens the tunnel for as long as Uncloud runs. A tunnel that drops is an outage of
    /// reaching the host from outside, never of the host: everyone on the network keeps working
    /// while this retries.
    /// </summary>
    public void Watch(int port)
    {
        if (!IsEnabled) return;
        _ = Task.Run(async () =>
        {
            var backoff = TimeSpan.FromSeconds(2);
            while (!_stopping.IsCancellationRequested)
            {
                ITunnel? current;
                Task<string>? pending;
                lock (_gate) { current = _tunnel; pending = _pending; }

                if (pending is not null)
                {
                    // No deadline on a person. A startup timeout is for a program that has gone
                    // wrong; somebody walking off to find their phone has not gone wrong.
                    try
                    {
                        var allowed = await pending.WaitAsync(_stopping.Token);
                        lock (_gate) { _pending = null; _signIn = null; }
                        Settle(allowed, "on", null);
                        backoff = TimeSpan.FromSeconds(2);
                        logger.LogInformation(
                            "Uncloud was allowed onto the internet and is reachable at https://{Hostname}", allowed);
                        continue;
                    }
                    catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
                    catch (Exception failure)
                    {
                        logger.LogWarning(failure, "The tunnel Uncloud was waiting to be allowed never opened");
                        lock (_gate) { _pending = null; _signIn = null; }
                    }
                }
                else if (current is not null)
                {
                    try { await current.Closed.WaitAsync(_stopping.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception failure) { logger.LogWarning(failure, "The tunnel to this Uncloud stopped"); }
                    if (_stopping.IsCancellationRequested) return;
                    Settle(null, "reconnecting", "The tunnel stopped and Uncloud is opening another.");
                }

                // Whatever it was, it is not the current tunnel any more. Waiting on a tunnel
                // that failed to open would be waiting on a program that timed out but is still
                // running, or one that never started, and neither will ever say so — which would
                // end the retries for good on the first failure.
                lock (_gate) { if (ReferenceEquals(_tunnel, current)) _tunnel = null; }
                if (current is not null) await Retire(current);

                try { await Task.Delay(backoff, _stopping.Token); }
                catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));

                ITunnel? next = null;
                try
                {
                    next = _tunnels(options);
                    var opening = next.OpenAsync(port, _stopping.Token);
                    var signIn = next.SignInRequired;
                    lock (_gate) _tunnel = next;

                    var settled = await Task.WhenAny(
                        opening, signIn, Task.Delay(options.Timeout, _stopping.Token));

                    if (ReferenceEquals(settled, signIn))
                    {
                        // Asked to be allowed again, which a kept identity usually spares us.
                        var link = await signIn;
                        lock (_gate)
                        {
                            _pending = opening;
                            _signIn = link;
                            _status = "needs_sign_in";
                            _detail = "Uncloud is waiting to be allowed onto the internet.";
                        }
                        logger.LogInformation("Uncloud is waiting to be allowed again. Open {Link}.", link);
                        continue;
                    }
                    if (!ReferenceEquals(settled, opening))
                        throw new LibraryException(
                            $"{options.Executable} didn’t report an address in time.", "unavailable");

                    var hostname = await opening;
                    Settle(hostname, "on", null);
                    backoff = TimeSpan.FromSeconds(2);
                    logger.LogInformation("Uncloud is reachable from anywhere again at https://{Hostname}", hostname);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
                catch (Exception failure)
                {
                    logger.LogWarning(failure, "Couldn’t open a tunnel to this Uncloud; trying again");
                    lock (_gate) { if (ReferenceEquals(_tunnel, next)) _tunnel = null; }
                    if (next is not null) await Retire(next);
                    Settle(null, "reconnecting", "Uncloud couldn’t open a tunnel and is trying again.");
                }
            }
        });
    }

    private async Task Retire(ITunnel tunnel)
    {
        try { await tunnel.DisposeAsync(); }
        catch (Exception failure) { logger.LogWarning(failure, "Couldn’t stop a tunnel that had already gone"); }
    }

    private void Settle(string? hostname, string status, string? detail)
    {
        lock (_gate) { _hostname = hostname; _status = status; _detail = detail; }
        _answering = hostname;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stopping.IsCancellationRequested) await _stopping.CancelAsync();
        ITunnel? tunnel;
        lock (_gate)
        {
            tunnel = _tunnel;
            _tunnel = null; _pending = null; _signIn = null; _status = "off"; _hostname = null;
        }
        if (tunnel is not null) await tunnel.DisposeAsync();
        _stopping.Dispose();
    }
}

/// <summary>
/// A tunnel that is somebody else's program. Uncloud runs it, reads the address out of what it
/// prints, and stops it on the way out so a funnel never outlives the host it was opened for.
/// </summary>
public sealed class ProcessTunnel(RemoteAccessOptions options, ILogger logger) : ITunnel
{
    private const int Remembered = 20;

    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _address = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _signIn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // The last thing the program said. An address is one line out of dozens, and everything a
    // failure to sign in or to reach the service would have explained is in the rest of them.
    private readonly Queue<string> _recent = new();
    private Process? _process;

    public Task Closed => _closed.Task;
    public Task<string> SignInRequired => _signIn.Task;

    public async Task<string> OpenAsync(int port, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(options.Executable)
        {
            Arguments = options.ArgumentsFor(port),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, line) => Read(line.Data);
        process.ErrorDataReceived += (_, line) => Read(line.Data);
        process.Exited += (_, _) =>
        {
            _address.TrySetException(new LibraryException(
                $"{options.Executable} stopped with code {process.ExitCode} instead of opening a tunnel, "
                + $"saying:{Environment.NewLine}{Said()}", "unavailable"));
            _signIn.TrySetCanceled();
            _closed.TrySetResult();
        };
        _process = process;

        try { process.Start(); }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new LibraryException(
                $"Uncloud couldn’t run “{options.Executable}”. Install it and make sure it is on the "
                + "PATH, or set Homebase__RemoteAccess__Command to where it lives.", "not_configured");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // A named Cloudflare tunnel announces no address of its own, so its configured hostname
        // is the answer already and there would be nothing to wait for but the first failure.
        // Nothing else: under the builtin provider a hostname is the name to ask the tailnet
        // for, and taking it as an answer would report a tunnel nobody had allowed as open.
        if (options.AnnouncesNothing && options.Hostname is { Length: > 0 } configured)
            return configured;
        return await _address.Task.WaitAsync(cancellationToken);
    }

    private void Read(string? line)
    {
        if (line is null) return;
        lock (_recent)
        {
            _recent.Enqueue(line);
            while (_recent.Count > Remembered) _recent.Dequeue();
        }
        logger.LogDebug("{Executable}: {Line}", options.Executable, line);
        // Asked for once and only once: the program repeats itself while it waits, and the
        // person is already looking at the link.
        if (options.SignInPrompt?.Match(line) is { Success: true } asked)
            _signIn.TrySetResult(asked.Groups[1].Value);
        if (_address.Task.IsCompleted) return;
        if (options.Announcement.Match(line) is { Success: true } found)
            _address.TrySetResult(found.Groups[1].Value);
    }

    private string Said()
    {
        lock (_recent) return _recent.Count == 0 ? "nothing at all." : string.Join(Environment.NewLine, _recent);
    }

    public async ValueTask DisposeAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        // Stopped before it ever gave an address — a timeout, most often. Whatever it did say is
        // the only account of why, and this is the last moment anybody can be told.
        if (!_address.Task.IsCompletedSuccessfully)
            logger.LogWarning("{Executable} never opened a tunnel, saying:{Break}{Said}",
                options.Executable, Environment.NewLine, Said());
        try
        {
            if (!process.HasExited)
            {
                // The whole tree: cloudflared and tailscale both leave children behind.
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            }
        }
        catch (Exception failure) when (failure is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception or OperationCanceledException or AggregateException)
        {
            // Already gone, or refusing to go. Either way there is nothing left to wait for.
        }
        finally
        {
            _closed.TrySetResult();
            _address.TrySetCanceled();
            _signIn.TrySetCanceled();
            process.Dispose();
        }
    }
}
