namespace Homebase.Core.Accounts;

/// <summary>
/// Where a host keeps what is its own: the accounts, the sessions, the key that seals provider
/// tokens, and the settings that say where everybody's files live and whether this host can be
/// reached from outside. Named here rather than worked out twice, because the Uncloud server is
/// not the only thing that needs to find it — the app that starts the server reads the same
/// settings, and two spellings of one path is two hosts that disagree about which is which.
/// </summary>
public static class HostPaths
{
    public static string DefaultConfigDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Homebase")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homebase");

    /// <summary>Whether this host opens a way in from outside, and by which tunnel.</summary>
    public const string RemoteAccessSetting = "remote_access";
}
