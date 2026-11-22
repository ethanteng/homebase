using System.Reflection;
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
/// Uncloud on its network and asks to be added: its owner says yes after signing in, in this
/// computer's own browser, or on a phone that's signed in already by scanning a QR code — or it
/// pairs with a link or an address and code, as it always could. The host sets Uncloud up in an
/// account of its own and opens it in the browser, where the first account is made as it always
/// has been.
/// </summary>
public sealed class SetupWindow : Window
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#1f6fe5"));
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    private readonly DesktopController _controller;
    private readonly StackPanel _body = new() { Spacing = 16, Margin = new Thickness(28) };
    private readonly TextBlock _problem = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    // Whatever the window is doing in the background — looking for Uncloud, or waiting for somebody
    // to approve — stopped when it moves on to something else or is closed.
    private CancellationTokenSource? _work;

    public SetupWindow(DesktopController controller, string? link, SetupStart start = SetupStart.Next)
    {
        _controller = controller;
        Title = "Uncloud";
        Width = 520;
        FontSize = 15;
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
        _body.Children.Add(new TextBlock { Text = heading, FontSize = 24, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        if (explanation.Length > 0)
            _body.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
    }

    /// <summary>
    /// "Version 0.492 (a553cc1)", as packaging stamped it in, so it matches what the web page shows.
    /// Nothing in a build that wasn't packaged, which only has the SDK's placeholder.
    /// </summary>
    internal static string? VersionLabel =>
        typeof(SetupWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            is { Length: > 0 } version && !version.StartsWith("1.0.0", StringComparison.Ordinal)
            ? $"Version {version}"
            : null;

    /// <summary>A line that is only worth a glance: smaller, quieter, never the point of the window.</summary>
    private static TextBlock Quiet(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 14 };

    private CancellationToken Begin()
    {
        _work = new CancellationTokenSource();
        return _work.Token;
    }

    private void ShowChoice()
    {
        Reset("Welcome to Uncloud", "How will you use this Mac?");
        _body.Children.Add(Choice("Store everyone’s files here",
            "For the Mac that stays home and on", () => ShowHost()));
        _body.Children.Add(Choice("Connect to your Uncloud",
            "Keep a folder on this Mac in sync", ShowFind));
        if (VersionLabel is { } version)
            _body.Children.Add(new TextBlock { Text = version, Opacity = 0.6, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center });
    }

    private static Button Choice(string title, string detail, Action choose)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 14),
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
        Reset("Connect this Mac", "Looking for your Uncloud…");
        var stop = Begin();
        // Two seconds of looking: nothing to offer instead until it has had them.
        _body.Children.Add(new ProgressBar { IsIndeterminate = true });

        IReadOnlyList<FoundHost> found;
        try { found = await _controller.FindAsync(stop); }
        catch (OperationCanceledException) { return; }
        if (stop.IsCancellationRequested) return;

        if (!GoTo(found))
            // Found but not reachable from here is worth saying: what to do about it is on the host.
            if (found.FirstOrDefault() is { } host) ShowUnreachable(host);
            else ShowAddress(notFound: true);
    }

    /// <summary>On to whatever was found that this Mac can reach, if anything was.</summary>
    private bool GoTo(IReadOnlyList<FoundHost> found)
    {
        var reachable = found.Where(host => Reachable(host) is not null).ToList();
        if (reachable.Count == 1) ShowConnect(Reachable(reachable[0])!, reachable[0].Name);
        else if (reachable.Count > 1) ShowPick(reachable);
        return reachable.Count > 0;
    }

    private static Uri? Reachable(FoundHost host)
    {
        try { return host.Url is null ? null : PairingLink.ParseAddress(host.Url); }
        catch (FormatException) { return null; }
    }

    private void ShowPick(IReadOnlyList<FoundHost> hosts)
    {
        Reset("Connect this Mac", "Found more than one Uncloud. Which is yours?");
        foreach (var host in hosts)
        {
            var address = Reachable(host)!;
            _body.Children.Add(Choice(host.Name, address.Host, () => ShowConnect(address, host.Name)));
        }
        _body.Children.Add(Links(("Other ways to connect", () => ShowAddress(notFound: false))));
    }

    /// <summary>
    /// Found on this network, but not open to other computers: the host only answers its own
    /// browser until Access From Anywhere is on. Said once, with who can do something about it, and
    /// then this keeps looking, so it carries on by itself the moment it's turned on.
    /// </summary>
    private async void ShowUnreachable(FoundHost host)
    {
        Reset($"{host.Name} isn’t open to other computers yet", "");
        var stop = Begin();
        _body.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#fdf0dc")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = $"Turn on Access From Anywhere on {host.Name}", FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#8a4b00")), TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = $"On {host.Name}, open the Uncloud menu and choose Access From Anywhere. Ask whoever set up Uncloud if that isn’t you.",
                        Foreground = new SolidColorBrush(Color.Parse("#5c3300")),
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        });
        _body.Children.Add(Waiting("This window carries on by itself once it’s on."));
        _body.Children.Add(Links(("Other ways to connect", () => ShowAddress(notFound: false))));

        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stop);
                var found = await _controller.FindAsync(stop);
                if (stop.IsCancellationRequested) return;
                if (GoTo(found)) return;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <param name="notFound">Whether this is because looking found nothing, rather than having been asked for.</param>
    private void ShowAddress(bool notFound)
    {
        Reset("Connect this Mac", notFound
            ? "Couldn’t find your Uncloud on this network. Enter the address you open it at in your browser."
            : "Enter the address you open Uncloud at in your browser.");

        var address = new TextBox { Watermark = "Address, such as https://home.example.ts.net", Text = _controller.Settings.Address };
        var next = new Button { Content = "Next", Background = Accent, Foreground = Brushes.White };
        next.Click += (_, _) =>
        {
            var text = address.Text?.Trim() ?? "";
            // The link from Computers, pasted whole, works here too.
            if (text.StartsWith(PairingLink.Scheme + "://", StringComparison.OrdinalIgnoreCase))
            {
                ShowCode(text);
                return;
            }
            try { ShowConnect(PairingLink.ParseAddress(text), null); }
            catch (FormatException error) { Problem(error.Message); }
        };

        _body.Children.Add(Labelled("Uncloud address", address));
        _body.Children.Add(_problem);
        if (notFound)
        {
            var again = new Button { Content = "Look Again" };
            again.Click += (_, _) => ShowFind();
            _body.Children.Add(Buttons(again, next));
        }
        else _body.Children.Add(Buttons(next));
        _body.Children.Add(Links(("Type a pairing code instead", () => ShowCode(null))));
    }

    /// <summary>
    /// Asks the Uncloud at this address to add this Mac, and waits for somebody signed in to say
    /// yes. The quickest way to say it is right here, in this Mac's browser, so that's the button;
    /// a phone that's already signed in can scan a QR code instead. Either way the same request is
    /// waiting, and one that runs out, or that a restarted host has forgotten, is quietly replaced.
    /// </summary>
    private async void ShowConnect(Uri address, string? hostName)
    {
        Reset("Connect this Mac", "");
        var stop = Begin();
        var heading = (TextBlock)_body.Children[0];
        var name = DesktopController.ComputerName();
        PairingRequest? request = null;
        void OpenBrowser() { if (request is not null) App.Open(request.ApproveUrl.ToString()); }

        // Ready: what this is, and the one thing to do.
        var signIn = new Button
        {
            Content = "Getting ready…",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(16, 11),
            Background = Accent,
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold
        };
        var preparing = Quiet("Getting this Mac ready to sync. The first time, this can take a minute.");
        var ready = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                HostCard(hostName ?? address.Host, address.Host, found: hostName is not null),
                new TextBlock
                {
                    Text = $"Sign in to Uncloud in your browser to add {name}. Your files will sync to the Uncloud folder in your home folder.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.8
                },
                signIn,
                preparing
            }
        };

        // Waiting on this Mac's browser.
        var browserWait = new StackPanel
        {
            Spacing = 12,
            IsVisible = false,
            Children = { Waiting("Waiting for you to sign in and add this Mac…") }
        };

        // Waiting on a phone, with the code to scan.
        var code = new Image { Width = 200, Height = 200, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(code, BitmapInterpolationMode.None);
        var phoneWait = new StackPanel
        {
            Spacing = 12,
            IsVisible = false,
            Children =
            {
                code,
                new TextBlock { Text = "Scan with your phone’s camera, then tap Add this computer.", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                Waiting("Waiting for you to add this Mac on your phone…")
            }
        };

        var cancel = new Button { Content = "Cancel" };
        var waitingActions = new StackPanel { Spacing = 10, IsVisible = false };
        var otherWays = Links(("Other ways to connect", () => ShowAddress(notFound: false)));
        // Declared ahead of Show, which hides it, so the link can call Show itself.
        WrapPanel phoneLink = null!;
        phoneLink = Links(("Scan with a phone that’s signed in instead", () => Show(phone: true)));
        phoneLink.IsEnabled = false;

        void Show(bool? phone)
        {
            heading.Text = phone switch { null => "Connect this Mac", true => "Scan with your phone", false => "Finish in your browser" };
            ready.IsVisible = phone is null;
            phoneLink.IsVisible = phone is null;
            otherWays.IsVisible = phone is null;
            browserWait.IsVisible = phone is false;
            phoneWait.IsVisible = phone is true;
            waitingActions.IsVisible = phone is not null;
            waitingActions.Children.Clear();
            if (phone is false) waitingActions.Children.Add(Links(("Didn’t see a browser window? Open it again", OpenBrowser)));
            if (phone is true) waitingActions.Children.Add(Links(("Sign in on this Mac instead", () => { OpenBrowser(); Show(phone: false); })));
            waitingActions.Children.Add(Buttons(cancel));
        }
        signIn.Click += (_, _) => { OpenBrowser(); Show(phone: false); };
        cancel.Click += (_, _) => Show(phone: null);

        _body.Children.Add(ready);
        _body.Children.Add(browserWait);
        _body.Children.Add(phoneWait);
        _body.Children.Add(_problem);
        _body.Children.Add(waitingActions);
        _body.Children.Add(phoneLink);
        _body.Children.Add(otherWays);

        try
        {
            while (true)
            {
                request = await _controller.RequestPairingAsync(address, name, stop);
                code.Source = QrCode(request.ApproveUrl);
                signIn.Content = "Sign in to connect";
                signIn.IsEnabled = true;
                phoneLink.IsEnabled = true;
                preparing.IsVisible = false;
                _problem.IsVisible = false;

                while (true)
                {
                    await Task.Delay(Poll, stop);
                    PairingResult? result;
                    try { result = await _controller.CollectPairingAsync(request, stop); }
                    catch (PairingExpiredException) { break; }
                    catch (PairingDeclinedException)
                    {
                        ShowNotAdded(address, hostName);
                        return;
                    }
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
            Show(phone: null);
            signIn.Content = "Sign in to connect";
            signIn.IsEnabled = false;
            phoneLink.IsEnabled = false;
            preparing.IsVisible = false;
            var again = new Button { Content = "Try Again", HorizontalAlignment = HorizontalAlignment.Right };
            again.Click += (_, _) => ShowConnect(address, hostName);
            _body.Children.Insert(_body.Children.IndexOf(_problem) + 1, again);
        }
    }

    /// <summary>Somebody signed in said no. Nothing changed here, and it can be asked again.</summary>
    private void ShowNotAdded(Uri address, string? hostName)
    {
        Reset("This Mac wasn’t added", "Whoever was asked chose not to add it, so nothing has changed. If that was a mistake, start again.");
        var again = new Button { Content = "Start Again", Background = Accent, Foreground = Brushes.White };
        again.Click += (_, _) => ShowConnect(address, hostName);
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        _body.Children.Add(Buttons(close, again));
    }

    /// <summary>The Uncloud this Mac is about to join, so nobody has to wonder which.</summary>
    private static Border HostCard(string name, string address, bool found) => new()
    {
        BorderBrush = new SolidColorBrush(Color.Parse("#40808080")),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 12),
        Child = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                new TextBlock
                {
                    [DockPanel.DockProperty] = Dock.Right,
                    Text = found ? "Found" : "",
                    Foreground = new SolidColorBrush(Color.Parse("#17663a")),
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center
                },
                new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = address, Opacity = 0.7, FontSize = 14, TextWrapping = TextWrapping.Wrap }
                    }
                }
            }
        }
    };

    /// <summary>Something this window is waiting on, said plainly, with motion so it doesn't look stuck.</summary>
    private static StackPanel Waiting(string text) => new()
    {
        Spacing = 8,
        Children =
        {
            new ProgressBar { IsIndeterminate = true },
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }
        }
    };

    /// <summary>Dark on white whatever the window's theme, with the quiet border scanners need.</summary>
    private static Bitmap QrCode(Uri url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url.ToString(), QRCodeGenerator.ECCLevel.M);
        return new Bitmap(new MemoryStream(new PngByteQRCode(data).GetGraphic(10)));
    }

    /// <summary>
    /// Pairing with a code from Computers. A link from there arrives with the address and code in
    /// it, and asks only for a yes; typed by hand, it takes both.
    /// </summary>
    private void ShowCode(string? link)
    {
        PairingLink? parsed = null;
        string? problem = null;
        if (link is not null)
        {
            try { parsed = PairingLink.Parse(link); }
            catch (FormatException error) { problem = error.Message; }
        }

        if (parsed is not null)
            Reset("Connect this Mac", $"Connect to Uncloud at {parsed.Address.Host}, with the code Computers gave you.");
        else
            Reset("Type a pairing code",
                "In Uncloud in your browser, open Computers, then Other ways to connect, and choose Get a pairing code.");
        if (problem is not null) Problem(problem);

        var address = new TextBox { Watermark = "Address, such as https://home.example.ts.net" };
        var code = new TextBox { Watermark = "Pairing code, such as AB12C-DE34F" };
        var name = new TextBox { Text = DesktopController.ComputerName(), Watermark = "What to call this computer" };
        if (parsed is not null)
        {
            address.Text = parsed.Address.ToString().TrimEnd('/');
            code.Text = parsed.Code;
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

        if (parsed is null)
        {
            _body.Children.Add(Labelled("Uncloud address", address));
            _body.Children.Add(Labelled("Pairing code", code));
        }
        _body.Children.Add(Labelled("This computer’s name", name));
        _body.Children.Add(_problem);
        _body.Children.Add(connect);
        _body.Children.Add(Links(("Sign in with your browser instead", ShowFind)));
    }

    private void ShowPaired(string account)
    {
        Reset("You’re connected",
            $"{account}’s files are arriving in {_controller.Paths.Files}. Changes sync both ways, and anything deleted is kept for 30 days.");
        var open = new Button { Content = "Open Uncloud Folder", Background = Accent, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
        open.Click += (_, _) => { App.Open(_controller.Paths.Files); Close(); };
        _body.Children.Add(open);
    }

    private void ShowHost(string? root = null)
    {
        if (!_controller.CanSetUpPrivately)
        {
            Reset("Set up this Mac",
                "Everyone’s files will be stored here. Next, your browser opens to create your account.");
        }
        else if (_controller.HasKeptHost)
        {
            Reset("Turn Uncloud back on",
                "Everyone’s files and accounts are still here, and pick up where they left off.");
        }
        else
        {
            Reset("Set up this Mac", "Everyone’s files will be stored here.");
            if (_controller.AsksWhereFilesGo)
            {
                _body.Children.Add(new TextBlock
                {
                    Text = root is null ? "Stored on this Mac" : $"Stored in {root}",
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                _body.Children.Add(root is null
                    ? Links(("Use an external drive or another folder", async () =>
                    {
                        if (await ChooseFolderAsync() is { } chosen) ShowHost(chosen);
                    }))
                    : Links(("Use this Mac instead", () => ShowHost())));
            }
            // Why it runs in an account of its own, as the one thing it means for the people here.
            _body.Children.Add(Quiet("Files stay private, even from others who use this Mac."));
        }
        var awake = _controller.CanSetUpPrivately ? AwakeChoice() : null;
        var start = new Button { Content = "Set Up", Background = Accent, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
        start.Click += async (_, _) => await AdministerAsync("Set up this Mac", start, "Setting up…",
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
        _body.Children.Add(_controller.CanSetUpPrivately ? Footer("Needs your Mac password", start) : start);
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
            "Right now, anyone signed in to this Mac can open everyone’s files. Uncloud can keep them in a separate account instead, and start when the Mac does.");
        _body.Children.Add(Quiet("Accounts, files and synced computers carry on as they are. Files in your home folder move to a folder only Uncloud can open."));
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
                    "Nobody signed in to this Mac can open them now, and Uncloud starts with the Mac.");
                Done();
            },
            later);
        if (awake is not null) _body.Children.Add(awake);
        _body.Children.Add(_problem);
        _body.Children.Add(Footer("Needs your Mac password", later, go));
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
        Reset("Store files somewhere else",
            "Choose a drive, or a folder outside your home folder. If it already has things in it, Uncloud makes its own folder inside.");
        var choose = new Button { Content = "Choose Folder…", Background = Accent, Foreground = Brushes.White };
        choose.Click += async (_, _) =>
        {
            if (await ChooseFolderAsync() is not { } chosen) return;
            string? folder = null;
            await AdministerAsync("Store files somewhere else", choose, "Getting it ready…",
                async (inTerminal, stop) => folder = await _controller.PrepareFolderAsync(chosen, stop, inTerminal),
                again: ShowAnotherFolder,
                done: () =>
                {
                    Reset("Now choose it in Settings",
                        "In Settings, set this folder as the storage location. Files already stored stay where they are until you move them.");
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
        _body.Children.Add(Footer("Needs your Mac password", choose));
    }

    /// <summary>
    /// Uncloud runs from a copy of itself only an administrator can change, so that nobody can make
    /// it read everybody's files by changing the app. Updating the app reaches it this way.
    /// </summary>
    private void ShowUpdate()
    {
        Reset("Finish updating", VersionLabel is { } version ? $"{version} is ready." : "");
        _body.Children.Add(new TextBlock
        {
            Text = "Enter your Mac password to finish. It takes a few seconds, and nobody gets signed out.",
            TextWrapping = TextWrapping.Wrap
        });
        // Asked here, ticked, of a host that was set up before Uncloud could keep it awake: the
        // password is being asked for anyway.
        var awake = _controller.OffersToKeepAwake ? AwakeChoice() : null;
        var later = new Button { Content = "Not Now" };
        later.Click += (_, _) => Close();
        var go = new Button { Content = "Update", Background = Accent, Foreground = Brushes.White };
        go.Click += async (_, _) => await AdministerAsync("Finish updating", go, "Updating…",
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
    private CheckBox AwakeChoice() => new()
    {
        IsChecked = true,
        Content = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "Keep this Mac awake", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = $"So files are always available. The screen still turns off.{Besides()}",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75,
                    FontSize = 14
                }
            }
        }
    };

    /// <summary>
    /// What else keeping this Mac awake comes to, as it is: a desktop starts up again after a power
    /// cut, and a laptop still sleeps when its lid is closed, which nothing here can stop.
    /// </summary>
    private string Besides() =>
        _controller.Power switch
        {
            { HasBattery: true, SleepDisabled: false } => " Keep the lid open.",
            { CanAutoRestart: true } => " It also turns back on after a power cut.",
            _ => ""
        };

    /// <summary>What was chosen, or null where nothing was asked, which leaves things as they are.</summary>
    private static bool? Chosen(CheckBox? awake) => awake is null ? null : awake.IsChecked == true;

    /// <summary>From the menu: keeping this host awake, or letting it go back to sleeping as it was set to.</summary>
    private void ShowKeepAwake()
    {
        var awake = _controller.KeepsAwake;
        var heading = awake ? "Let this Mac sleep again?" : "Keep this Mac awake?";
        Reset(heading, awake
            ? "While it sleeps, nobody can reach their files and computers stop syncing."
            : $"So everyone can reach their files. The screen still turns off.{Besides()}");
        var later = new Button { Content = awake ? "Cancel" : "Not Now" };
        later.Click += (_, _) => Close();
        var go = awake
            ? new Button { Content = "Let It Sleep" }
            : new Button { Content = "Keep It Awake", Background = Accent, Foreground = Brushes.White };
        go.Click += async (_, _) => await AdministerAsync(heading, go, awake ? "Letting it sleep…" : "Keeping it awake…",
            (inTerminal, stop) => _controller.KeepAwakeAsync(!awake, stop, inTerminal),
            again: ShowKeepAwake, done: Close, later);
        _body.Children.Add(_problem);
        _body.Children.Add(Footer("Needs your Mac password", later, go));
    }

    private void ShowTurnOff()
    {
        Reset("Turn off Uncloud on this Mac?",
            "Nobody can reach their files until it’s back on, and computers stop syncing. Everything stays here, ready for when you turn it on again.");
        var off = new Button { Content = "Turn Off", HorizontalAlignment = HorizontalAlignment.Right };
        off.Click += async (_, _) => await AdministerAsync("Turn off Uncloud on this Mac?", off, "Turning off…",
            (inTerminal, stop) => _controller.TurnOffAsync(stop, inTerminal),
            again: ShowTurnOff, done: Close);
        _body.Children.Add(_problem);
        _body.Children.Add(Footer("Needs your Mac password", off));
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
            Title = "Where should files be stored?",
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

    /// <summary>The buttons, with a quiet note beside them: what pressing the main one will ask for.</summary>
    private static DockPanel Footer(string note, params Button[] buttons)
    {
        var row = new DockPanel { LastChildFill = true };
        var actions = Buttons(buttons);
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(new TextBlock { Text = note, Opacity = 0.65, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        return row;
    }

    private void ShowDisconnect()
    {
        var account = _controller.Settings.AccountName is { Length: > 0 } name ? name : "the account it was added to";
        Reset("Disconnect this computer?",
            $"It stops syncing and is removed from Computers for {account}. Files in {_controller.Paths.Files} stay here, and nothing is deleted from Uncloud.");
        var address = _controller.Settings.Address;
        var disconnect = new Button { Content = "Disconnect", HorizontalAlignment = HorizontalAlignment.Right };
        disconnect.Click += async (_, _) =>
        {
            // Telling Uncloud can take a few seconds, over the tunnel especially.
            disconnect.IsEnabled = false;
            try
            {
                if (await _controller.DisconnectAsync(CancellationToken.None) is not { } stillListed)
                {
                    Close();
                    return;
                }
                // Disconnecting is what somebody reaches for to move this computer to another
                // account. When Uncloud couldn't be told, that's said now, with the account to
                // remove it from, before they find out by being refused.
                Reset("This computer is disconnected", stillListed);
                if (address is not null)
                    _body.Children.Add(Links(("Open Uncloud in Browser", () => App.Open(address))));
                Done();
            }
            catch (Exception error) when (error is Homebase.Core.LibraryException or HttpRequestException)
            {
                disconnect.IsEnabled = true;
                Problem(error.Message);
            }
        };
        _body.Children.Add(_problem);
        _body.Children.Add(disconnect);
    }

    private static StackPanel Labelled(string label, Control field) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, field }
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
