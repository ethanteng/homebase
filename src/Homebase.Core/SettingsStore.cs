using System.Text.Json;

namespace Homebase.Core;

public sealed class SettingsStore(string directory)
{
    private readonly string _file = Path.Combine(directory, "settings.json");

    public string? Load()
    {
        if (!File.Exists(_file)) return null;
        try
        {
            return JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(_file))?.RootPath;
        }
        catch (JsonException)
        {
            // A corrupt preference file must not prevent choosing a new library.
            return null;
        }
    }

    public async Task SaveAsync(string root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new LibraryState(root, Path.GetFileName(root))), cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
