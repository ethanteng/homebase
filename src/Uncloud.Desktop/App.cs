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
    private DesktopController _controller = null!;
    private ILoggerFactory _loggers = null!;
    private TrayIcon _tray = null!;
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
        _loggers = LoggerFactory.Create(builder => builder.AddProvider(new FileLoggerProvider(Path.Combine(Paths.AppData, "uncloud.log"))));
        _controller = new DesktopController(Paths, ServerDirectory, _loggers, _http);

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Uncloud/Assets/tray.png"))),
            ToolTipText = "Uncloud",
            IsVisible = true,
            Menu = new NativeMenu()
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
        _ = StartAsync();
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
            _status = error.Message;
        }
        if (_controller.Settings.Mode is DesktopMode.Unset || StartupLink is not null) ShowSetup(StartupLink);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
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
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        var menu = new NativeMenu();
        menu.Add(new NativeMenuItem(_status) { IsEnabled = false });
        menu.Add(new NativeMenuItemSeparator());
        var settings = _controller.Settings;
        switch (settings.Mode)
        {
            case DesktopMode.Computer:
                if (settings.AccountName is { } account)
                    menu.Add(new NativeMenuItem($"Signed in as {account}") { IsEnabled = false });
                menu.Add(Item("Open Uncloud Folder", () => Open(Paths.Files)));
                if (settings.Address is { } address) menu.Add(Item("Open Uncloud in Browser", () => Open(address)));
                menu.Add(new NativeMenuItemSeparator());
                menu.Add(Item(_paused ? "Resume Syncing" : "Pause Syncing", async () =>
                {
                    await _controller.PauseAsync(!_paused, CancellationToken.None);
                    await RefreshAsync();
                }));
                menu.Add(Item("Disconnect This Computer…", () => ShowSetup(null, disconnect: true)));
                break;
            case DesktopMode.Host:
                menu.Add(Item("Open Uncloud", () => Open(_controller.Server?.Address.ToString() ?? $"http://127.0.0.1:{HostServer.Port}")));
                var reach = new NativeMenuItem("Reach From Anywhere")
                {
                    ToggleType = NativeMenuItemToggleType.CheckBox,
                    IsChecked = settings.ReachFromAnywhere
                };
                reach.Click += async (_, _) => await Guard(() => _controller.SetReachFromAnywhereAsync(!settings.ReachFromAnywhere, CancellationToken.None));
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
        _tray.Menu = menu;
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { _status = error.Message; }
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
        await _controller.DisposeAsync();
        _loggers.Dispose();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
