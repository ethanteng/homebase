namespace Homebase.Core;

/// <summary>Space on the volume a library folder sits on.</summary>
public sealed record StorageReport(long FreeBytes, long TotalBytes);

public static class Storage
{
    /// <summary>
    /// Space on the volume holding <paramref name="path"/>, or null when it can't be read. An
    /// external drive is its own volume, so this asks about the path rather than the boot disk.
    /// </summary>
    public static StorageReport? For(string path)
    {
        try
        {
            var drive = new DriveInfo(path);
            return new StorageReport(drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A drive that won't answer isn't a reason to fail the page that asked.
            return null;
        }
    }

    /// <summary>Bytes as a person reads them, matching what the interface shows.</summary>
    public static string Describe(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes / 1024d;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
