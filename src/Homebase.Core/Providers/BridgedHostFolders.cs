using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace Homebase.Core.Providers;

/// <summary>
/// What the Uncloud app on the host and the server it serves the host's folders to say to each
/// other, in one place so the two ends can't drift apart.
/// </summary>
public static class HostFoldersWire
{
    public const string Candidates = "/candidates";
    public const string Settle = "/settle";
    public const string Normalize = "/normalize";
    public const string Exists = "/exists";
    public const string Metadata = "/metadata";
    public const string List = "/list";
    public const string Open = "/open";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Settled(string Path, bool IsSettled);
    public sealed record Normalized(string Path);
    public sealed record Existence(bool Exists);
    /// <summary>A refusal, carried across in the words and code it was refused with.</summary>
    public sealed record Refusal(string Code, string Message);
}

/// <summary>
/// The host's folders, read by the Uncloud app running as the person signed in at the host, over a
/// socket only that person and Uncloud's own account can open.
///
/// This is how a host that runs in an account of its own still brings files in from somebody's
/// Documents or the folder their Google Drive app keeps: that account can't read them — which is
/// the point of it — so the app reads them on its behalf, with that person's own permissions, while
/// it is open. When it isn't, nothing is offered, and anything reaching for a folder is told why.
/// </summary>
public sealed class BridgedHostFolders : IHostFolders, IDisposable
{
    public const string AppNotOpen =
        "The Uncloud app isn’t open on the host Mac, and Uncloud needs it to read that Mac’s folders. "
        + "Open Uncloud there, then try again.";

    private const string NotAnswering =
        "The Uncloud app on the host Mac didn’t answer. Check it’s open there, then try again.";

    private const string StoppedSending =
        "The Uncloud app on the host Mac stopped sending this file. Check it’s still open there, then try again.";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http;

    public BridgedHostFolders(string socket)
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await connection.ConnectAsync(new UnixDomainSocketEndPoint(socket), cancellationToken);
                    return new NetworkStream(connection, ownsSocket: true);
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }
            }
        })
        {
            // Only a name for the request line: every connection goes to the socket above.
            BaseAddress = new Uri("http://uncloud-app"),
            // A file is read for as long as it takes; everything else is held to Patience below.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public HostFolderCandidates Candidates(int rewrites) =>
        Wait(Get<HostFolderCandidates>(HostFoldersWire.Candidates, ("rewrites", rewrites.ToString()), CancellationToken.None));

    public string Settle(string path, int rewrites, out bool settled)
    {
        try
        {
            var answer = Wait(Get<HostFoldersWire.Settled>(HostFoldersWire.Settle,
                [("path", path), ("rewrites", rewrites.ToString())], CancellationToken.None));
            settled = answer.IsSettled;
            return answer.Path;
        }
        catch (LibraryException)
        {
            // As a path that can't be resolved at all: it comes back as it went in, which is the
            // name it was stored under. Nothing is read through it while the app is away.
            settled = true;
            return path;
        }
    }

    public string Normalize(string path) =>
        Wait(Get<HostFoldersWire.Normalized>(HostFoldersWire.Normalize, ("path", path), CancellationToken.None)).Path;

    public bool Exists(string path) =>
        Wait(Get<HostFoldersWire.Existence>(HostFoldersWire.Exists, ("path", path), CancellationToken.None)).Exists;

    public IImportSource Open(ImportPlace place) => new Source(this, place);

    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private Task<T> Get<T>(string route, (string Name, string Value) parameter, CancellationToken cancellationToken) =>
        Get<T>(route, [parameter], cancellationToken);

    private async Task<T> Get<T>(string route, (string Name, string Value)[] parameters, CancellationToken cancellationToken)
    {
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        patience.CancelAfter(Patience);
        try
        {
            using var response = await SendAsync(route, parameters, HttpCompletionOption.ResponseContentRead, patience.Token);
            return await response.Content.ReadFromJsonAsync<T>(HostFoldersWire.Json, patience.Token)
                ?? throw new LibraryException(AppNotOpen, "unavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LibraryException(NotAnswering, "unavailable");
        }
    }

    private async Task<Stream> OpenAsync((string Name, string Value)[] parameters, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        using (var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            patience.CancelAfter(Patience);
            try { response = await SendAsync(HostFoldersWire.Open, parameters, HttpCompletionOption.ResponseHeadersRead, patience.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LibraryException(NotAnswering, "unavailable");
            }
        }
        try { return new Owned(await response.Content.ReadAsStreamAsync(cancellationToken), response); }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        string route, (string Name, string Value)[] parameters, HttpCompletionOption completion,
        CancellationToken cancellationToken)
    {
        var query = string.Join('&', parameters.Select(parameter =>
            $"{parameter.Name}={Uri.EscapeDataString(parameter.Value)}"));
        HttpResponseMessage response;
        // Nothing listening — the app isn't open — is a refused or missing socket, not a timeout.
        try { response = await _http.GetAsync($"{route}?{query}", completion, cancellationToken); }
        catch (Exception failure) when (failure is HttpRequestException or SocketException or IOException)
        {
            throw new LibraryException(AppNotOpen, "unavailable");
        }
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            HostFoldersWire.Refusal? refusal = null;
            try { refusal = await response.Content.ReadFromJsonAsync<HostFoldersWire.Refusal>(HostFoldersWire.Json, cancellationToken); }
            catch (Exception failure) when (failure is JsonException or NotSupportedException) { }
            throw refusal is { Message.Length: > 0 }
                ? new LibraryException(refusal.Message, refusal.Code)
                : new LibraryException(AppNotOpen, "unavailable");
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>A place on the host, read through the app.</summary>
    private sealed class Source(BridgedHostFolders folders, ImportPlace place) : IImportSource
    {
        public string ProviderId => place.ProviderId;
        public string DestinationPrefix => place.DestinationPrefix;

        public Task<SourceEntry> GetMetadataAsync(string path, CancellationToken cancellationToken) =>
            folders.Get<SourceEntry>(HostFoldersWire.Metadata, [("root", place.Path), ("name", place.Name), ("path", path)], cancellationToken);

        public async Task<IReadOnlyList<SourceEntry>> ListFolderAsync(string path, CancellationToken cancellationToken) =>
            await folders.Get<SourceEntry[]>(HostFoldersWire.List, [("root", place.Path), ("name", place.Name), ("path", path)], cancellationToken);

        public Task<Stream> OpenAsync(string path, CancellationToken cancellationToken) =>
            folders.OpenAsync([("root", place.Path), ("name", place.Name), ("path", path)], cancellationToken);
    }

    /// <summary>A file's contents that let go of the response carrying them when they're closed.</summary>
    private sealed class Owned(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Lost(() => inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { return await inner.ReadAsync(buffer, cancellationToken); }
            catch (Exception failure) when (failure is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
            {
                throw new LibraryException(StoppedSending, "unavailable");
            }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static int Lost(Func<int> read)
        {
            try { return read(); }
            catch (Exception failure) when (failure is HttpRequestException or IOException)
            {
                throw new LibraryException(StoppedSending, "unavailable");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
