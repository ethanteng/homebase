using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Homebase.Core.Sync;
using QRCoder;

namespace Uncloud.Desktop;

/// <summary>What the setup window opens on, when it isn't the first question.</summary>
public enum SetupStart
{
    /// <summary>Whatever comes next: the first question, or finding Uncloud for a computer.</summary>
    Next,
    Disconnect,
    /// <summary>Moving a host that runs as the person signed in here into an account of its own.</summary>
    KeepPrivate,
    UseAnotherFolder,
    TurnOff
}

/// <summary>
/// The one window the app has. First run asks what this computer is. A computer then finds the
/// Uncloud on its network and shows a QR code for its owner to scan and approve on their phone —
/// or pairs with a link or an address and code, as it always could. The host sets Uncloud up in an
/// account of its own and opens it in the browser, where the first account is made as it always
/// has been.
/// </summary>
public sealed class SetupWindow : Window
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#1f6fe5"));
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    private readonly DesktopController _controller;
    private readonly StackPanel _body = new() { Spacing = 14, Margin = new Thickness(28) };
    private readonly TextBlock _problem = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    // Whatever the window is doing in the background — looking for Uncloud, or waiting for somebody
    // to approve — stopped when it moves on to something else or is closed.
    private CancellationTokenSource? _work;

    public SetupWindow(DesktopController controller, string? link, SetupStart start = SetupStart.Next)
    {
        _controller = controller;
        Title = "Uncloud";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _body;
        Closed += (_, _) => _work?.Cancel();

        if (start is SetupStart.Disconnect) ShowDisconnect();
        else if (start is SetupStart.KeepPrivate) ShowKeepPrivate();
        else if (start is SetupStart.UseAnotherFolder) ShowAnotherFolder();
        else if (start is SetupStart.TurnOff) ShowTurnOff();
        else if (link is not null) ShowCode(link);
        else if (controller.Settings.Mode is DesktopMode.Computer) ShowFind();
        else ShowChoice();
    }

    private void Reset(string heading, string explanation)
    {
        _work?.Cancel();
        _work = null;
        _body.Children.Clear();
        _problem.IsVisible = false;
        _body.Children.Add(new TextBlock { Text = heading, FontSize = 22, FontWeight = FontWeight.SemiBold });
        _body.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
    }

    private CancellationToken Begin()
    {
        _work = new CancellationTokenSource();
        return _work.Token;
    }

    private void ShowChoice()
    {
        Reset("Set up Uncloud", "Is this the computer everybody’s files live on, or one of your own computers?");
        _body.Children.Add(Choice("Connect this computer to an Uncloud",
            "Your files appear in an Uncloud folder here, and stay in step both ways.", ShowFind));
        _body.Children.Add(Choice("Make this Mac the Uncloud host",
            "Everyone’s files live here. Keep it on and awake while people use it.", () => ShowHost()));
    }

    private static Button Choice(string title, string detail, Action choose)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 12),
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 }
                }
            }
        };
        button.Click += (_, _) => choose();
        return button;
    }

    /// <summary>Looks for the household's Uncloud on this network, so nobody has to know its address.</summary>
    private async void ShowFind()
    {
        Reset("Connect this computer", "Looking for Uncloud on this network…");
        var stop = Begin();
        _body.Children.Add(new ProgressBar { IsIndeterminate = true });
        _body.Children.Add(Links(("Enter its address instead", () => ShowAddress(null)), ("Use a pairing code", () => ShowCode(null))));

        IReadOnlyList<FoundHost> found;
        try { found = await _controller.FindAsync(stop); }
        catch (OperationCanceledException) { return; }
        if (stop.IsCancellationRequested) return;

        var reachable = found.Where(host => Reachable(host) is not null).ToList();
        if (reachable.Count == 1) ShowScan(Reachable(reachable[0])!, reachable[0].Name);
        else if (reachable.Count > 1) ShowPick(reachable);
        // Found but not reachable from here is worth saying: what to do about it is on the host.
        else ShowAddress(found.FirstOrDefault());
    }

    private static Uri? Reachable(FoundHost host)
    {
        try { return host.Url is null ? null : PairingLink.ParseAddress(host.Url); }
        catch (FormatException) { return null; }
    }

    private void ShowPick(IReadOnlyList<FoundHost> hosts)
    {
        Reset("Connect this computer", "There’s more than one Uncloud on this network. Which is yours?");
        foreach (var host in hosts)
        {
            var address = Reachable(host)!;
            _body.Children.Add(Choice(host.Name, address.Host, () => ShowScan(address, host.Name)));
        }
        _body.Children.Add(Links(("Use a pairing code instead", () => ShowCode(null))));
    }

    /// <param name="unreachable">An Uncloud that was found but can't be reached from other computers yet.</param>
    private void ShowAddress(FoundHost? unreachable)
    {
        Reset("Connect this computer", unreachable is { } host
            ? $"Found Uncloud on {host.Name}, but other computers can’t reach it yet. On {host.Name}, open the Uncloud menu and tick Reach From Anywhere, then look again."
            : "Couldn’t find Uncloud on this network. On your home Wi-Fi, look again — or enter the address you open Uncloud at.");

        var address = new TextBox { Watermark = "Address, such as https://home.example.ts.net", Text = _controller.Settings.Address };
        var next = new Button { Content = "Next", Background = Accent, Foreground = Brushes.White };
        next.Click += (_, _) =>
        {
            var text = address.Text?.Trim() ?? "";
            // The link from My computers, pasted whole, works here too.
            if (text.StartsWith(PairingLink.Scheme + "://", StringComparison.OrdinalIgnoreCase))
            {
                ShowCode(text);
                return;
            }
            try { ShowScan(PairingLink.ParseAddress(text), null); }
            catch (FormatException error) { Problem(error.Message); }
        };
        var again = new Button { Content = "Look Again" };
        again.Click += (_, _) => ShowFind();

        _body.Children.Add(Labelled("Uncloud address", address));
        _body.Children.Add(_problem);
        _body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { again, next }
        });
        _body.Children.Add(Links(("Use a pairing code instead", () => ShowCode(null))));
    }

    /// <summary>
    /// Shows a QR code for the person to scan with their phone, and waits for them to approve it.
    /// A code that runs out, or that a restarted host has forgotten, is replaced by a new one.
    /// </summary>
    private async void ShowScan(Uri address, string? hostName)
    {
        Reset("Connect this computer", hostName is null ? $"Uncloud at {address.Host}." : $"Found Uncloud on {hostName}.");
        var stop = Begin();
        var code = new Image { Width = 220, Height = 220, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(code, BitmapInterpolationMode.None);
        var instructions = new TextBlock { Text = "Getting a code…", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        var browser = new Button { Content = "Approve in This Mac’s Browser", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Center };
        PairingRequest? request = null;
        browser.Click += (_, _) => { if (request is not null) App.Open(request.ApproveUrl.ToString()); };
        _body.Children.Add(code);
        _body.Children.Add(instructions);
        _body.Children.Add(browser);
        _body.Children.Add(_problem);
        _body.Children.Add(Links(("Use a pairing code instead", () => ShowCode(null))));

        var name = DesktopController.ComputerName();
        try
        {
            while (true)
            {
                request = await _controller.RequestPairingAsync(address, name, stop);
                code.Source = QrCode(request.ApproveUrl);
                instructions.Text = $"Scan this with your phone’s camera, then tap Add this computer. It’s called {name}. Sign in to Uncloud first if your phone asks.";
                browser.IsEnabled = true;
                _problem.IsVisible = false;

                while (true)
                {
                    await Task.Delay(Poll, stop);
                    PairingResult? result;
                    try { result = await _controller.CollectPairingAsync(request, stop); }
                    catch (PairingExpiredException) { break; }
                    // Not reachable for a moment — the Wi-Fi changing, the host restarting — is
                    // said, and tried again, rather than the end of setting up.
                    catch (PairingException error) when (!stop.IsCancellationRequested)
                    {
                        Problem(error.Message);
                        continue;
                    }
                    if (result is not null)
                    {
                        ShowPaired(result.AccountName);
                        return;
                    }
                    _problem.IsVisible = false;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is PairingException or InvalidOperationException or Homebase.Core.LibraryException or IOException or InvalidDataException)
        {
            if (stop.IsCancellationRequested) return;
            Problem(error.Message);
            code.Source = null;
            browser.IsEnabled = false;
            instructions.Text = "";
            var again = new Button { Content = "Try Again", HorizontalAlignment = HorizontalAlignment.Right };
            again.Click += (_, _) => ShowScan(address, hostName);
            _body.Children.Insert(_body.Children.IndexOf(_problem) + 1, again);
        }
    }

    /// <summary>Dark on white whatever the window's theme, with the quiet border scanners need.</summary>
    private static Bitmap QrCode(Uri url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url.ToString(), QRCodeGenerator.ECCLevel.M);
        return new Bitmap(new MemoryStream(new PngByteQRCode(data).GetGraphic(10)));
    }

    private void ShowCode(string? link)
    {
        Reset("Connect with a pairing code",
            "In Uncloud, open My computers and choose Get a pairing code. Click the link it shows, or copy the address and code here.");

        var address = new TextBox { Watermark = "Address, such as https://home.example.ts.net" };
        var code = new TextBox { Watermark = "Pairing code, such as AB12C-DE34F" };
        var name = new TextBox { Text = DesktopController.ComputerName(), Watermark = "What to call this computer" };
        if (link is not null)
        {
            try
            {
                var parsed = PairingLink.Parse(link);
                address.Text = parsed.Address.ToString().TrimEnd('/');
                code.Text = parsed.Code;
            }
            catch (FormatException error) { Problem(error.Message); }
        }
        else if (_controller.Settings.Address is { } known) address.Text = known;

        var connect = new Button { Content = "Connect", Background = Accent, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
        connect.Click += async (_, _) =>
        {
            connect.IsEnabled = false;
            connect.Content = "Connecting…";
            try
            {
                // The link pasted whole into the address box works too.
                var pairing = address.Text?.Trim().StartsWith(PairingLink.Scheme + "://", StringComparison.OrdinalIgnoreCase) == true
                    ? PairingLink.Parse(address.Text)
                    : PairingLink.From(address.Text, code.Text);
                var result = await _controller.PairAsync(pairing, string.IsNullOrWhiteSpace(name.Text) ? Environment.MachineName : name.Text.Trim(), CancellationToken.None);
                ShowPaired(result.AccountName);
            }
            catch (Exception error) when (error is FormatException or PairingException or InvalidOperationException or Homebase.Core.LibraryException or IOException)
            {
                Problem(error.Message);
                connect.IsEnabled = true;
                connect.Content = "Connect";
            }
        };

        _body.Children.Add(Labelled("Uncloud address", address));
        _body.Children.Add(Labelled("Pairing code", code));
        _body.Children.Add(Labelled("This computer’s name", name));
        _body.Children.Add(_problem);
        _body.Children.Add(connect);
        _body.Children.Add(Links(("Scan a code with your phone instead", ShowFind)));
    }

    private void ShowPaired(string account)
    {
        Reset("You’re connected",
            $"Files for {account} are arriving in {_controller.Paths.Files}. Anything you add, change or delete there reaches Uncloud, and the other way round. Uncloud keeps anything deleted for 30 days.");
        var open = new Button { Content = "Open Uncloud Folder", Background = Accent, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
        open.Click += (_, _) => { App.Open(_controller.Paths.Files); Close(); };
        _body.Children.Add(open);
    }

    private void ShowHost(string? root = null)
    {
        if (!_controller.CanSetUpPrivately)
        {
            Reset("Make this Mac the host",
                "Uncloud will run here from the menu bar, with everything it needs built in. Next, your browser opens to make the first account and choose where everyone’s files are kept.");
        }
        else
        {
            Reset("Make this Mac the host",
                "Uncloud runs here in the background from now on, from the moment the Mac starts, in an account of its own that nobody signs in to. Everyone’s files belong to that account, so nobody using this Mac can look through them, in Finder or in Terminal. macOS asks for an administrator’s password once, to set it up.");
            if (_controller.AsksWhereFilesGo)
            {
                var where = new TextBlock
                {
                    Text = root is null ? "Everyone’s files will be kept on this Mac." : $"Everyone’s files will be kept in {root}.",
                    TextWrapping = TextWrapping.Wrap
                };
                _body.Children.Add(where);
                _body.Children.Add(root is null
                    ? Links(("Keep them on a drive or another folder instead", async () =>
                    {
                        if (await ChooseFolderAsync() is { } chosen) ShowHost(chosen);
                    }))
                    : Links(("Keep them on this Mac instead", () => ShowHost())));
            }
        }
        var start = new Button { Content = "Start Uncloud Here", Background = Accent, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
        start.Click += async (_, _) =>
        {
            start.IsEnabled = false;
            start.Content = "Starting…";
            try
            {
                await _controller.BecomeHostAsync(root, CancellationToken.None);
                OpenAtLogin();
                App.Open(DesktopController.HostAddress.ToString());
                Close();
            }
            catch (OperationCanceledException)
            {
                start.IsEnabled = true;
                start.Content = "Start Uncloud Here";
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                Problem(error.Message);
                start.IsEnabled = true;
                start.Content = "Start Uncloud Here";
            }
        };
        _body.Children.Add(_problem);
        _body.Children.Add(start);
        if (_controller.CannotSetUpPrivately is { } why)
        {
            Problem(why);
            start.IsEnabled = false;
        }
    }

    /// <summary>
    /// For a host set up before Uncloud ran in an account of its own: everything moves across as it
    /// is, and from then on nobody signed in here can look through anybody's files.
    /// </summary>
    private void ShowKeepPrivate()
    {
        Reset("Keep everyone’s files private",
            "Uncloud on this Mac runs as you, so anyone signed in here can open everybody’s files in Finder or Terminal. It can run in an account of its own instead, which nobody signs in to. Then nobody using this Mac can look through them, and Uncloud starts with the Mac, before anyone signs in.");
        _body.Children.Add(new TextBlock
        {
            Text = "Accounts, files and synced computers all carry on as they are. If everyone’s files are in your home folder, they move out of it, to a folder only Uncloud can open. macOS asks for an administrator’s password.",
            TextWrapping = TextWrapping.Wrap
        });
        var later = new Button { Content = "Not Now" };
        later.Click += (_, _) => Close();
        var go = new Button { Content = "Continue", Background = Accent, Foreground = Brushes.White };
        go.Click += async (_, _) =>
        {
            go.IsEnabled = later.IsEnabled = false;
            go.Content = "Moving everything across…";
            try
            {
                await _controller.KeepPrivateAsync(null, CancellationToken.None);
                OpenAtLogin();
                Reset("Everyone’s files are private",
                    "They belong to Uncloud’s own account now. Nobody signed in to this Mac can look through them, and Uncloud starts with the Mac.");
                Done();
            }
            catch (OperationCanceledException) { ShowKeepPrivate(); }
            catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                Problem(error.Message);
                go.IsEnabled = later.IsEnabled = true;
                go.Content = "Continue";
            }
        };
        _body.Children.Add(_problem);
        _body.Children.Add(Buttons(later, go));
        if (_controller.CannotSetUpPrivately is { } why)
        {
            Problem(why);
            go.IsEnabled = false;
        }
    }

    /// <summary>
    /// Opens a drive or folder to Uncloud's account so it can be chosen in Settings, which is the
    /// only way a host in an account of its own can use a folder nobody opened to it.
    /// </summary>
    private void ShowAnotherFolder()
    {
        Reset("Keep everyone’s files somewhere else",
            "Choose a drive, or a folder outside your home folder. Uncloud opens it to its own account, and then you choose it in Settings. If it already has things in it, Uncloud makes a folder called Uncloud inside, so nothing there changes hands. macOS asks for an administrator’s password.");
        var choose = new Button { Content = "Choose Folder…", Background = Accent, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
        choose.Click += async (_, _) =>
        {
            if (await ChooseFolderAsync() is not { } chosen) return;
            choose.IsEnabled = false;
            choose.Content = "Opening it to Uncloud…";
            try
            {
                var folder = await _controller.PrepareFolderAsync(chosen, CancellationToken.None);
                Reset("Now choose it in Settings",
                    "In Uncloud’s Settings, enter this folder as where everyone’s files are kept. The files already in Uncloud stay where they are until you move them.");
                var path = new TextBox { Text = folder, IsReadOnly = true };
                _body.Children.Add(path);
                var settings = new Button { Content = "Open Settings", Background = Accent, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
                settings.Click += (_, _) =>
                {
                    App.Open(new Uri(DesktopController.HostAddress, "/?settings").ToString());
                    Close();
                };
                _body.Children.Add(settings);
            }
            catch (OperationCanceledException)
            {
                choose.IsEnabled = true;
                choose.Content = "Choose Folder…";
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                Problem(error.Message);
                choose.IsEnabled = true;
                choose.Content = "Choose Folder…";
            }
        };
        _body.Children.Add(_problem);
        _body.Children.Add(choose);
    }

    private void ShowTurnOff()
    {
        Reset("Turn off Uncloud on this Mac?",
            "Nobody can reach Uncloud until it’s on again, and other computers stop syncing. Everyone’s files and accounts stay here, still private to Uncloud’s own account, and making this Mac the host again picks them up. macOS asks for an administrator’s password.");
        var off = new Button { Content = "Turn Off", HorizontalAlignment = HorizontalAlignment.Right };
        off.Click += async (_, _) =>
        {
            off.IsEnabled = false;
            try
            {
                await _controller.TurnOffAsync(CancellationToken.None);
                Close();
            }
            catch (OperationCanceledException) { off.IsEnabled = true; }
            catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                Problem(error.Message);
                off.IsEnabled = true;
            }
        };
        _body.Children.Add(_problem);
        _body.Children.Add(off);
    }

    private async Task<string?> ChooseFolderAsync()
    {
        var chosen = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where should everyone’s files be kept?",
            AllowMultiple = false
        });
        return chosen.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>
    /// Uncloud itself starts with the Mac now; this app opening too is what hands it the Mac's
    /// folders, for bringing files in from them.
    /// </summary>
    private static void OpenAtLogin()
    {
        if (OperatingSystem.IsMacOS() && Environment.ProcessPath is { } app)
            LoginItem.ForThisUser(app).Set(true);
    }

    private void Done()
    {
        var close = new Button { Content = "Done", Background = Accent, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        _body.Children.Add(close);
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    private void ShowDisconnect()
    {
        Reset("Disconnect this computer?",
            $"It stops syncing with Uncloud. Everything already in {_controller.Paths.Files} stays here, and nothing is deleted from Uncloud.");
        var disconnect = new Button { Content = "Disconnect", HorizontalAlignment = HorizontalAlignment.Right };
        disconnect.Click += async (_, _) =>
        {
            try
            {
                await _controller.DisconnectAsync(CancellationToken.None);
                Close();
            }
            catch (Exception error) when (error is Homebase.Core.LibraryException or HttpRequestException)
            {
                Problem(error.Message);
            }
        };
        _body.Children.Add(_problem);
        _body.Children.Add(disconnect);
    }

    private static StackPanel Labelled(string label, Control field) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontSize = 12, Opacity = 0.7 }, field }
    };

    /// <summary>The other ways to go from here, quietly, under whatever the window is doing.</summary>
    private static WrapPanel Links(params (string Text, Action Go)[] links)
    {
        var panel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (text, go) in links)
        {
            var link = new HyperlinkButton { Content = text, Margin = new Thickness(6, 0) };
            link.Click += (_, _) => go();
            panel.Children.Add(link);
        }
        return panel;
    }

    private void Problem(string message)
    {
        _problem.Text = message;
        _problem.IsVisible = true;
    }
}
