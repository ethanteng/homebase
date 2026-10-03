using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Homebase.Core.Sync;

/// <summary>
/// An Uncloud host found on this network: the name of the computer it runs on, and the address
/// other computers reach it at, or null when it can't be reached from anywhere but itself yet.
/// </summary>
public sealed record FoundHost(string Name, string? Url);

/// <summary>
/// Telling computers on the home network where this Uncloud is, and finding it from one of them,
/// so that nobody types its address. It goes through the Bonjour macOS already runs, so there is
/// nothing to install. What is announced is the address alone, which is no key to anything:
/// everybody still signs in. Anywhere without Bonjour nothing is announced and nothing found,
/// and the address is typed as it always was.
/// </summary>
public static class Bonjour
{
    public const string ServiceType = "_uncloud._tcp";

    /// <summary>
    /// Announces this host on the network until the announcement is disposed, under the computer's
    /// own name. Null where Bonjour isn't there to do it.
    /// </summary>
    /// <param name="url">Where other computers reach this host, or null when nowhere yet.</param>
    public static Announcement? Announce(int port, string? url)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var txt = Txt(url);
            // No name, so Bonjour uses the computer's — "Ethan’s Mac mini" — and renames it if
            // another host on the network already has it. No callback: nothing here waits to hear
            // that it worked, and the announcement stands for as long as the reference is held.
            var error = DnsSd.DNSServiceRegister(out var reference, 0, 0, null, ServiceType, null, null,
                (ushort)IPAddress.HostToNetworkOrder((short)port), (ushort)txt.Length, txt, IntPtr.Zero, IntPtr.Zero);
            return error == 0 ? new Announcement(reference, url) : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>
    /// Every Uncloud announcing itself on this network, after listening for <paramref name="wait"/>.
    /// Nothing at all where Bonjour isn't available, or the person hasn't let Uncloud look.
    /// </summary>
    public static Task<IReadOnlyList<FoundHost>> FindAsync(TimeSpan wait, CancellationToken cancellationToken) =>
        OperatingSystem.IsMacOS()
            ? Task.Run(() => Find(wait, cancellationToken), cancellationToken)
            : Task.FromResult<IReadOnlyList<FoundHost>>([]);

    /// <summary>
    /// The announcement's TXT record: a version, so a later Uncloud can tell what it is reading,
    /// and the address when there is one.
    /// </summary>
    internal static byte[] Txt(string? url)
    {
        var record = new List<byte>();
        foreach (var entry in url is { Length: > 0 } ? new[] { "v=1", $"url={url}" } : ["v=1"])
        {
            var bytes = Encoding.UTF8.GetBytes(entry);
            if (bytes.Length > byte.MaxValue) continue;
            record.Add((byte)bytes.Length);
            record.AddRange(bytes);
        }
        return [.. record];
    }

    /// <summary>The address in a TXT record, if it carries one; anything malformed carries none.</summary>
    internal static string? UrlFrom(ReadOnlySpan<byte> txt)
    {
        while (txt.Length > 0)
        {
            var length = txt[0];
            if (length + 1 > txt.Length) return null;
            var entry = Encoding.UTF8.GetString(txt.Slice(1, length));
            if (entry.StartsWith("url=", StringComparison.OrdinalIgnoreCase) && entry.Length > 4) return entry[4..];
            txt = txt[(length + 1)..];
        }
        return null;
    }

    private static IReadOnlyList<FoundHost> Find(TimeSpan wait, CancellationToken cancellationToken)
    {
        try
        {
            // The same host turns up once for each way this computer can reach it — Wi-Fi and
            // Ethernet, say — and is one host for all that.
            var seen = new List<(string Name, string Type, string Domain, uint Interface)>();
            DnsSd.BrowseReply browsed = (_, flags, interfaceIndex, error, name, type, domain, _) =>
            {
                if (error != 0 || (flags & DnsSd.FlagsAdd) == 0) return;
                var service = (Text(name), Text(type), Text(domain), interfaceIndex);
                if (!seen.Any(known => known.Name == service.Item1 && known.Domain == service.Item3)) seen.Add(service);
            };
            if (DnsSd.DNSServiceBrowse(out var browser, 0, 0, ServiceType, null, browsed, IntPtr.Zero) != 0) return [];
            try { Pump(browser, wait, () => false, cancellationToken); }
            finally
            {
                DnsSd.DNSServiceRefDeallocate(browser);
                GC.KeepAlive(browsed);
            }

            var found = new List<FoundHost>();
            foreach (var service in seen)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? url = null;
                var resolved = false;
                DnsSd.ResolveReply reply = (_, _, _, error, _, _, _, length, txt, _) =>
                {
                    resolved = true;
                    if (error != 0 || txt == IntPtr.Zero) return;
                    var bytes = new byte[length];
                    Marshal.Copy(txt, bytes, 0, length);
                    url = UrlFrom(bytes);
                };
                if (DnsSd.DNSServiceResolve(out var resolver, 0, service.Interface, service.Name, service.Type, service.Domain, reply, IntPtr.Zero) != 0)
                    continue;
                try { Pump(resolver, TimeSpan.FromSeconds(2), () => resolved, cancellationToken); }
                finally
                {
                    DnsSd.DNSServiceRefDeallocate(resolver);
                    GC.KeepAlive(reply);
                }
                if (resolved) found.Add(new FoundHost(service.Name, url));
            }
            return found;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return []; }
    }

    /// <summary>
    /// Hands Bonjour's answers to the callbacks above, on this thread, until <paramref name="done"/>
    /// or the time is up. It wakes at least every quarter of a second, so a search somebody has
    /// given up on stops promptly.
    /// </summary>
    private static void Pump(IntPtr reference, TimeSpan wait, Func<bool> done, CancellationToken cancellationToken)
    {
        var socket = DnsSd.DNSServiceRefSockFD(reference);
        if (socket < 0) return;
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (!done() && !cancellationToken.IsCancellationRequested)
        {
            var left = deadline - Environment.TickCount64;
            if (left <= 0) return;
            var descriptors = new[] { new DnsSd.PollDescriptor { Descriptor = socket, Events = DnsSd.PollIn } };
            var ready = DnsSd.poll(descriptors, 1, (int)Math.Min(left, 250));
            if (ready < 0) return;
            if (ready > 0 && DnsSd.DNSServiceProcessResult(reference) != 0) return;
        }
    }

    private static string Text(IntPtr text) => Marshal.PtrToStringUTF8(text) ?? "";

    /// <summary>
    /// This host, as announced. Disposing it withdraws the announcement, which Bonjour also does by
    /// itself when the process ends.
    /// </summary>
    public sealed class Announcement : IDisposable
    {
        private readonly Lock _gate = new();
        private IntPtr _reference;

        internal Announcement(IntPtr reference, string? url)
        {
            _reference = reference;
            Url = url;
        }

        /// <summary>The address announced now.</summary>
        public string? Url { get; private set; }

        /// <summary>
        /// Announces a new address — a tunnel that came up after the host started, or went away —
        /// and does nothing when it hasn't changed.
        /// </summary>
        public void Update(string? url)
        {
            lock (_gate)
            {
                if (_reference == IntPtr.Zero || url == Url) return;
                var txt = Txt(url);
                if (DnsSd.DNSServiceUpdateRecord(_reference, IntPtr.Zero, 0, (ushort)txt.Length, txt, 0) == 0) Url = url;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_reference == IntPtr.Zero) return;
                DnsSd.DNSServiceRefDeallocate(_reference);
                _reference = IntPtr.Zero;
            }
        }
    }

    /// <summary>The parts of dns_sd.h used here, which macOS keeps in libSystem.</summary>
    private static class DnsSd
    {
        private const string Library = "/usr/lib/libSystem.dylib";

        public const uint FlagsAdd = 0x2;
        public const short PollIn = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        public struct PollDescriptor
        {
            public int Descriptor;
            public short Events;
            public short ReturnedEvents;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void BrowseReply(IntPtr reference, uint flags, uint interfaceIndex, int error,
            IntPtr name, IntPtr type, IntPtr domain, IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ResolveReply(IntPtr reference, uint flags, uint interfaceIndex, int error,
            IntPtr fullName, IntPtr hostTarget, ushort port, ushort txtLength, IntPtr txt, IntPtr context);

        [DllImport(Library)]
        public static extern int DNSServiceRegister(out IntPtr reference, uint flags, uint interfaceIndex,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? name, [MarshalAs(UnmanagedType.LPUTF8Str)] string type,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? domain, [MarshalAs(UnmanagedType.LPUTF8Str)] string? host,
            ushort port, ushort txtLength, byte[]? txt, IntPtr callback, IntPtr context);

        [DllImport(Library)]
        public static extern int DNSServiceUpdateRecord(IntPtr reference, IntPtr record, uint flags,
            ushort length, byte[] data, uint timeToLive);

        [DllImport(Library)]
        public static extern int DNSServiceBrowse(out IntPtr reference, uint flags, uint interfaceIndex,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string type, [MarshalAs(UnmanagedType.LPUTF8Str)] string? domain,
            BrowseReply callback, IntPtr context);

        [DllImport(Library)]
        public static extern int DNSServiceResolve(out IntPtr reference, uint flags, uint interfaceIndex,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string type,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string domain, ResolveReply callback, IntPtr context);

        [DllImport(Library)]
        public static extern int DNSServiceRefSockFD(IntPtr reference);

        [DllImport(Library)]
        public static extern int DNSServiceProcessResult(IntPtr reference);

        [DllImport(Library)]
        public static extern void DNSServiceRefDeallocate(IntPtr reference);

        [DllImport(Library)]
        public static extern int poll([In, Out] PollDescriptor[] descriptors, uint count, int timeout);
    }
}
