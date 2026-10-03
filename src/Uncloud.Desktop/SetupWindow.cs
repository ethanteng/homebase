using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Homebase.Core.Sync;
using QRCoder;

namespace Uncloud.Desktop;

/// <summary>
/// The one window the app has. First run asks what this computer is. A computer then finds the
/// Uncloud on its network and shows a QR code for its owner to scan and approve on their phone —
/// or pairs with a link or an address and code, as it always could. The host starts Uncloud and
/// opens it in the browser, where the first account is made as it always has been.
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

    public SetupWindow(DesktopController controller, string? link, bool disconnect)
    {
        _controller = controller;
        Title = "Uncloud";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _body;
        Closed += (_, _) => _work?.Cancel();

        if (disconnect) ShowDisconnect();
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
            "Everyone’s files live here. Keep it on and awake while people use it.", ShowHost));
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

    private void ShowHost()
    {
        Reset("Make this Mac the host",
            "Uncloud will run here from the menu bar, with everything it needs built in. Next, your browser opens to make the first account and choose where everyone’s files are kept.");
        var start = new Button { Content = "Start Uncloud Here", Background = Accent, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
        start.Click += async (_, _) =>
        {
            start.IsEnabled = false;
            start.Content = "Starting…";
            try
            {
                await _controller.BecomeHostAsync(CancellationToken.None);
                App.Open(_controller.Server!.Address.ToString());
                Close();
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
