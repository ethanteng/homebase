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
    TurnOff,
    /// <summary>Bringing Uncloud itself up to date with an app that was updated.</summary>
    Update,
    /// <summary>Keeping this Mac awake for everybody, or letting it sleep again.</summary>
    KeepAwake
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
        else if (start is SetupStart.Update) ShowUpdate();
        else if (start is SetupStart.KeepAwake) ShowKeepAwake();
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
        else if (_controller.HasKeptHost)
        {
            Reset("Turn Uncloud on again",
                "Uncloud kept everyone’s files and accounts on this Mac when it was turned off, still private to its own account. Starting it again picks them all up where they were. macOS asks for an administrator’s password.");
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
        var awake = _controller.CanSetUpPrivately ? AwakeChoice() : null;
        var start = new Button { Content = "Start Uncloud Here", Background = Accent, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
        start.Click += async (_, _) => await AdministerAsync("Make this Mac the host", start, "Starting…",
            (inTerminal, stop) => _controller.BecomeHostAsync(root, stop, inTerminal, Chosen(awake)),
            again: () => ShowHost(root),
            done: () =>
            {
                OpenAtLogin();
                App.Open(DesktopController.HostAddress.ToString());
                Close();
            });
        if (awake is not null) _body.Children.Add(awake);
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
        var awake = _controller.OffersToKeepAwake ? AwakeChoice() : null;
        var later = new Button { Content = "Not Now" };
        later.Click += (_, _) => Close();
        var go = new Button { Content = "Continue", Background = Accent, Foreground = Brushes.White };
        go.Click += async (_, _) => await AdministerAsync("Keep everyone’s files private", go, "Moving everything across…",
            (inTerminal, stop) => _controller.KeepPrivateAsync(null, stop, inTerminal, Chosen(awake)),
            again: ShowKeepPrivate,
            done: () =>
            {
                OpenAtLogin();
                Reset("Everyone’s files are private",
                    "They belong to Uncloud’s own account now. Nobody signed in to this Mac can look through them, and Uncloud starts with the Mac.");
                Done();
            },
            later);
        if (awake is not null) _body.Children.Add(awake);
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
            string? folder = null;
            await AdministerAsync("Keep everyone’s files somewhere else", choose, "Opening it to Uncloud…",
                async (inTerminal, stop) => folder = await _controller.PrepareFolderAsync(chosen, stop, inTerminal),
                again: ShowAnotherFolder,
                done: () =>
                {
                    Reset("Now choose it in Settings",
                        "In Uncloud’s Settings, enter this folder as where everyone’s files are kept. The files already in Uncloud stay where they are until you move them.");
                    _body.Children.Add(new TextBox { Text = folder, IsReadOnly = true });
                    var settings = new Button { Content = "Open Settings", Background = Accent, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
                    settings.Click += (_, _) =>
                    {
                        App.Open(new Uri(DesktopController.HostAddress, "/?settings").ToString());
                        Close();
                    };
                    _body.Children.Add(settings);
                });
        };
        _body.Children.Add(_problem);
        _body.Children.Add(choose);
    }

    /// <summary>
    /// Uncloud runs from a copy of itself only an administrator can change, so that nobody can make
    /// it read everybody's files by changing the app. Updating the app reaches it this way.
    /// </summary>
    private void ShowUpdate()
    {
        Reset("Finish updating Uncloud",
            "This app has been updated. Uncloud itself runs from a copy only an administrator can change, so nobody can swap in something that reads everyone’s files. Updating that copy asks for an administrator’s password. Nobody is signed out, and it takes a moment.");
        // Asked here, ticked, of a host that was set up before Uncloud could keep it awake: the
        // password is being asked for anyway.
        var awake = _controller.OffersToKeepAwake ? AwakeChoice() : null;
        var later = new Button { Content = "Not Now" };
        later.Click += (_, _) => Close();
        var go = new Button { Content = "Update", Background = Accent, Foreground = Brushes.White };
        go.Click += async (_, _) => await AdministerAsync("Finish updating Uncloud", go, "Updating…",
            (inTerminal, stop) => _controller.KeepPrivateAsync(null, stop, inTerminal, Chosen(awake)),
            again: ShowUpdate, done: Close, later);
        if (awake is not null) _body.Children.Add(awake);
        _body.Children.Add(_problem);
        _body.Children.Add(Buttons(later, go));
    }

    /// <summary>
    /// Whether to keep this Mac awake, asked wherever an administrator's password is asked anyway.
    /// Ticked: a host asleep can't hand anybody their files.
    /// </summary>
    private static CheckBox AwakeChoice() => new()
    {
        IsChecked = true,
        Content = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "Keep this Mac awake, so everyone can reach their files", TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = "It won’t go to sleep while it’s the host, and starts up again by itself after a power cut where it can. The screen still turns off as usual.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                }
            }
        }
    };

    /// <summary>What was chosen, or null where nothing was asked, which leaves things as they are.</summary>
    private static bool? Chosen(CheckBox? awake) => awake is null ? null : awake.IsChecked == true;

    /// <summary>From the menu: keeping this host awake, or letting it go back to sleeping as it was set to.</summary>
    private void ShowKeepAwake()
    {
        var awake = _controller.KeepsAwake;
        var heading = awake ? "Let this Mac sleep again?" : "Keep this Mac awake";
        Reset(heading, awake
            ? "It goes back to sleeping as it’s set to in System Settings, and to what it did after a power cut before. While it’s asleep, nobody can reach their files and other computers stop syncing. macOS asks for an administrator’s password."
            : "A Mac that’s asleep can’t hand anybody their files, and macOS puts one to sleep after a few minutes with nobody at it. Uncloud can keep it awake while it’s the host, and have it start up again by itself after a power cut where it can. The screen still turns off as usual, and turning Uncloud off puts everything back. macOS asks for an administrator’s password.");
        var later = new Button { Content = awake ? "Cancel" : "Not Now" };
        later.Click += (_, _) => Close();
        var go = awake
            ? new Button { Content = "Let It Sleep" }
            : new Button { Content = "Keep It Awake", Background = Accent, Foreground = Brushes.White };
        go.Click += async (_, _) => await AdministerAsync(heading, go, awake ? "Letting it sleep…" : "Keeping it awake…",
            (inTerminal, stop) => _controller.KeepAwakeAsync(!awake, stop, inTerminal),
            again: ShowKeepAwake, done: Close, later);
        _body.Children.Add(_problem);
        _body.Children.Add(Buttons(later, go));
    }

    private void ShowTurnOff()
    {
        Reset("Turn off Uncloud on this Mac?",
            "Nobody can reach Uncloud until it’s on again, and other computers stop syncing. Everyone’s files and accounts stay here, still private to Uncloud’s own account, and making this Mac the host again picks them up. macOS asks for an administrator’s password.");
        var off = new Button { Content = "Turn Off", HorizontalAlignment = HorizontalAlignment.Right };
        off.Click += async (_, _) => await AdministerAsync("Turn off Uncloud on this Mac?", off, "Turning off…",
            (inTerminal, stop) => _controller.TurnOffAsync(stop, inTerminal),
            again: ShowTurnOff, done: Close);
        _body.Children.Add(_problem);
        _body.Children.Add(off);
    }

    /// <summary>
    /// Runs something that needs an administrator's password, asking in macOS's own prompt first.
    /// That prompt can come back cancelled without ever having been shown — some Macs won't show it
    /// for an app in the menu bar — so cancelled is never taken as the end of it: the same step is
    /// offered in Terminal, where sudo asks instead.
    /// </summary>
    /// <param name="heading">The panel's own heading, kept if it has to offer Terminal.</param>
    /// <param name="work">The step, told whether to ask in Terminal, and when to stop waiting for it there.</param>
    /// <param name="again">The panel as it was, for trying again.</param>
    /// <param name="done">What happens once it has worked.</param>
    /// <param name="others">Buttons beside <paramref name="go"/> that wait with it.</param>
    private async Task AdministerAsync(string heading, Button go, string busy,
        Func<bool, CancellationToken, Task> work, Action again, Action done, params Button[] others)
    {
        var idle = go.Content;
        go.IsEnabled = false;
        foreach (var other in others) other.IsEnabled = false;
        go.Content = busy;
        try
        {
            await work(false, CancellationToken.None);
            done();
        }
        catch (OperationCanceledException)
        {
            OfferTerminal(heading, work, again, done);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Problem(error.Message);
            go.IsEnabled = true;
            foreach (var other in others) other.IsEnabled = true;
            go.Content = idle;
        }
    }

    private void OfferTerminal(string heading, Func<bool, CancellationToken, Task> work, Action again, Action done)
    {
        Reset(heading,
            "macOS didn’t ask for your password, or the request was cancelled, so nothing has changed. You can finish in Terminal instead: a window opens where you type the same password, and this window carries on by itself once it’s done.");
        var waiting = new TextBlock
        {
            Text = "In the Terminal window that opened, type your Mac’s password and press Return. Nothing shows as you type.",
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        var cancel = new Button { Content = "Cancel", IsVisible = false };
        cancel.Click += (_, _) =>
        {
            // Settled with Terminal rather than just stopped waiting: anything that has already begun
            // there finishes first, and this window carries on from however it ends.
            cancel.IsEnabled = false;
            waiting.Text = "Stopping… If it has already begun in Terminal, it finishes there first.";
            _work?.Cancel();
        };
        var retry = new Button { Content = "Try Again" };
        retry.Click += (_, _) => again();
        var terminal = new Button { Content = "Finish in Terminal", Background = Accent, Foreground = Brushes.White };
        terminal.Click += async (_, _) =>
        {
            var stop = Begin();
            terminal.IsEnabled = retry.IsEnabled = false;
            terminal.Content = "Waiting for Terminal…";
            waiting.IsVisible = cancel.IsVisible = true;
            _problem.IsVisible = false;
            try
            {
                await work(true, stop);
                done();
            }
            // Stopped waiting, or the window closed: whatever ran as this person before runs again.
            catch (OperationCanceledException)
            {
                if (IsVisible) again();
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                Problem(error.Message);
                terminal.IsEnabled = retry.IsEnabled = true;
                terminal.Content = "Finish in Terminal";
                waiting.IsVisible = cancel.IsVisible = false;
            }
        };
        _body.Children.Add(waiting);
        _body.Children.Add(_problem);
        _body.Children.Add(Buttons(cancel, retry, terminal));
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
