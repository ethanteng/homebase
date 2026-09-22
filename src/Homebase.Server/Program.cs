using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Core.Providers;
using Homebase.Server;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;

// A published executable can be launched from Finder or any working directory.
var publishedAssets = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(publishedAssets) ? AppContext.BaseDirectory : null
});

// Remote access is settled before anything is served. The tunnel's address is the name this
// host answers to and the address Dropbox returns the browser to, so the binding below is built
// from it rather than corrected once requests are already arriving under it.
static string Join(string? existing, params string[] additions) => string.Join(',',
    (existing ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Concat(additions).Distinct(StringComparer.OrdinalIgnoreCase));

var defaultConfig = OperatingSystem.IsMacOS()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Homebase")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homebase");

// Read once before anything is started, so a missing certificate or an unreadable bind address
// is refused while there is still nothing running to clean up.
HostBinding.From(builder.Configuration);

var remoteLog = LoggerFactory.Create(logging => logging.AddConsole());
var remote = RemoteAccess.From(
    builder.Configuration,
    builder.Configuration["Homebase:ConfigDirectory"] ?? defaultConfig,
    remoteLog.CreateLogger("Homebase.RemoteAccess"));
// A tunnel still waiting to be allowed by a person answers with no address. Uncloud comes up
// anyway — everyone on the network is waiting for their files — and starts answering to the
// address as soon as the watch below hears it.
if (remote.IsEnabled)
{
    var announced = remote.Open(builder.Configuration.GetValue("Homebase:Port", 5210));
    var overlay = new Dictionary<string, string?>
    {
        // The tunnel client runs on this machine and reaches Uncloud over loopback: it is the
        // proxy, and naming it is what lets the https:// origin the browser sent be believed
        // while Kestrel itself is serving plain HTTP. This follows from remote access being
        // turned on rather than from an address having arrived — a tunnel allowed a minute from
        // now has to work without restarting the host everybody is already using.
        ["Homebase:TrustedProxies"] = Join(builder.Configuration["Homebase:TrustedProxies"], "127.0.0.1", "::1")
    };
    if (announced is { } name)
    {
        overlay["Homebase:AllowedHosts"] = Join(builder.Configuration["Homebase:AllowedHosts"], name);
        overlay["Homebase:PublicUrl"] = builder.Configuration["Homebase:PublicUrl"] is { Length: > 0 } configured
            ? configured
            : $"https://{name}";
    }
    builder.Configuration.AddInMemoryCollection(overlay);
}

void StopTunnel() => remote.DisposeAsync().AsTask().GetAwaiter().GetResult();

var startup = HostBinding.From(builder.Configuration);
// Explicit binding: environment URLs must never decide who can reach the host's files.
builder.WebHost.ConfigureKestrel(options => options.Listen(startup.Address, startup.Port, listen =>
{
    if (startup.CertificatePath is not null)
        listen.UseHttps(startup.CertificatePath, startup.CertificatePassword);
}));
// Read through the container, not from the builder: a test host replaces this configuration
// after the builder is made, and reading it eagerly would quietly ignore that.
static string ConfigDirectory(IServiceProvider provider, string fallback) =>
    provider.GetRequiredService<IConfiguration>()["Homebase:ConfigDirectory"] ?? fallback;

builder.Services.AddSingleton(remote);
builder.Services.AddSingleton(provider => HostBinding.From(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(provider => new ControlDatabase(ConfigDirectory(provider, defaultConfig)));
builder.Services.AddSingleton(provider => new SecretProtector(ConfigDirectory(provider, defaultConfig)));
builder.Services.AddSingleton(provider => new SettingsStore(ConfigDirectory(provider, defaultConfig)));
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<ConnectorStore>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<UsageService>();
builder.Services.AddSingleton(provider => new HostService(
    provider.GetRequiredService<ControlDatabase>(), provider.GetRequiredService<SettingsStore>()));
builder.Services.AddSingleton<MetadataIndex>();
builder.Services.AddSingleton<ImportLog>();
builder.Services.AddSingleton<IFolderPicker, NativeFolderPicker>();
// One shared client; downloads of large files need a generous timeout.
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
builder.Services.AddSingleton<IDropboxApiFactory>(provider => new DropboxApiFactory(
    provider.GetRequiredService<HttpClient>(),
    provider.GetRequiredService<ConnectorStore>(),
    provider.GetRequiredService<IConfiguration>()["Homebase:Dropbox:AppKey"]));
builder.Services.AddSingleton<UserWorkspaces>();
builder.Services.AddSingleton<DropboxAuthFlow>();
// An import's stage travels as its name, not as whichever number the enum happens to sit at.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
WebApplication app;
try { app = builder.Build(); }
catch { StopTunnel(); throw; }
var binding = app.Services.GetRequiredService<HostBinding>();
// Where Dropbox returns the browser. Read per request rather than once, because a tunnel
// allowed a minute after startup has an address this host could not have known then — and
// sending somebody back to a localhost they aren't sitting at is worse than nothing. A name
// this host was actually given always wins: the tunnel is another way in, not a new identity.
string PublicAddress() =>
    binding.PublicUrlConfigured ? binding.PublicUrl : remote.State.Url ?? binding.PublicUrl;
string RedirectUri() => $"{PublicAddress()}/api/providers/dropbox/callback";
if (binding.Warning is { } warning) app.Logger.LogWarning("{Warning}", warning);
// A tunnel that drops is an outage of reaching this host from outside, never of the host, so
// this reopens in the background while everyone on the network carries on.
remote.Watch(binding.Port);

const string SessionCookie = "uncloud_session";
// Reached without a session. Everything else is refused until somebody signs in.
string[] anonymous =
[
    "/api/health",
    "/api/session",
    "/api/setup",
    "/api/providers/dropbox/callback"
];
// Reached only by an administrator: the host's own folder, and the accounts on it.
string[] administrative =
[
    "/api/host",
    "/api/users",
    "/api/folder-picker",
    "/api/remote-access"
];

// Behind a proxy that terminates TLS, Kestrel sees plain HTTP on a loopback address while the
// browser sent an https:// Origin. Without this the origin comparison below rejects every
// mutation — sign-in included — and the session cookie goes out without Secure. Only the proxies
// named in configuration are believed, and only about the scheme and host.
if (binding.TrustedProxies.Count > 0)
{
    // A tunnel adds the client's own address to that. Everything it carries reaches Kestrel from
    // loopback, so without this the sign-in throttle would count every person on the internet
    // into one bucket, and ten wrong guesses from anywhere would lock out every account on the
    // host, including whoever is sitting at it. Only a tunnel Uncloud opened itself is taken at
    // its word about who the client is: a proxy somebody else configured is believed about the
    // scheme and host it was named for, and nothing more.
    var client = remote.IsEnabled ? ForwardedHeaders.XForwardedFor : ForwardedHeaders.None;
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = client | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
        ForwardLimit = 1
    };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    foreach (var proxy in binding.TrustedProxies) forwarded.KnownProxies.Add(proxy);
    // A forwarded host still has to be one this Uncloud answers to.
    foreach (var host in binding.AllowedHosts) forwarded.AllowedHosts.Add(host);
    app.UseForwardedHeaders(forwarded);
}

app.Use(async (context, next) =>
{
    var request = context.Request;
    // Host validation also prevents DNS rebinding from turning a browser into a way in. An
    // unnamed tunnel comes back under a different address after a reconnect, so what it carries
    // now is asked as well as what was configured at startup.
    if (!binding.AllowedHosts.Contains(request.Host.Host) && !remote.Answers(request.Host.Host))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    // Dropbox returns the browser here by redirect, so this one path is necessarily cross-site.
    // It carries no authority of its own: the state parameter names the account and is checked
    // before the code is used.
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
            await Results.Problem("Open Uncloud and make this request from there.", statusCode: 403).ExecuteAsync(context);
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
                "unauthenticated" or "invalid_credentials" => 401,
                "forbidden" => 403,
                "too_many_attempts" => 429,
                "host_key" => 500,
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

// Who is asking. Everything under /api that isn't on the anonymous list stops here without a
// live session, and the administrative list stops here for anybody who isn't one.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/api")) { await next(context); return; }

    var token = context.Request.Cookies[SessionCookie];
    if (context.RequestServices.GetRequiredService<SessionStore>().Resolve(token) is { } resolved)
    {
        context.Items[CurrentUser.Key] = new CurrentUser(resolved.Account, token!);
        // The stored expiry moved, so the browser is handed the same token with the new one;
        // otherwise it would discard a session it has been using all along.
        if (resolved.RenewedUntil is { } until) IssueSession(context, new AuthSession(token!, until));
    }

    var open = anonymous.Any(allowed => path.StartsWithSegments(allowed));
    if (!open && context.Items[CurrentUser.Key] is null)
    {
        await Results.Problem("Sign in to Uncloud to continue.", statusCode: 401).ExecuteAsync(context);
        return;
    }
    if (!open && administrative.Any(prefix => path.StartsWithSegments(prefix))
        && context.Items[CurrentUser.Key] is CurrentUser { IsAdmin: false })
    {
        await Results.Problem("Only an administrator of this Uncloud can do that.", statusCode: 403).ExecuteAsync(context);
        return;
    }
    await next(context);
});

static void IssueSession(HttpContext context, AuthSession session) =>
    context.Response.Cookies.Append(SessionCookie, session.Token, new CookieOptions
    {
        HttpOnly = true,
        IsEssential = true,
        // Lax, not Strict: Dropbox returns the browser by a top-level cross-site redirect, and
        // under Strict the cookie would be withheld for it and for the hop back into the app,
        // so connecting an account would look like being signed out. The defence against
        // cross-site requests is the Origin and X-Homebase-Request checks above, not this.
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps,
        Path = "/",
        Expires = session.ExpiresAt
    });

static object Describe(UserAccount account) => new
{
    account.Id,
    account.Username,
    account.DisplayName,
    account.IsAdmin,
    account.CreatedAt,
    account.DisabledAt
};

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// The one call the app makes before anything else: whether this host has been set up at all,
// and who, if anybody, the browser is signed in as.
app.MapGet("/api/session", (HttpContext context, UserStore users, HostService host) =>
{
    var account = (context.Items[CurrentUser.Key] as CurrentUser)?.Account;
    return Results.Ok(new
    {
        setupNeeded = users.Count() == 0,
        hostConfigured = host.IsConfigured,
        user = account is null ? null : Describe(account)
    });
});

app.MapPost("/api/session", (SignIn request, HttpContext context, UserStore users, SessionStore sessions, LoginThrottle throttle) =>
{
    var address = context.Connection.RemoteIpAddress?.ToString();
    throttle.RequireAllowed(request.Username, address);
    if (users.Authenticate(request.Username, request.Password) is not { } account)
    {
        throttle.RecordFailure(request.Username, address);
        // The same refusal either way: a sign-in form must not say which accounts exist.
        throw new LibraryException("That username and password don’t match.", "invalid_credentials");
    }
    throttle.RecordSuccess(request.Username, address);
    IssueSession(context, sessions.Create(account.Id));
    return Results.Ok(new { user = Describe(account) });
});

app.MapDelete("/api/session", (HttpContext context, SessionStore sessions) =>
{
    sessions.Delete(context.Request.Cookies[SessionCookie]);
    context.Response.Cookies.Delete(SessionCookie, new CookieOptions
    {
        Path = "/", SameSite = SameSiteMode.Lax, Secure = context.Request.IsHttps
    });
    return Results.Ok(new { signedOut = true });
});

// The first account on a fresh host, which is an administrator because somebody has to be.
// Open only while there are none: afterwards an administrator adds the rest.
app.MapPost("/api/setup", (CreateUser request, HttpContext context, UserStore users, SessionStore sessions, RemoteAccess access) =>
{
    // Until this succeeds there is nobody to refuse anybody, so whoever finds the address first
    // becomes the administrator of the host. A tunnel puts that address on the internet, so this
    // one door stays shut to it: the rest of Uncloud is reachable through the tunnel as ever.
    if (access.Answers(context.Request.Host.Host))
        throw new LibraryException(
            "Set up this Uncloud on the computer it runs on, or from its own network. The first "
            + "account can’t be created over the internet, because until it exists there is "
            + "nobody here to say who may.", "forbidden");

    // Checked and inserted as one transaction: until it succeeds this endpoint is open to
    // anybody, so two people racing a fresh host must not both come away administrators.
    var account = users.CreateFirstAdmin(request.Username, request.DisplayName, request.Password ?? "");
    IssueSession(context, sessions.Create(account.Id));
    return Results.Ok(new { user = Describe(account) });
});

app.MapPost("/api/account/password", (ChangePassword request, CurrentUser user, UserStore users, SessionStore sessions) =>
{
    if (users.Authenticate(user.Account.Username, request.CurrentPassword) is null)
        throw new LibraryException("That isn’t your current password.", "invalid_credentials");
    users.SetPassword(user.Id, request.NewPassword ?? "");
    // A changed password is a decision that everywhere else should be signed out.
    sessions.DeleteAllFor(user.Id, except: user.Token);
    return Results.Ok(new { changed = true });
});

app.MapGet("/api/library", (CurrentUser user, UserWorkspaces workspaces, HostService host, IFolderPicker picker) =>
{
    var state = workspaces.For(user.Account).Library.State;
    return Results.Ok(new
    {
        state.RootPath,
        state.Name,
        hostRoot = user.IsAdmin ? host.RootPath : null,
        canPickFolder = user.IsAdmin && picker.IsSupported
    });
});

app.MapGet("/api/storage", (CurrentUser user, UserWorkspaces workspaces, HostService host, UsageService usage) =>
{
    // Free space is the volume's, and the volume is shared — that is the arrangement. What is
    // this account's alone is how much of it they are using.
    var report = Storage.For(host.RequireRoot());
    return Results.Ok(new
    {
        freeBytes = report?.FreeBytes,
        totalBytes = report?.TotalBytes,
        usedBytes = usage.UsedBytes(workspaces.For(user.Account).Root)
    });
});

app.MapGet("/api/files", async (string? path, CurrentUser user, UserWorkspaces workspaces, CancellationToken cancellationToken) =>
    Results.Ok(await workspaces.For(user.Account).Library.BrowseAsync(path, cancellationToken)));

app.MapGet("/api/files/download", async (string path, CurrentUser user, UserWorkspaces workspaces, CancellationToken cancellationToken) =>
{
    var (stream, name) = await workspaces.For(user.Account).Library.OpenFileAsync(path, cancellationToken);
    // Always download arbitrary user content; HTML/SVG must never run on the app's origin.
    return Results.File(stream, "application/octet-stream", name, enableRangeProcessing: true);
});

app.MapGet("/api/providers/dropbox", (CurrentUser user, UserWorkspaces workspaces) =>
{
    var dropbox = workspaces.For(user.Account).Dropbox;
    return Results.Ok(new { configured = dropbox.IsConfigured, connected = dropbox.IsConnected, accountName = dropbox.AccountName });
});

app.MapPost("/api/providers/dropbox/connect", (CurrentUser user, UserWorkspaces workspaces, DropboxAuthFlow flow) =>
    Results.Ok(new { authorizeUrl = flow.Begin(user.Id, workspaces.For(user.Account).Dropbox.AppKey, RedirectUri()) }));

app.MapPost("/api/providers/dropbox/disconnect", (CurrentUser user, UserWorkspaces workspaces, DropboxAuthFlow flow) =>
{
    workspaces.For(user.Account).Dropbox.Disconnect();
    flow.Forget(user.Id);
    return Results.Ok(new { connected = false });
});

// Dropbox sends the browser back here. Responses are redirects, not JSON, because a person is
// looking at them, and the account to connect comes from the state rather than from a cookie
// that a cross-site navigation may not carry.
app.MapGet("/api/providers/dropbox/callback", async (string? code, string? state, string? error, UserWorkspaces workspaces, DropboxAuthFlow flow, CancellationToken cancellationToken) =>
{
    if (error is not null || code is null) return Results.Redirect("/?dropbox=denied");
    try
    {
        var (userId, verifier) = flow.Consume(state);
        await workspaces.For(userId).Dropbox.ConnectAsync(code, verifier, RedirectUri(), cancellationToken);
        return Results.Redirect("/?dropbox=connected");
    }
    catch (Exception failure) when (failure is LibraryException or HttpRequestException)
    {
        app.Logger.LogWarning(failure, "Dropbox sign-in failed");
        return Results.Redirect("/?dropbox=failed");
    }
});

app.MapGet("/api/providers/dropbox/files", async (string? path, CurrentUser user, UserWorkspaces workspaces, CancellationToken cancellationToken) =>
    Results.Ok(await workspaces.For(user.Account).Dropbox.ListFolderAsync(path ?? "", cancellationToken)));

app.MapGet("/api/imports", (CurrentUser user, UserWorkspaces workspaces) =>
    Results.Ok(workspaces.For(user.Account).Imports.Imported()));
// Starting an import answers immediately; the work itself is watched through the job below.
app.MapPost("/api/imports", (ImportRequest request, CurrentUser user, UserWorkspaces workspaces) =>
    Results.Ok(new { job = workspaces.For(user.Account).Jobs.Start(request.RemotePath, request.Label) }));
app.MapGet("/api/imports/job", (CurrentUser user, UserWorkspaces workspaces) =>
    Results.Ok(new { job = workspaces.For(user.Account).Jobs.Current }));
app.MapPost("/api/imports/job/cancel", (CurrentUser user, UserWorkspaces workspaces) =>
    Results.Ok(new { job = workspaces.For(user.Account).Jobs.Cancel() }));
app.MapGet("/api/imports/estimate", async (string remotePath, CurrentUser user, UserWorkspaces workspaces, CancellationToken cancellationToken) =>
    Results.Ok(await workspaces.For(user.Account).Imports.MeasureAsync(remotePath, cancellationToken)));

// Where this host can be reached from outside the house, for the administrator to pass on.
app.MapGet("/api/remote-access", (RemoteAccess access) =>
{
    var state = access.State;
    return Results.Ok(new
    {
        state.Provider, state.Hostname, state.Url, state.Status, state.Detail, state.SignInUrl,
        // The same address Dropbox is told, so this never says one thing and does another.
        PublicUrl = PublicAddress()
    });
});

app.MapGet("/api/host", (HostService host, IFolderPicker picker) =>
    Results.Ok(new { rootPath = host.RootPath, canPickFolder = picker.IsSupported }));

app.MapPut("/api/host", (SelectRoot request, HostService host, UsageService usage) =>
{
    var root = host.SelectRoot(request.Path);
    usage.Invalidate(root);
    return Results.Ok(new { rootPath = root });
});

// What a v0 library left sitting at the top of the host's folder, belonging to no account.
app.MapGet("/api/host/unclaimed", (HostService host) =>
    Results.Ok(new { entries = UserPaths.Unclaimed(host.RequireRoot()) }));

app.MapPost("/api/host/unclaimed", (CurrentUser user, HostService host, UserWorkspaces workspaces, UsageService usage) =>
{
    var root = host.RequireRoot();
    var destination = workspaces.For(user.Account).Root;
    var moved = new List<string>();
    var skipped = new List<object>();
    foreach (var name in UserPaths.Unclaimed(root))
    {
        var from = Path.Combine(root, name);
        var to = Path.Combine(destination, name);
        if (File.Exists(to) || Directory.Exists(to))
        {
            // Uncloud never overwrites something it didn't write, here as anywhere else.
            skipped.Add(new { name, reason = "You already have something by that name." });
            continue;
        }
        try
        {
            // A rename on the same volume: the files never move and nothing is copied.
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
            moved.Add(name);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            app.Logger.LogWarning(failure, "Couldn’t move {Name} into an account folder", name);
            skipped.Add(new { name, reason = "Uncloud couldn’t move this one." });
        }
    }
    usage.Invalidate(destination);
    return Results.Ok(new { moved, skipped });
});

app.MapPost("/api/folder-picker", async (IFolderPicker picker, CancellationToken cancellationToken) =>
    Results.Ok(new { path = await picker.ChooseAsync(cancellationToken) }));

app.MapGet("/api/users", (UserStore users, HostService host, UsageService usage) =>
{
    var root = host.RootPath;
    // A walk of each account's folder, cached for a minute. Fine for a household; if a host ever
    // holds many accounts, this is the line to make lazy.
    long? Used(string id)
    {
        if (root is null) return null;
        try { return usage.UsedBytes(UserPaths.RootFor(root, id)); }
        catch (Exception error) when (error is LibraryException or IOException or UnauthorizedAccessException)
        {
            // A drive that isn't plugged in is no reason to be unable to manage accounts.
            return null;
        }
    }
    return Results.Ok(users.List().Select(account => new
    {
        account.Id,
        account.Username,
        account.DisplayName,
        account.IsAdmin,
        account.CreatedAt,
        account.DisabledAt,
        usedBytes = Used(account.Id)
    }));
});

app.MapPost("/api/users", (CreateUser request, UserStore users, UserWorkspaces workspaces, HostService host) =>
{
    var account = users.Create(request.Username, request.DisplayName, request.Password ?? "", request.IsAdmin);
    // Made now so a new account's first sign-in finds a folder rather than an error.
    if (host.IsConfigured) workspaces.For(account);
    return Results.Ok(Describe(account));
});

app.MapPatch("/api/users/{id}", (string id, UpdateUser request, CurrentUser actor, UserStore users, SessionStore sessions, UserWorkspaces workspaces, DropboxAuthFlow flow) =>
{
    if (users.Find(id) is null) throw new LibraryException("There’s no such account.", "not_found");
    if (request.DisplayName is not null) users.SetDisplayName(id, request.DisplayName);
    if (request.Password is not null)
    {
        users.SetPassword(id, request.Password);
        sessions.DeleteAllFor(id, except: id == actor.Id ? actor.Token : null);
    }
    if (request.IsAdmin is { } admin) users.SetAdmin(id, admin);
    if (request.Disabled is { } disabled)
    {
        users.SetDisabled(id, disabled);
        if (disabled)
        {
            // Turning an account off takes effect now, not whenever its sessions expire.
            sessions.DeleteAllFor(id);
            workspaces.Forget(id);
            flow.Forget(id);
        }
    }
    return Results.Ok(Describe(users.Find(id)!));
});

app.MapDelete("/api/users/{id}", (string id, UserStore users, SessionStore sessions, UserWorkspaces workspaces, DropboxAuthFlow flow, HostService host) =>
{
    if (users.Find(id) is null) throw new LibraryException("There’s no such account.", "not_found");
    users.Delete(id);
    sessions.DeleteAllFor(id);
    workspaces.Forget(id);
    flow.Forget(id);
    // Their files are not Uncloud's to throw away. Say where they are instead.
    var folder = host.RootPath is { } root
        ? Path.Combine(root, UserPaths.UsersDirectory, id)
        : null;
    return Results.Ok(new { deleted = true, filesRemainAt = folder });
});

app.Map("/api/{**path}", () => Results.Problem("This endpoint doesn’t exist.", statusCode: 404));
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Services.GetRequiredService<SessionStore>().PruneExpired();
try { app.Run(); }
finally
{
    // Whether the host stopped for a reason or never managed to listen at all, the tunnel
    // program is a child of this process and stopping it is nobody else's job.
    StopTunnel();
    remoteLog.Dispose();
}

public sealed record SelectRoot(string Path);
public sealed record SignIn(string? Username, string? Password);
public sealed record CreateUser(string? Username, string? DisplayName, string? Password, bool IsAdmin = false);
public sealed record UpdateUser(string? DisplayName, string? Password, bool? IsAdmin, bool? Disabled);
public sealed record ChangePassword(string? CurrentPassword, string? NewPassword);
public sealed record ImportRequest(string RemotePath, string? Label);
public partial class Program;
