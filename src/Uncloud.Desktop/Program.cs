using Avalonia;

namespace Uncloud.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = DesktopPaths.ForThisUser();
        // One copy at a time: two would each start a Syncthing on the same folder and port.
        Directory.CreateDirectory(paths.AppData);
        FileStream instance;
        try
        {
            instance = new FileStream(Path.Combine(paths.AppData, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return 0;
        }
        using (instance)
        {
            App.Paths = paths;
            App.ServerDirectory = ServerDirectory();
            App.StartupLink = args.FirstOrDefault(argument => argument.StartsWith(PairingLink.Scheme + "://", StringComparison.OrdinalIgnoreCase));
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    /// <summary>
    /// The Uncloud server the app carries, with Syncthing and the tunnel beside it: inside the
    /// bundle on macOS (Contents/Resources/server), beside the app anywhere else.
    /// </summary>
    private static string ServerDirectory()
    {
        if (Environment.GetEnvironmentVariable("UNCLOUD_SERVER_DIR") is { Length: > 0 } configured) return configured;
        var bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "server"));
        return Directory.Exists(bundled) ? bundled : Path.Combine(AppContext.BaseDirectory, "server");
    }
}
