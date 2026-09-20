using System.Net;
using Homebase.Core;
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
    if (request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        var origin = request.Headers.Origin.ToString();
        var sameOrigin = origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme == request.Scheme && uri.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase));
        if (!sameOrigin || request.Headers["Sec-Fetch-Site"] == "cross-site"
            || (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && request.Headers["X-Homebase-Request"] != "1"))
        {
            await Results.Problem("Open Homebase on this computer to make this request.", statusCode: 403).ExecuteAsync(context);
            return;
        }
    }
    try { await next(context); }
    catch (Exception error) when (!context.Response.HasStarted && error is LibraryException or IOException or UnauthorizedAccessException or SqliteException or ArgumentException)
    {
        var (status, detail) = error switch
        {
            LibraryException library => (library.Code switch { "not_found" => 404, "not_configured" or "unavailable" or "busy" => 409, "unsupported" => 501, _ => 400 }, library.Message),
            UnauthorizedAccessException => (403, "Homebase can’t access this folder. Check its permissions and macOS privacy settings."),
            SqliteException => (500, "Homebase couldn’t update its local index. Check disk space and folder permissions. Your files haven’t been changed."),
            ArgumentException => (400, "This folder path isn’t valid."),
            _ => (409, "The file or drive isn’t available. Check it in Finder and refresh.")
        };
        app.Logger.LogWarning(error, "Homebase operation failed");
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
app.MapGet("/api/files", async (string? path, LibraryService library, CancellationToken cancellationToken) =>
    Results.Ok(await library.BrowseAsync(path, cancellationToken)));
app.MapGet("/api/files/download", async (string path, LibraryService library, CancellationToken cancellationToken) =>
{
    var (stream, name) = await library.OpenFileAsync(path, cancellationToken);
    // Always download arbitrary user content; HTML/SVG must never run on the app's origin.
    return Results.File(stream, "application/octet-stream", name, enableRangeProcessing: true);
});
app.Map("/api/{**path}", () => Results.Problem("This endpoint doesn’t exist.", statusCode: 404));
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();

public sealed record SelectRoot(string Path);
public partial class Program;
