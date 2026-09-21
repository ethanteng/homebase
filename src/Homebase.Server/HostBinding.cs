using System.Net;
using Homebase.Core;

namespace Homebase.Server;

/// <summary>
/// Where this host listens and which names it answers to. A single-user Uncloud could bind
/// loopback and be done; a host serving several people has to be reachable, which makes the
/// host-header allowlist load-bearing rather than belt-and-braces.
/// </summary>
public sealed record HostBinding(
    IPAddress Address,
    int Port,
    IReadOnlySet<string> AllowedHosts,
    string? CertificatePath,
    string? CertificatePassword,
    string PublicUrl)
{
    public bool IsLoopback => IPAddress.IsLoopback(Address);
    public bool IsSecure => CertificatePath is not null;

    public static HostBinding From(IConfiguration configuration)
    {
        var port = configuration.GetValue("Homebase:Port", 5210);
        var bind = configuration["Homebase:Bind"] ?? "127.0.0.1";
        if (!IPAddress.TryParse(bind, out var address))
            throw new LibraryException($"Homebase__Bind must be an IP address, not “{bind}”.");

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Loopback always answers, so an administrator at the machine can always get in.
            "localhost", "127.0.0.1", "[::1]", "::1"
        };
        foreach (var host in (configuration["Homebase:AllowedHosts"] ?? "")
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            allowed.Add(host);

        var loopback = IPAddress.IsLoopback(address);
        if (!loopback && allowed.Count == 4)
            // Without this, a name in somebody's DNS that resolves to this host turns any
            // browser on the network into a way in. The list is the defence, so it is required.
            throw new LibraryException(
                $"Uncloud is set to listen on {bind}, which is more than this computer. Set "
                + "Homebase__AllowedHosts to the names people will use to reach it, such as "
                + "Homebase__AllowedHosts=uncloud.local,192.168.1.10", "not_configured");

        var certificate = configuration["Homebase:Certificate:Path"];
        if (certificate is { Length: > 0 } && !File.Exists(certificate))
            throw new LibraryException($"There's no certificate at {certificate}.", "not_found");
        if (certificate is { Length: 0 }) certificate = null;

        var scheme = certificate is null ? "http" : "https";
        var publicUrl = (configuration["Homebase:PublicUrl"] ?? $"{scheme}://localhost:{port}").TrimEnd('/');

        return new HostBinding(address, port, allowed, certificate,
            configuration["Homebase:Certificate:Password"], publicUrl);
    }

    /// <summary>What to say at startup when passwords would cross a network in the clear.</summary>
    public string? Warning => IsLoopback || IsSecure ? null
        : $"Uncloud is listening on {Address}:{Port} without a certificate, so passwords and "
          + "files cross the network unencrypted. Set Homebase__Certificate__Path, or put a "
          + "reverse proxy that terminates TLS in front of it.";
}
