using System.Text.Json;

namespace Homebase.Core.Providers;

/// <summary>
/// Keeps the Dropbox refresh token outside the library, next to the root preference, with
/// owner-only permissions. The token grants read access to the connected account, so it is
/// never written into the library folder itself, where it would travel with synced files.
/// </summary>
public sealed class DropboxTokenStore(string directory)
{
    private readonly string _file = Path.Combine(directory, "dropbox.json");

    private sealed record Stored(string RefreshToken, string? AccountName);

    public string? Load()
    {
        if (!File.Exists(_file)) return null;
        try
        {
            return JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file))?.RefreshToken;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string? LoadAccountName()
    {
        if (!File.Exists(_file)) return null;
        try
        {
            return JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file))?.AccountName;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string refreshToken, string? accountName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"dropbox.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Stored(refreshToken, accountName)), cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Clear()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }
}
