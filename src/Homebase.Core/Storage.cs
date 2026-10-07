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

    /// <summary>
    /// The most room to leave alone on the drive. Filling a disk to the last byte breaks far more
    /// than whatever filled it — the metadata index lives on the same disk and needs somewhere to write.
    /// </summary>
    public const long Headroom = 256L * 1024 * 1024;

    /// <summary>
    /// Whether this much can be written without running the drive down to nothing. The room held
    /// back shrinks with the space left, so a drive that is already tight still takes a small file
    /// rather than refusing everything on principle.
    /// </summary>
    public static bool Fits(long needed, long? free, long headroom = Headroom) =>
        needed == 0 || Room(free, headroom) is not { } room || needed <= room;

    /// <summary>
    /// How much can be written before the drive is run down to the room held back, or null when the
    /// drive won't say how much is free.
    /// </summary>
    public static long? Room(long? free, long headroom = Headroom) =>
        free is { } bytes ? bytes - Math.Min(headroom, bytes / 10) : null;

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
