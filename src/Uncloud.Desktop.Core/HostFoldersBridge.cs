using Homebase.Core;
using Homebase.Core.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Uncloud.Desktop;

/// <summary>
/// The Mac's folders, read for Uncloud by this app, as the person signed in at the Mac.
///
/// On a host where Uncloud runs in an account of its own, that account can't read anybody's
/// folders — that is what keeps everybody's files out of reach of whoever is signed in here, and it
/// works both ways. So bringing files in from somebody's Documents, or the folder their Google
/// Drive app keeps, goes through here instead: Uncloud asks, and this reads with that person's own
/// permissions, while the app is open.
///
/// It answers on a socket in a folder only this person and Uncloud's account can open, and it never
/// reads anything of Uncloud's own — which it couldn't anyway, but saying no is better than trying.
/// </summary>
public sealed class HostFoldersBridge : IAsyncDisposable
{
    private const int Rewrites = 8;
    private readonly WebApplication _app;

    private HostFoldersBridge(WebApplication app, string socket)
    {
        _app = app;
        Socket = socket;
    }

    public string Socket { get; }

    /// <param name="socket">Where to answer. Its folder must already be there, made so Uncloud's account can reach it.</param>
    /// <param name="folders">This computer's folders, as this person sees them.</param>
    /// <param name="refused">Uncloud's own folders, which are never read from here.</param>
    public static async Task<HostFoldersBridge> StartAsync(
        string socket, LocalHostFolders folders, IReadOnlyList<string> refused, CancellationToken cancellationToken)
    {
        // Made by the installer, owned by this person and handed to Uncloud's account's group. One
        // made here would belong to nobody Uncloud can reach, so it is not made here.
        var directory = Path.GetDirectoryName(socket)!;
        if (!Directory.Exists(directory))
            throw new IOException($"{directory} isn’t there, so Uncloud can’t be handed this Mac’s folders.");
        // Left behind by a copy of the app that didn't get to close it.
        if (File.Exists(socket)) File.Delete(socket);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socket));
        var app = builder.Build();
        Map(app, folders, refused);
        await app.StartAsync(cancellationToken);
        // Opening a socket takes write permission on it. Until this, only this person has that; from
        // here, Uncloud's account does too, through the group the folder gave the socket.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(socket,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        return new HostFoldersBridge(app, socket);
    }

    private static void Map(WebApplication app, LocalHostFolders folders, IReadOnlyList<string> refused)
    {
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (Exception failure) when (!context.Response.HasStarted
                && failure is LibraryException or IOException or UnauthorizedAccessException)
            {
                var (status, code, message) = failure switch
                {
                    LibraryException refusal => (refusal.Code switch
                    {
                        "not_found" => 404,
                        "forbidden" => 403,
                        "unavailable" => 409,
                        _ => 400
                    }, refusal.Code, refusal.Message),
                    UnauthorizedAccessException => (403, "forbidden",
                        "Uncloud isn’t allowed to read that folder. On the host Mac, allow Uncloud under System "
                        + "Settings → Privacy & Security → Files and Folders."),
                    _ => (409, "unavailable", "That file or drive isn’t available. Check it in Finder and try again.")
                };
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(new HostFoldersWire.Refusal(code, message), HostFoldersWire.Json);
            }
        });

        IImportSource Source(string root, string? name)
        {
            // Uncloud's own folders are its own account's alone. Compared by the real folder, so a
            // link to one is refused as surely as its own name.
            var real = LocalHostFolders.Resolve(root, Rewrites, out var settled);
            if (!settled || refused.Any(folder => Nested(real, LocalHostFolders.Resolve(folder, Rewrites, out _))))
                throw new LibraryException("That folder is Uncloud’s own, and isn’t read from here.", "forbidden");
            return folders.Open(new ImportPlace("", string.IsNullOrWhiteSpace(name) ? Path.GetFileName(root) : name,
                root, DateTimeOffset.UtcNow, "", Shared: false));
        }

        app.MapGet(HostFoldersWire.Candidates, (int? rewrites) =>
            Results.Json(folders.Candidates(rewrites ?? Rewrites), HostFoldersWire.Json));
        app.MapGet(HostFoldersWire.Settle, (string path, int? rewrites) =>
        {
            var real = folders.Settle(path, rewrites ?? Rewrites, out var settled);
            return Results.Json(new HostFoldersWire.Settled(real, settled), HostFoldersWire.Json);
        });
        app.MapGet(HostFoldersWire.Normalize, (string path) =>
            Results.Json(new HostFoldersWire.Normalized(folders.Normalize(path)), HostFoldersWire.Json));
        app.MapGet(HostFoldersWire.Exists, (string path) =>
            Results.Json(new HostFoldersWire.Existence(folders.Exists(path)), HostFoldersWire.Json));
        app.MapGet(HostFoldersWire.Metadata, async (string root, string? name, string? path, CancellationToken cancellationToken) =>
            Results.Json(await Source(root, name).GetMetadataAsync(path ?? "", cancellationToken), HostFoldersWire.Json));
        app.MapGet(HostFoldersWire.List, async (string root, string? name, string? path, CancellationToken cancellationToken) =>
            Results.Json(await Source(root, name).ListFolderAsync(path ?? "", cancellationToken), HostFoldersWire.Json));
        app.MapGet(HostFoldersWire.Open, async (string root, string? name, string? path, CancellationToken cancellationToken) =>
            Results.Stream(await Source(root, name).OpenAsync(path ?? "", cancellationToken), "application/octet-stream"));
    }

    private static bool Nested(string first, string second)
    {
        static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);
        bool Inside(string outer, string inner) =>
            inner.StartsWith(Trim(outer) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return Trim(first).Equals(Trim(second), StringComparison.OrdinalIgnoreCase)
            || Inside(first, second) || Inside(second, first);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { File.Delete(Socket); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
}
