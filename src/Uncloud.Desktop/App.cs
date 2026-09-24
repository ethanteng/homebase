using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Uncloud.Desktop;

/// <summary>
/// The menu-bar app. Everything it does goes through <see cref="DesktopController"/>; this only
/// keeps the menu honest about how things stand and opens the setup window when there's no
/// setup yet.
/// </summary>
public sealed class App : Application
{
    internal static DesktopPaths Paths { get; set; } = DesktopPaths.ForThisUser();
    internal static string ServerDirectory { get; set; } = AppContext.BaseDirectory;
    internal static string? StartupLink { get; set; }

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    // One menu for the app's whole life, whose items change. On macOS the menu-bar item is bound
    // to the first menu it's given, and handing it another one throws — which, at startup, is a
    // crash before anything appears.
    private readonly NativeMenu _menu = new();
    private DesktopController _controller = null!;
    private ILoggerFactory _loggers = null!;
    private ILogger _logger = null!;
    private TrayIcon _tray = null!;
    private string? _shownMenu;
    private SetupWindow? _setup;
    private string _status = "Starting…";
    private bool _paused;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;
        Name = "Uncloud";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _loggers = LoggerFactory.Create(builder => builder.AddProvider(new FileLoggerProvider(Paths.Log)));
        _logger = _loggers.CreateLogger("Uncloud.App");
        // A menu-bar app that quits over one failed click leaves people with nothing to click. What
        // went wrong goes in the log, and the app carries on.
        Dispatcher.UIThread.UnhandledException += (_, unhandled) =>
        {
            _logger.LogCritical(unhandled.Exception, "Unhandled error");
            unhandled.Handled = true;
        };
        _controller = new DesktopController(Paths, ServerDirectory, _loggers, _http);

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Uncloud/Assets/tray.png"))),
            ToolTipText = "Uncloud",
            IsVisible = true,
            Menu = _menu
        };
        // Black on transparent, so macOS tints it to suit a light or dark menu bar.
        MacOSProperties.SetIsTemplateIcon(_tray, true);
        TrayIcon.SetIcons(this, [_tray]);

        // A pairing link clicked in Uncloud's web page opens here, already filled in.
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            activatable.Activated += (_, activation) =>
            {
                if (activation is ProtocolActivatedEventArgs { Kind: ActivationKind.OpenUri } opened)
                    Dispatcher.UIThread.Post(() => ShowSetup(opened.Uri.ToString()));
            };

        RebuildMenu();
        // Once the app is running, so anything that goes wrong starting reaches the log above.
        Dispatcher.UIThread.Post(async () => await StartAsync());
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync()
    {
        try
        {
            await _controller.StartAsync(CancellationToken.None);
            if (_controller.Settings.Mode is DesktopMode.Host && _controller.Server is { } server)
                _status = "Uncloud is running";
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Uncloud couldn’t start");
            _status = error.Message;
        }
        if (_controller.Settings.Mode is DesktopMode.Unset || StartupLink is not null) ShowSetup(StartupLink);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            switch (_controller.Settings.Mode)
            {
                case DesktopMode.Computer:
                    var status = await _controller.StatusAsync(CancellationToken.None);
                    _status = status.Summary;
                    _paused = status.Summary == "Paused";
                    break;
                case DesktopMode.Host:
                    _status = _controller.Server is { IsRunning: true }
                        ? "Uncloud is running"
                        : $"Uncloud has stopped. {_controller.Server?.LastError}".Trim();
                    break;
                default:
                    _status = "Not set up yet";
                    break;
            }
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Couldn’t check how syncing is going");
            _status = error.Message;
        }
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        var menu = new List<NativeMenuItemBase>();
        menu.Add(new NativeMenuItem(_status) { IsEnabled = false });
        menu.Add(new NativeMenuItemSeparator());
        var settings = _controller.Settings;
        switch (settings.Mode)
        {
            case DesktopMode.Computer:
                if (settings.AccountName is { } account)
                    menu.Add(new NativeMenuItem($"Signed in as {account}") { IsEnabled = false });
                menu.Add(Item("Open Uncloud Folder", () => Open(Paths.Files)));
                // Handlers read how things stand when clicked: an item that didn't change stays in
                // the menu, however long ago it was made.
                if (settings.Address is not null)
                    menu.Add(Item("Open Uncloud in Browser", () => Open(_controller.Settings.Address ?? "")));
                menu.Add(new NativeMenuItemSeparator());
                menu.Add(Item(_paused ? "Resume Syncing" : "Pause Syncing",
                    () => Guard(() => _controller.PauseAsync(!_paused, CancellationToken.None))));
                menu.Add(Item("Disconnect This Computer…", () => ShowSetup(null, disconnect: true)));
                break;
            case DesktopMode.Host:
                menu.Add(Item("Open Uncloud", () => Open(_controller.Server?.Address.ToString() ?? $"http://127.0.0.1:{HostServer.Port}")));
                var reach = new NativeMenuItem("Reach From Anywhere")
                {
                    ToggleType = NativeMenuItemToggleType.CheckBox,
                    IsChecked = _controller.ReachFromAnywhere
                };
                reach.Click += async (_, _) => await Guard(() =>
                    _controller.SetReachFromAnywhereAsync(!_controller.ReachFromAnywhere, CancellationToken.None));
                menu.Add(reach);
                if (_controller.Server is not { IsRunning: true })
                    menu.Add(Item("Start Uncloud Again", () => Guard(() => _controller.RestartServerAsync(CancellationToken.None))));
                break;
            default:
                menu.Add(Item("Set Up Uncloud…", () => ShowSetup(null)));
                break;
        }
        menu.Add(new NativeMenuItemSeparator());
        var login = LoginItem.ForThisUser(Environment.ProcessPath ?? "Uncloud");
        if (OperatingSystem.IsMacOS())
        {
            var atLogin = new NativeMenuItem("Open at Login") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = login.IsEnabled };
            atLogin.Click += (_, _) => { login.Set(!login.IsEnabled); RebuildMenu(); };
            menu.Add(atLogin);
        }
        menu.Add(Item("Quit Uncloud", Quit));

        // Only when something changed: the status is checked every few seconds, and replacing the
        // items each time would redraw a menu somebody might have open.
        var shown = string.Join('\n', menu.Select(item => item is NativeMenuItem { Header: var header } entry
            ? $"{header}|{entry.IsChecked}|{entry.IsEnabled}"
            : "—"));
        if (shown == _shownMenu) return;
        _shownMenu = shown;
        _menu.Items.Clear();
        foreach (var item in menu) _menu.Items.Add(item);
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error)
        {
            _logger.LogWarning(error, "A menu action failed");
            _status = error.Message;
        }
        await RefreshAsync();
    }

    private void ShowSetup(string? link, bool disconnect = false)
    {
        _setup?.Close();
        _setup = new SetupWindow(_controller, link, disconnect);
        _setup.Closed += async (_, _) =>
        {
            _setup = null;
            await RefreshAsync();
        };
        _setup.Show();
        _setup.Activate();
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private static NativeMenuItem Item(string header, Func<Task> action)
    {
        var item = new NativeMenuItem(header);
        item.Click += async (_, _) => await action();
        return item;
    }

    internal static void Open(string target)
    {
        try
        {
            if (Directory.Exists(target) || target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private async void Quit()
    {
        _tray.IsVisible = false;
        try { await _controller.DisposeAsync(); }
        catch (Exception error) { _logger.LogWarning(error, "Couldn’t stop everything cleanly"); }
        _loggers.Dispose();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
