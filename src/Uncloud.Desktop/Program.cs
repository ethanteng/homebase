using Avalonia;
using Microsoft.Extensions.Logging;

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
            // Opened from Finder there's nowhere for .NET to print why it stopped, so it goes in the
            // log, where "Uncloud won't open" can be answered.
            AppDomain.CurrentDomain.UnhandledException += (_, unhandled) => Fatal(paths, unhandled.ExceptionObject as Exception);
            App.Paths = paths;
            App.ServerDirectory = ServerDirectory();
            App.StartupLink = args.FirstOrDefault(argument => argument.StartsWith(PairingLink.Scheme + "://", StringComparison.OrdinalIgnoreCase));
            try
            {
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception error)
            {
                Fatal(paths, error);
                throw;
            }
        }
    }

    private static void Fatal(DesktopPaths paths, Exception? error)
    {
        using var log = new FileLoggerProvider(paths.Log);
        log.CreateLogger("Uncloud").LogCritical(error, "Uncloud stopped");
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    /// <summary>
    /// The Uncloud server the app carries, with Syncthing and the tunnel beside it. In the Mac app
    /// it sits beside the app itself, sharing its copy of .NET; a build without it looks in ./server.
    /// </summary>
    private static string ServerDirectory()
    {
        if (Environment.GetEnvironmentVariable("UNCLOUD_SERVER_DIR") is { Length: > 0 } configured) return configured;
        var beside = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(beside, OperatingSystem.IsWindows() ? "Homebase.Server.exe" : "Homebase.Server"))
            ? beside
            : Path.Combine(beside, "server");
    }
}
