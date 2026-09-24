using System.Net;
using Homebase.Core;
using Homebase.Core.Accounts;
using Homebase.Core.Providers;
using Homebase.Core.Sync;
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
// Uncloud's own Dropbox app, under everybody else's. Overridable so that somebody running their
// own build against their own app and relay isn't forced to edit source to do it.
builder.Services.AddSingleton(provider => new DropboxRelay(
    provider.GetRequiredService<IConfiguration>()["Homebase:Dropbox:RelayAppKey"],
    provider.GetRequiredService<IConfiguration>()["Homebase:Dropbox:RelayCallback"]));
builder.Services.AddSingleton(provider => new DropboxAppKey(
    provider.GetRequiredService<ControlDatabase>(),
    provider.GetRequiredService<IConfiguration>()["Homebase:Dropbox:AppKey"],
    provider.GetRequiredService<DropboxRelay>()));
builder.Services.AddSingleton<IDropboxApiFactory>(provider => new DropboxApiFactory(
    provider.GetRequiredService<HttpClient>(),
    provider.GetRequiredService<ConnectorStore>(),
    // A function, not a value: an administrator can set the app key while Uncloud is running.
    userId => provider.GetRequiredService<DropboxAppKey>().For(userId)));
builder.Services.AddSingleton(provider => new ImportPlaces(
    provider.GetRequiredService<ControlDatabase>(),
    provider.GetRequiredService<HostService>(),
    ConfigDirectory(provider, defaultConfig)));
builder.Services.AddSingleton<UserWorkspaces>();
builder.Services.AddSingleton<DropboxAuthFlow>();
builder.Services.AddSingleton(provider => new SyncthingHost(
    ConfigDirectory(provider, defaultConfig),
    provider.GetRequiredService<IConfiguration>(),
    provider.GetRequiredService<ILogger<SyncthingHost>>(),
    provider.GetRequiredService<HttpClient>()));
builder.Services.AddSingleton<ISyncthingEndpoint>(provider => provider.GetRequiredService<SyncthingHost>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<SyncthingHost>());
builder.Services.AddSingleton<ISyncthingApi, SyncthingApi>();
builder.Services.AddSingleton<SyncOwnership>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<PairingCodes>();
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
var relay = app.Services.GetRequiredService<DropboxRelay>();
var appKeys = app.Services.GetRequiredService<DropboxAppKey>();
// Where Dropbox sends an account's sign-in back to, which follows from whose Dropbox app it is
// signing in through. An account on Uncloud's own app goes by way of the relay — that is the one
// address registered with it — and is told to come home by the port on the end of its state.
// Anybody on a key somebody here chose comes straight back, as they always did.
// Why Uncloud's own Dropbox app can't finish a sign-in for the browser asking, or null when it
// can. It ends by handing the sign-in to this host at its plain loopback address, so every way
// that hop can fail to arrive is a reason not to start down it: better to say so than to send
// somebody somewhere that cannot work. Each of these leaves them their own app key, which comes
// straight back here and so has none of these problems.
string? RelayCannotFinish(HttpContext context)
{
    // A host serving its own https is reached by a name its certificate was issued for, and the
    // loopback address is not usually one of them — the browser would stop at a certificate
    // warning rather than arrive. A certificate that does cover it can't be told apart from here.
    if (binding.IsSecure)
        return "This Uncloud answers on its own secure address, which Uncloud's own Dropbox app "
             + "can't finish a sign-in on. Set up your own Dropbox app under My account — it takes "
             + "a couple of minutes and works either way.";
    // X-Forwarded-For is believed only from a tunnel Uncloud opened itself, so behind a proxy
    // somebody else configured the address below is the proxy's — often loopback, which would read
    // as "sitting at the machine" for somebody who is nowhere near it.
    if (binding.TrustedProxies.Count > 0 && !remote.IsEnabled)
        return "This Uncloud is behind a proxy, so it can't tell whether you're at the computer it "
             + "runs on — and Uncloud's own Dropbox app only works there. Set up your own Dropbox "
             + "app under My account — it takes a couple of minutes and works either way.";
    // A dual-stack socket reports a local browser as ::ffff:127.0.0.1, which has to be read as
    // loopback or everybody at the machine gets turned away.
    var from = context.Connection.RemoteIpAddress;
    if (from is { IsIPv4MappedToIPv6: true }) from = from.MapToIPv4();
    if (from is null || !IPAddress.IsLoopback(from))
        return "Uncloud's own Dropbox app can only finish a sign-in on the computer Uncloud is "
             + "running on. You're reaching it from somewhere else, so set up your own Dropbox app "
             + "under My account — it takes a couple of minutes and works from anywhere.";
    return null;
}
(string RedirectUri, Func<string, string>? WayBack) DropboxReturn(string userId) =>
    appKeys.UsesRelay(userId)
        ? (relay.Callback, state => DropboxRelay.StateWithWayBack(state, binding.IsSecure, binding.Port))
        : (RedirectUri(), (Func<string, string>?)null);
// Read-only, and the same three whoever's app is being set up.
string[] DropboxScopes = ["account_info.read", "files.metadata.read", "files.content.read"];
if (binding.Warning is { } warning) app.Logger.LogWarning("{Warning}", warning);
// A tunnel that drops is an outage of reaching this host from outside, never of the host, so
// this reopens in the background while everyone on the network carries on.
remote.Watch(binding.Port);
// Once Syncthing answers, bring its configuration in line with who owns what. Until then nothing
// syncs, so there is nothing to be out of line.
_ = app.Services.GetRequiredService<SyncthingHost>().Ready.ContinueWith(async _ =>
{
    try { await app.Services.GetRequiredService<SyncService>().ReconcileAsync(app.Lifetime.ApplicationStopping); }
    catch (Exception error) when (error is LibraryException or IOException or UnauthorizedAccessException or OperationCanceledException)
    {
        app.Logger.LogWarning(error, "Couldn’t bring Syncthing in line with Uncloud’s accounts");
    }
}, TaskScheduler.Default);

const string SessionCookie = "uncloud_session";
// Reached without a session. Everything else is refused until somebody signs in.
string[] anonymous =
[
    "/api/health",
    "/api/session",
    "/api/setup",
    "/api/providers/dropbox/callback",
    // The Uncloud app on somebody's computer has no session; the pairing code is its authority.
    "/api/sync/pair"
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
                    or "invalid_device" or "no_devices" or "sync_unavailable" or "sync_failed" => 409,
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
    return Results.Ok(new
    {
        configured = dropbox.IsConfigured,
        connected = dropbox.IsConnected,
        accountName = dropbox.AccountName,
        // Everybody can set up their own Dropbox app; nobody waits on an administrator for it.
        canConfigure = true
    });
});

app.MapPost("/api/providers/dropbox/connect", (HttpContext context, CurrentUser user, UserWorkspaces workspaces, DropboxAuthFlow flow) =>
{
    var (redirectUri, wayBack) = DropboxReturn(user.Id);
    // Refused rather than quietly sent back here instead: the key in force is Uncloud's own, and
    // Dropbox would turn away a sign-in to it from an address that app doesn't hold.
    if (wayBack is not null && RelayCannotFinish(context) is { } why)
        throw new LibraryException(why, "not_configured");
    return Results.Ok(new
    {
        authorizeUrl = flow.Begin(user.Id, workspaces.For(user.Account).Dropbox.AppKey, redirectUri,
            // Where to put them back afterwards. Dropbox returns the browser to the one registered
            // address, which may not be the one they are using.
            $"{context.Request.Scheme}://{context.Request.Host}", wayBack)
    });
});

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
    // Where the person pressed Connect, if that address is one this Uncloud answers to. Anything
    // else is ignored in favour of a relative hop, so the return address can never become a way to
    // send somebody off this host.
    string Back(string? returnTo, string outcome)
    {
        if (returnTo is not null && Uri.TryCreate(returnTo, UriKind.Absolute, out var origin)
            && origin.Scheme is "http" or "https" && binding.AllowedHosts.Contains(origin.Host))
            return $"{origin.Scheme}://{origin.Authority}/?dropbox={outcome}";
        return $"/?dropbox={outcome}";
    }

    // Consumed first, and whatever the outcome: a refusal ends this attempt as surely as a
    // success does, and the state is the only thing that says which address to go back to.
    string? returnTo = null;
    string? verifier = null;
    string? userId = null;
    string? startedUnder = null;
    string? startedWith = null;
    try
    {
        (userId, verifier, returnTo, startedUnder, startedWith) = flow.Consume(state);
    }
    catch (LibraryException expired)
    {
        app.Logger.LogWarning(expired, "A Dropbox sign-in came back with a state Uncloud didn’t recognise");
    }
    if (error is not null || code is null) return Results.Redirect(Back(returnTo, "denied"));
    if (verifier is null || userId is null || startedUnder is null || startedWith is null)
        return Results.Redirect(Back(returnTo, "failed"));
    try
    {
        await workspaces.For(userId).Dropbox
            .ConnectAsync(code, verifier, startedUnder, startedWith, cancellationToken);
        return Results.Redirect(Back(returnTo, "connected"));
    }
    catch (Exception failure) when (failure is LibraryException or HttpRequestException)
    {
        app.Logger.LogWarning(failure, "Dropbox sign-in failed");
        return Results.Redirect(Back(returnTo, "failed"));
    }
});

// Everywhere files can be brought in from, as this account sees them: the folders an
// administrator has shared, and this account's own Dropbox.
app.MapGet("/api/imports/sources", (CurrentUser user, UserWorkspaces workspaces, ImportPlaces places) =>
{
    var dropbox = workspaces.For(user.Account).Dropbox;
    return Results.Ok(new
    {
        places = places.List().Select(place => new
        {
            place.Id,
            place.Name,
            place.Path,
            // Said plainly rather than discovered by trying: an unplugged drive is a normal thing
            // for a household to have, not an error.
            available = Directory.Exists(place.Path)
        }),
        dropbox = new
        {
            configured = dropbox.IsConfigured,
            connected = dropbox.IsConnected,
            accountName = dropbox.AccountName
        }
    });
});

// Browsing one place. The id chooses among the places on the list; the path is relative to that
// place, so there is no way to name a folder outside one.
app.MapGet("/api/imports/sources/{sourceId}/files", async (string sourceId, string? path, CurrentUser user, UserWorkspaces workspaces, CancellationToken cancellationToken) =>
    Results.Ok(await workspaces.For(user.Account).Source(sourceId).ListFolderAsync(path ?? "", cancellationToken)));

app.MapGet("/api/imports", (CurrentUser user, UserWorkspaces workspaces) =>
    Results.Ok(workspaces.For(user.Account).Imports.Imported()));
// Starting an import answers immediately; the work itself is watched through the job below.
app.MapPost("/api/imports", (ImportRequest request, CurrentUser user, UserWorkspaces workspaces) =>
{
    var workspace = workspaces.For(user.Account);
    var sourceId = Sources.Name(request.Source);
    return Results.Ok(new
    {
        job = workspace.Jobs.Start(workspace.Source(sourceId), sourceId, request.RemotePath, request.Label)
    });
});
app.MapGet("/api/imports/job", (CurrentUser user, UserWorkspaces workspaces) =>
    Results.Ok(new { job = workspaces.For(user.Account).Jobs.Current }));
app.MapPost("/api/imports/job/cancel", (CurrentUser user, UserWorkspaces workspaces) =>
    Results.Ok(new { job = workspaces.For(user.Account).Jobs.Cancel() }));
app.MapGet("/api/imports/estimate", async (string remotePath, string? source, CurrentUser user, UserWorkspaces workspaces, CancellationToken cancellationToken) =>
{
    var workspace = workspaces.For(user.Account);
    return Results.Ok(await workspace.Imports.MeasureAsync(
        workspace.Source(Sources.Name(source)), remotePath, cancellationToken));
});

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

app.MapPut("/api/host", async (SelectRoot request, HostService host, UsageService usage, ImportPlaces places, SyncService sync, CancellationToken cancellationToken) =>
{
    // Refused here rather than left to break later: a folder everybody on this host may read from
    // cannot also be where everybody's files live, or bringing files in would read across accounts.
    if (places.Conflicting(request.Path) is { } clash)
        throw new LibraryException(
            $"“{clash.Name}” is somewhere everyone here brings files in from, and it holds this folder "
            + "(or sits inside it). Remove it under Where files come from, or choose a different folder.",
            "conflict");
    var root = host.SelectRoot(request.Path);
    usage.Invalidate(root);
    // Synced folders follow the host's folder. If Syncthing isn't up, this happens when it is;
    // if it refuses, the folder has still moved, and the next start tries again.
    try { await sync.ReconcileAsync(cancellationToken); }
    catch (LibraryException error) { app.Logger.LogWarning(error, "Couldn’t point synced folders at the new host folder"); }
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

// The folders everybody on this Uncloud may bring files in from. Administrative, because adding one
// shares it with every account here.
app.MapGet("/api/host/places", (ImportPlaces places, IFolderPicker picker) =>
    Results.Ok(new
    {
        places = places.List().Select(place => new
        {
            place.Id,
            place.Name,
            place.Path,
            place.AddedAt,
            place.DestinationPrefix,
            available = Directory.Exists(place.Path)
        }),
        suggestions = places.Suggestions(),
        canPickFolder = picker.IsSupported
    }));

app.MapPost("/api/host/places", (AddPlace request, ImportPlaces places) =>
    Results.Ok(places.Add(request.Path, request.Name)));

app.MapDelete("/api/host/places/{id}", (string id, ImportPlaces places, UserWorkspaces workspaces) =>
{
    var place = places.Find(id);
    places.Remove(id);
    // Taking the row away does not reach an import already running from it, which holds the folder
    // it started on. Removing a place has to actually stop the reading, not just the starting.
    var stopped = workspaces.CancelImportsFrom(place.ProviderId);
    return Results.Ok(new { removed = true, stopped });
});

// The Dropbox app this Uncloud offers everybody by default. Administrative because it is the
// host's, and because setting it saves every other account the trouble — but never a thing anyone
// has to wait for: each account can set its own below.
app.MapGet("/api/host/dropbox", (DropboxAppKey appKey) => Results.Ok(new
{
    appKey = appKey.HostStored,
    configured = appKey.Host is not null,
    fromEnvironment = appKey.HostFromEnvironment,
    // Setting one here is a convenience, not a requirement, when Uncloud brings its own.
    relayProvides = relay.Available,
    redirectUri = RedirectUri(),
    scopes = DropboxScopes
}));

app.MapPut("/api/host/dropbox", (SetDropboxAppKey request, DropboxAppKey appKey, ConnectorStore connectors, UserWorkspaces workspaces) =>
{
    if (!appKey.SetHost(request.AppKey))
        return Results.Ok(new { configured = appKey.Host is not null, disconnected = 0 });
    // Every connection made through the app that key named can no longer be refreshed. Clearing
    // them makes the panel say "connect" instead of failing later. An account connecting through
    // its own key is unaffected, so only the ones that fell back to the host's are cleared.
    var disconnected = connectors.ClearAll(DropboxApi.ProviderName, appKey.UsesHostKey);
    // Deleting the stored tokens is only half of it: a client already in use holds a live access
    // token of its own. Dropping the workspaces stops those, and whatever they were importing.
    workspaces.ForgetAll(appKey.UsesHostKey);
    return Results.Ok(new { configured = appKey.Host is not null, disconnected });
});

// One account's own Dropbox app. Anybody signed in can set this for themselves: an app key is not
// a secret, it only ever authorises that account's own Dropbox, and the alternative is everybody
// waiting on whoever looks after the host.
app.MapGet("/api/account/dropbox", (HttpContext context, CurrentUser user, DropboxAppKey appKey) => Results.Ok(new
{
    appKey = appKey.OwnedBy(user.Id),
    configured = appKey.For(user.Id) is not null,
    source = appKey.SourceFor(user.Id).ToString(),
    // What leaving the box empty would fall back to. Two separate facts because they read
    // differently on screen: an administrator here chose that key, and nobody chose the relay.
    hostProvides = appKey.Host is not null,
    relayProvides = relay.Available,
    // Whether Uncloud's own app could actually finish a sign-in for the browser asking. Somebody
    // reading this from another computer needs a key of their own, and telling them there is
    // nothing to set up — right after refusing them — would be worse than saying nothing.
    relayReachable = RelayCannotFinish(context) is null,
    redirectUri = RedirectUri(),
    scopes = DropboxScopes
}));

app.MapPut("/api/account/dropbox", (SetDropboxAppKey request, CurrentUser user, DropboxAppKey appKey, ConnectorStore connectors, UserWorkspaces workspaces) =>
{
    var changed = appKey.SetOwn(user.Id, request.AppKey);
    var disconnected = false;
    if (changed)
    {
        // Their own connection only. Nobody else's key moved, so nobody else is signed out.
        disconnected = connectors.LoadSecret(user.Id, DropboxApi.ProviderName) is not null;
        connectors.Clear(user.Id, DropboxApi.ProviderName);
        workspaces.ForgetDropbox(user.Id);
    }
    return Results.Ok(new
    {
        configured = appKey.For(user.Id) is not null,
        source = appKey.SourceFor(user.Id).ToString(),
        disconnected
    });
});

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

app.MapPatch("/api/users/{id}", async (string id, UpdateUser request, CurrentUser actor, UserStore users, SessionStore sessions, UserWorkspaces workspaces, DropboxAuthFlow flow, SyncService sync, CancellationToken cancellationToken) =>
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
        // A disabled account's computers stop syncing too, and start again when it is enabled.
        // The account has changed either way; a Syncthing that refuses is caught up on next start.
        try { await sync.SuspendAsync(id, disabled, cancellationToken); }
        catch (LibraryException error) { app.Logger.LogWarning(error, "Couldn’t pause or resume {Id}'s computers", id); }
    }
    return Results.Ok(Describe(users.Find(id)!));
});

app.MapDelete("/api/users/{id}", async (string id, UserStore users, SessionStore sessions, UserWorkspaces workspaces, DropboxAuthFlow flow, HostService host, DropboxAppKey appKey, SyncService sync, CancellationToken cancellationToken) =>
{
    if (users.Find(id) is null) throw new LibraryException("There’s no such account.", "not_found");
    // Nothing may carry on syncing into a deleted account's folder.
    await sync.ForgetAsync(id, () => users.Delete(id), cancellationToken);
    sessions.DeleteAllFor(id);
    workspaces.Forget(id);
    flow.Forget(id);
    // Their row went with the account; this drops what was held for it in memory.
    appKey.Forget(id);
    // Their files are not Uncloud's to throw away. Say where they are instead.
    var folder = host.RootPath is { } root
        ? Path.Combine(root, UserPaths.UsersDirectory, id)
        : null;
    return Results.Ok(new { deleted = true, filesRemainAt = folder });
});

// Each account's own computers, and the folders it keeps the same on them. Nothing here takes a
// root or an owner from the request: both come from the session.
app.MapGet("/api/sync", async (CurrentUser user, SyncService sync, CancellationToken cancellationToken) =>
    Results.Ok(await sync.StatusAsync(user.Id, cancellationToken)));
app.MapPost("/api/sync/devices", async (PairDevice request, CurrentUser user, SyncService sync, CancellationToken cancellationToken) =>
{
    await sync.PairAsync(user.Id, request.DeviceId, request.Name, cancellationToken);
    return Results.Ok(await sync.StatusAsync(user.Id, cancellationToken));
});
app.MapDelete("/api/sync/devices/{deviceId}", async (string deviceId, CurrentUser user, SyncService sync, CancellationToken cancellationToken) =>
{
    await sync.UnpairAsync(user.Id, deviceId, cancellationToken);
    return Results.Ok(await sync.StatusAsync(user.Id, cancellationToken));
});
// A code for the Uncloud app on this person's computer, and the app redeeming it.
app.MapPost("/api/sync/pairing-codes", (CurrentUser user, PairingCodes codes) => Results.Ok(codes.Issue(user.Id)));
app.MapPost("/api/sync/pair", async (RedeemPairing request, HttpContext context, PairingCodes codes, UserWorkspaces workspaces, CancellationToken cancellationToken) =>
    Results.Ok(await codes.RedeemAsync(request.Code, request.DeviceId, request.Name,
        context.Connection.RemoteIpAddress?.ToString(),
        request.SyncEverything ? userId => workspaces.For(userId).Root : null, cancellationToken)));
app.MapPost("/api/sync/folders", async (SyncFolderRequest request, CurrentUser user, UserWorkspaces workspaces, SyncService sync, CancellationToken cancellationToken) =>
    Results.Ok(await sync.ShareAsync(user.Id, workspaces.For(user.Account).Root, request.Path, request.DeviceIds, cancellationToken)));
app.MapPost("/api/sync/folders/accept", async (AcceptFolder request, CurrentUser user, UserWorkspaces workspaces, SyncService sync, CancellationToken cancellationToken) =>
    Results.Ok(await sync.AcceptAsync(user.Id, workspaces.For(user.Account).Root, request.FolderId, request.Path, cancellationToken)));
app.MapDelete("/api/sync/folders/{folderId}", async (string folderId, CurrentUser user, SyncService sync, CancellationToken cancellationToken) =>
{
    await sync.StopAsync(user.Id, folderId, cancellationToken);
    return Results.Ok(await sync.StatusAsync(user.Id, cancellationToken));
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
/// <summary>
/// Somewhere to bring files in from, as a request names it: a place's id, or "dropbox". Absent
/// means Dropbox, which is what the only source used to be.
/// </summary>
public static class Sources
{
    public static string Name(string? source) =>
        string.IsNullOrWhiteSpace(source) ? Homebase.Core.Providers.DropboxApi.ProviderName : source.Trim();
}

public sealed record ImportRequest(string RemotePath, string? Label, string? Source);
public sealed record AddPlace(string? Path, string? Name);
public sealed record SetDropboxAppKey(string? AppKey);
public sealed record PairDevice(string DeviceId, string? Name);
public sealed record SyncFolderRequest(string? Path, IReadOnlyList<string>? DeviceIds);
public sealed record AcceptFolder(string FolderId, string? Path);
/// <param name="SyncEverything">The Uncloud app asks for this: bring the computer in on every folder the account syncs.</param>
public sealed record RedeemPairing(string? Code, string? DeviceId, string? Name, bool SyncEverything = false);
public partial class Program;
