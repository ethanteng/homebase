using System.Diagnostics;
using System.Text.RegularExpressions;
using Homebase.Core;

namespace Homebase.Server;

/// <summary>Which service carries traffic from the public internet to this host.</summary>
public enum RemoteAccessProvider { None, Tailscale, Cloudflare }

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
    TimeSpan Timeout)
{
    public bool IsEnabled => Provider is not RemoteAccessProvider.None;

    /// <summary>The address the tunnel is expected to announce, so another URL in the same
    /// output — a documentation link in a banner — is never mistaken for this host's.</summary>
    public Regex Announcement => Provider switch
    {
        RemoteAccessProvider.Tailscale => TailscaleAddress,
        RemoteAccessProvider.Cloudflare => CloudflareAddress,
        _ => throw new InvalidOperationException("Remote access is off.")
    };

    private static readonly Regex TailscaleAddress =
        new(@"https://([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.ts\.net)", RegexOptions.IgnoreCase);
    private static readonly Regex CloudflareAddress =
        new(@"https://([A-Za-z0-9-]+\.trycloudflare\.com)", RegexOptions.IgnoreCase);

    public string Executable => Command is { Length: > 0 } ? Command : Provider switch
    {
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
            RemoteAccessProvider.Tailscale => $"funnel {port}",
            RemoteAccessProvider.Cloudflare when Tunnel is { Length: > 0 } tunnel =>
                $"tunnel --url http://127.0.0.1:{port} run {tunnel}",
            RemoteAccessProvider.Cloudflare => $"tunnel --url http://127.0.0.1:{port}",
            _ => throw new InvalidOperationException("Remote access is off.")
        };
    }

    public static RemoteAccessOptions From(IConfiguration configuration)
    {
        var name = (configuration["Homebase:RemoteAccess:Provider"] ?? "none").Trim();
        if (name.Length == 0) name = "none";
        if (!Enum.TryParse<RemoteAccessProvider>(name, ignoreCase: true, out var provider))
            throw new LibraryException(
                $"Homebase__RemoteAccess__Provider must be none, tailscale or cloudflare, not “{name}”.");

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
}

/// <summary>What the administrator is shown, and what the host check consults.</summary>
public sealed record RemoteAccessState(string Provider, string? Hostname, string? Url, string Status, string? Detail);

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

    public static RemoteAccess From(IConfiguration configuration, ILogger logger) =>
        Override?.Invoke(configuration) ?? new RemoteAccess(RemoteAccessOptions.From(configuration), logger);

    private readonly Func<RemoteAccessOptions, ITunnel> _tunnels = tunnels ?? (each => new ProcessTunnel(each, logger));
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private ITunnel? _tunnel;
    private string? _hostname;
    private string _status = "off";
    private string? _detail;
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
                    _detail);
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
    /// </summary>
    public string Open(int port)
    {
        var tunnel = _tunnels(options);
        lock (_gate) { _tunnel = tunnel; _status = "opening"; }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            deadline.CancelAfter(options.Timeout);
            // Startup is single-threaded and has no synchronisation context, so waiting here
            // cannot deadlock, and there is nothing else for this thread to be doing yet.
            var hostname = tunnel.OpenAsync(port, deadline.Token).GetAwaiter().GetResult();
            Settle(hostname, "on", null);
            logger.LogInformation("Uncloud is reachable from anywhere at https://{Hostname}", hostname);
            return hostname;
        }
        catch (Exception failure)
        {
            // Uncloud is about to stop, and nothing else will come back for this: a tunnel
            // program left running would outlive the host it was started for and keep a name
            // pointed at a port with nothing behind it.
            lock (_gate) { _tunnel = null; _status = "off"; }
            tunnel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (failure is OperationCanceledException && !_stopping.IsCancellationRequested)
                throw new LibraryException(
                    $"{options.Executable} didn’t report an address within {options.Timeout.TotalSeconds:0} "
                    + "seconds, so Uncloud doesn’t know what name to answer to. Check that it is signed "
                    + "in and can reach the internet, or set Homebase__RemoteAccess__Provider=none to "
                    + "start without remote access.", "not_configured");
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
                lock (_gate) current = _tunnel;
                if (current is not null)
                {
                    try { await current.Closed.WaitAsync(_stopping.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception failure) { logger.LogWarning(failure, "The tunnel to this Uncloud stopped"); }
                    if (_stopping.IsCancellationRequested) return;
                    Settle(null, "reconnecting", "The tunnel stopped and Uncloud is opening another.");
                    lock (_gate) { if (ReferenceEquals(_tunnel, current)) _tunnel = null; }
                    await Retire(current);
                }

                try { await Task.Delay(backoff, _stopping.Token); }
                catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));

                ITunnel? next = null;
                try
                {
                    next = _tunnels(options);
                    lock (_gate) _tunnel = next;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    deadline.CancelAfter(options.Timeout);
                    var hostname = await next.OpenAsync(port, deadline.Token);
                    Settle(hostname, "on", null);
                    backoff = TimeSpan.FromSeconds(2);
                    logger.LogInformation("Uncloud is reachable from anywhere again at https://{Hostname}", hostname);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
                catch (Exception failure)
                {
                    logger.LogWarning(failure, "Couldn’t open a tunnel to this Uncloud; trying again");
                    // A tunnel that failed to open is never left as the current one. Waiting on it
                    // to close would be waiting on a program that timed out but is still running,
                    // or one that never started, and neither will ever say so — which would end
                    // the retries for good on the first failure.
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
        lock (_gate) { tunnel = _tunnel; _tunnel = null; _status = "off"; _hostname = null; }
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
    // The last thing the program said. An address is one line out of dozens, and everything a
    // failure to sign in or to reach the service would have explained is in the rest of them.
    private readonly Queue<string> _recent = new();
    private Process? _process;

    public Task Closed => _closed.Task;

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

        // A configured hostname is the answer already: a named Cloudflare tunnel announces no
        // address of its own, so there would be nothing to wait for but the first failure.
        if (options.Hostname is { Length: > 0 } configured) return configured;
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
            process.Dispose();
        }
    }
}
