using System.Net;
using Homebase.Core;
using Homebase.Core.Nodes;
using Homebase.Core.Providers;
using Homebase.Server;
using Microsoft.Data.Sqlite;

// A published executable can be launched from Finder or any working directory.
var publishedAssets = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(publishedAssets) ? AppContext.BaseDirectory : null
});
var port = builder.Configuration.GetValue("Homebase:Port", 5210);
// Explicit loopback binding: environment URLs must never expose the local filesystem to the LAN.
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
var defaultConfig = OperatingSystem.IsMacOS()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Homebase")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homebase");
builder.Services.AddSingleton(provider => new SettingsStore(
    provider.GetRequiredService<IConfiguration>()["Homebase:ConfigDirectory"] ?? defaultConfig));
builder.Services.AddSingleton<MetadataIndex>();
builder.Services.AddSingleton<LibraryService>();
builder.Services.AddSingleton<IFolderPicker, NativeFolderPicker>();
builder.Services.AddSingleton(provider => new DropboxTokenStore(
    provider.GetRequiredService<IConfiguration>()["Homebase:ConfigDirectory"] ?? defaultConfig));
// One shared client; downloads of large files need a generous timeout.
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
builder.Services.AddSingleton(provider => new DropboxApi(
    provider.GetRequiredService<HttpClient>(),
    provider.GetRequiredService<DropboxTokenStore>(),
    provider.GetRequiredService<IConfiguration>()["Homebase:Dropbox:AppKey"]));
builder.Services.AddSingleton<IDropboxApi>(provider => provider.GetRequiredService<DropboxApi>());
builder.Services.AddSingleton<ImportLog>();
builder.Services.AddSingleton<ImportService>();
builder.Services.AddSingleton<DropboxAuthFlow>();
builder.Services.AddSingleton(provider => new SyncthingHost(
    provider.GetRequiredService<IConfiguration>()["Homebase:ConfigDirectory"] ?? defaultConfig,
    provider.GetRequiredService<IConfiguration>(),
    provider.GetRequiredService<ILogger<SyncthingHost>>(),
    provider.GetRequiredService<HttpClient>()));
builder.Services.AddSingleton<ISyncthingEndpoint>(provider => provider.GetRequiredService<SyncthingHost>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<SyncthingHost>());
builder.Services.AddSingleton<ISyncthingApi, SyncthingApi>();
builder.Services.AddSingleton<NodeService>();
var redirectUri = $"http://localhost:{port}/api/providers/dropbox/callback";

var app = builder.Build();
app.Use(async (context, next) =>
{
    var request = context.Request;
    // Host validation also prevents DNS rebinding to a loopback service.
    if (request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    // Dropbox returns the browser here by redirect, so this one path is necessarily cross-site.
    // It carries no authority of its own: the state parameter is checked before the code is used.
    var providerCallback = request.Path.Equals("/api/providers/dropbox/callback");
    if (request.Path.StartsWithSegments("/api") && !providerCallback)
    {
        context.Response.Headers.CacheControl = "no-store";
        var origin = request.Headers.Origin.ToString();
        var sameOrigin = origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme == request.Scheme && uri.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase));
        if (!sameOrigin || request.Headers["Sec-Fetch-Site"] == "cross-site"
            || (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && request.Headers["X-Homebase-Request"] != "1"))
        {
            await Results.Problem("Open Uncloud on this computer to make this request.", statusCode: 403).ExecuteAsync(context);
            return;
        }
    }
    try { await next(context); }
    catch (Exception error) when (!context.Response.HasStarted && error is LibraryException or IOException or UnauthorizedAccessException or SqliteException or ArgumentException)
    {
        var (status, detail) = error switch
        {
            LibraryException library => (library.Code switch
            {
                "not_found" => 404,
                "not_configured" or "unavailable" or "busy" or "conflict"
                    or "provider_unconfigured" or "provider_disconnected" or "provider_auth" or "provider_failed"
                    or "invalid_device" or "no_devices" or "node_failed" => 409,
                "unsupported" => 501,
                _ => 400
            }, library.Message),
            UnauthorizedAccessException => (403, "Uncloud can’t access this folder. Check its permissions and macOS privacy settings."),
            SqliteException => (500, "Uncloud couldn’t update its local index. Check disk space and folder permissions. Your files haven’t been changed."),
            ArgumentException => (400, "This folder path isn’t valid."),
            _ => (409, "The file or drive isn’t available. Check it in Finder and refresh.")
        };
        app.Logger.LogWarning(error, "Uncloud operation failed");
        await Results.Problem(detail, statusCode: status).ExecuteAsync(context);
    }
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/library", (LibraryService library, IFolderPicker picker) =>
{
    var state = library.State;
    return Results.Ok(new { state.RootPath, state.Name, canPickFolder = picker.IsSupported });
});
app.MapPut("/api/library", async (SelectRoot request, LibraryService library, CancellationToken cancellationToken) =>
    Results.Ok(await library.SelectRootAsync(request.Path, cancellationToken)));
app.MapPost("/api/folder-picker", async (IFolderPicker picker, CancellationToken cancellationToken) =>
    Results.Ok(new { path = await picker.ChooseAsync(cancellationToken) }));
app.MapGet("/api/storage", (LibraryService library) =>
{
    var root = library.State.RootPath;
    var report = root is null ? null : Storage.For(root);
    return Results.Ok(new { freeBytes = report?.FreeBytes, totalBytes = report?.TotalBytes });
});
app.MapGet("/api/files", async (string? path, LibraryService library, CancellationToken cancellationToken) =>
    Results.Ok(await library.BrowseAsync(path, cancellationToken)));
app.MapGet("/api/files/download", async (string path, LibraryService library, CancellationToken cancellationToken) =>
{
    var (stream, name) = await library.OpenFileAsync(path, cancellationToken);
    // Always download arbitrary user content; HTML/SVG must never run on the app's origin.
    return Results.File(stream, "application/octet-stream", name, enableRangeProcessing: true);
});
app.MapGet("/api/providers/dropbox", (DropboxApi dropbox) =>
    Results.Ok(new { configured = dropbox.IsConfigured, connected = dropbox.IsConnected, accountName = dropbox.AccountName }));
app.MapPost("/api/providers/dropbox/connect", (DropboxApi dropbox, DropboxAuthFlow flow) =>
    Results.Ok(new { authorizeUrl = flow.Begin(dropbox.AppKey, redirectUri) }));
app.MapPost("/api/providers/dropbox/disconnect", (DropboxApi dropbox) =>
{
    dropbox.Disconnect();
    return Results.Ok(new { connected = false });
});
// Dropbox sends the browser back here. Responses are redirects, not JSON, because a person is looking at them.
app.MapGet("/api/providers/dropbox/callback", async (string? code, string? state, string? error, DropboxApi dropbox, DropboxAuthFlow flow, CancellationToken cancellationToken) =>
{
    if (error is not null || code is null) return Results.Redirect("/?dropbox=denied");
    try
    {
        await dropbox.ConnectAsync(code, flow.Consume(state), redirectUri, cancellationToken);
        return Results.Redirect("/?dropbox=connected");
    }
    catch (Exception failure) when (failure is LibraryException or HttpRequestException)
    {
        app.Logger.LogWarning(failure, "Dropbox sign-in failed");
        return Results.Redirect("/?dropbox=failed");
    }
});
app.MapGet("/api/providers/dropbox/files", async (string? path, IDropboxApi dropbox, CancellationToken cancellationToken) =>
    Results.Ok(await dropbox.ListFolderAsync(path ?? "", cancellationToken)));

app.MapGet("/api/imports", (ImportService imports) => Results.Ok(imports.Imported()));
app.MapPost("/api/imports", async (ImportRequest request, ImportService imports, CancellationToken cancellationToken) =>
    Results.Ok(await imports.ImportAsync(request.RemotePath, cancellationToken)));
app.MapGet("/api/imports/estimate", async (string remotePath, ImportService imports, CancellationToken cancellationToken) =>
    Results.Ok(await imports.MeasureAsync(remotePath, cancellationToken)));
app.MapGet("/api/nodes", async (NodeService nodes, CancellationToken cancellationToken) =>
    Results.Ok(await nodes.StatusAsync(cancellationToken)));
app.MapPost("/api/nodes", async (PairNode request, NodeService nodes, CancellationToken cancellationToken) =>
{
    await nodes.PairAsync(request.DeviceId, request.Name, cancellationToken);
    return Results.Ok(await nodes.StatusAsync(cancellationToken));
});
app.MapPost("/api/nodes/folders", async (ShareFolder request, NodeService nodes, CancellationToken cancellationToken) =>
    Results.Ok(await nodes.ShareAsync(request.Path, request.DeviceIds ?? [], cancellationToken)));
app.MapPost("/api/nodes/folders/accept", async (AcceptFolder request, NodeService nodes, CancellationToken cancellationToken) =>
    Results.Ok(await nodes.AcceptAsync(request.FolderId, request.Path, cancellationToken)));
app.Map("/api/{**path}", () => Results.Problem("This endpoint doesn’t exist.", statusCode: 404));
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();

public sealed record SelectRoot(string Path);
public sealed record ImportRequest(string RemotePath);
public sealed record PairNode(string DeviceId, string? Name);
public sealed record ShareFolder(string Path, IReadOnlyList<string>? DeviceIds);
public sealed record AcceptFolder(string FolderId, string? Path);
public partial class Program;
