using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Uncloud.Desktop;

/// <summary>
/// The one window the app has. First run asks what this computer is; a computer then pairs with
/// a link or an address and code; the host starts Uncloud and opens it in the browser, where the
/// first account is made as it always has been.
/// </summary>
public sealed class SetupWindow : Window
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#1f6fe5"));

    private readonly DesktopController _controller;
    private readonly StackPanel _body = new() { Spacing = 14, Margin = new Thickness(28) };
    private readonly TextBlock _problem = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public SetupWindow(DesktopController controller, string? link, bool disconnect)
    {
        _controller = controller;
        Title = "Uncloud";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _body;

        if (disconnect) ShowDisconnect();
        else if (link is not null || controller.Settings.Mode is DesktopMode.Computer) ShowPair(link);
        else ShowChoice();
    }

    private void Reset(string heading, string explanation)
    {
        _body.Children.Clear();
        _problem.IsVisible = false;
        _body.Children.Add(new TextBlock { Text = heading, FontSize = 22, FontWeight = FontWeight.SemiBold });
        _body.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
    }

    private void ShowChoice()
    {
        Reset("Set up Uncloud", "Is this the computer everybody’s files live on, or one of your own computers?");
        _body.Children.Add(Choice("Connect this computer to an Uncloud",
            "Your files appear in an Uncloud folder here, and stay in step both ways.", () => ShowPair(null)));
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

    private void ShowPair(string? link)
    {
        Reset("Connect this computer",
            "In Uncloud, open My computers and choose Get a pairing code. Click the link it shows, or copy the address and code here.");

        var address = new TextBox { Watermark = "Address, such as https://home.example.ts.net" };
        var code = new TextBox { Watermark = "Pairing code, such as AB12C-DE34F" };
        var name = new TextBox { Text = Environment.MachineName, Watermark = "What to call this computer" };
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

    private void Problem(string message)
    {
        _problem.Text = message;
        _problem.IsVisible = true;
    }
}
