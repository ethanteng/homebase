namespace Uncloud.Desktop;

/// <summary>
/// Opening at login on macOS, by the per-user LaunchAgent every app can write without asking for
/// anything: a plist in ~/Library/LaunchAgents that launchd reads when the person signs in.
/// </summary>
public sealed class LoginItem(string launchAgents, string executable)
{
    public const string Label = "life.uncloud.app";

    public string PlistPath => Path.Combine(launchAgents, Label + ".plist");
    public bool IsEnabled => File.Exists(PlistPath);

    public static LoginItem ForThisUser(string executable) => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"),
        executable);

    public void Set(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(PlistPath)) File.Delete(PlistPath);
            return;
        }
        Directory.CreateDirectory(launchAgents);
        File.WriteAllText(PlistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{Label}</string>
              <key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(executable)}</string></array>
              <key>RunAtLoad</key><true/>
              <key>ProcessType</key><string>Interactive</string>
            </dict>
            </plist>
            """);
    }
}
