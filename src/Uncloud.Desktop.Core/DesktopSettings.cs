using System.Text.Json;
using System.Text.Json.Serialization;

namespace Uncloud.Desktop;

/// <summary>What this computer is to Uncloud: nothing yet, the host, or one of somebody's computers.</summary>
public enum DesktopMode { Unset, Host, Computer }

/// <summary>
/// Everything the app remembers, in one small file beside its own Syncthing. Nothing secret is
/// kept here: a pairing code is used once and forgotten, and what lets this computer sync is
/// Syncthing's own key, which never leaves its directory.
/// </summary>
public sealed record DesktopSettings
{
    public DesktopMode Mode { get; init; }

    /// <summary>The Uncloud this computer paired with, as the person reached it.</summary>
    public string? Address { get; init; }
    public string? AccountName { get; init; }
    public string? HostDeviceId { get; init; }

    /// <summary>On the host: whether to open the tunnel so the host can be reached from anywhere.</summary>
    public bool ReachFromAnywhere { get; init; }

    public bool IsPaired => Mode is DesktopMode.Computer && HostDeviceId is { Length: > 0 };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static DesktopSettings Load(DesktopPaths paths)
    {
        try
        {
            return File.Exists(paths.SettingsFile)
                ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(paths.SettingsFile), Json) ?? new()
                : new();
        }
        catch (JsonException)
        {
            // A file somebody broke by hand is no reason to refuse to start; it's set up again.
            return new();
        }
    }

    public void Save(DesktopPaths paths)
    {
        Directory.CreateDirectory(paths.AppData);
        var temporary = paths.SettingsFile + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, paths.SettingsFile, overwrite: true);
    }
}

/// <summary>Where the app keeps its own state, and where people's files land on this computer.</summary>
public sealed class DesktopPaths(string appData, string files)
{
    public string AppData { get; } = appData;
    /// <summary>~/Uncloud: the folder people see, and the only one the app ever writes into.</summary>
    public string Files { get; } = files;
    public string SettingsFile => Path.Combine(AppData, "desktop.json");
    public string Syncthing => Path.Combine(AppData, "syncthing");

    public static DesktopPaths ForThisUser()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "Uncloud")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Uncloud");
        return new DesktopPaths(appData, Path.Combine(home, "Uncloud"));
    }
}
