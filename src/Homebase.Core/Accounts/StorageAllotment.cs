namespace Homebase.Core.Accounts;

/// <summary>How an administrator measures out Uncloud's share of the drive.</summary>
public enum AllotmentUnit
{
    /// <summary>A set number of bytes, however much else is on the drive.</summary>
    Bytes,
    /// <summary>
    /// A share of the room Uncloud could have: what is free on the drive plus what Uncloud already
    /// holds. Counting its own files in is what keeps the share the same size as Uncloud fills it.
    /// </summary>
    Percent
}

/// <summary>How much of the drive Uncloud may fill.</summary>
public sealed record Allotment(AllotmentUnit Unit, long Amount)
{
    /// <summary>
    /// The allotment a request asks for, or null for none, which leaves Uncloud the whole drive.
    /// </summary>
    public static Allotment? From(long? bytes, int? percent) => (bytes, percent) switch
    {
        (null, null) => null,
        ({ }, { }) => throw new LibraryException("Choose a set amount or a share of the drive, not both.", "invalid"),
        (_, < 1 or > 100) => throw new LibraryException("A share of the drive has to be between 1% and 100%.", "invalid"),
        (_, { } share) => new Allotment(AllotmentUnit.Percent, share),
        (<= 0, _) => throw new LibraryException("Give Uncloud more than nothing.", "invalid"),
        ({ } amount, _) => new Allotment(AllotmentUnit.Bytes, amount)
    };

    internal string Stored => $"{Unit.ToString().ToLowerInvariant()}:{Amount}";

    /// <summary>One stored before; anything unreadable is read as none rather than as a reason to refuse every write.</summary>
    internal static Allotment? Parse(string? stored)
    {
        var parts = stored?.Split(':', 2);
        if (parts is not [var unit, var amount] || !long.TryParse(amount, out var value)) return null;
        try
        {
            return unit switch
            {
                "bytes" => From(value, null),
                "percent" when value is >= 1 and <= 100 => From(null, (int)value),
                _ => null
            };
        }
        catch (LibraryException) { return null; }
    }
}

/// <summary>
/// How much of the drive an administrator lets Uncloud have. Every check before something is
/// written asks <see cref="Space"/>, which answers as though Uncloud's share were the whole drive,
/// so a copy, an upload and an import all stop at the allotment for the same reasons and in the
/// same words as they stop at a full drive. Without an allotment it is the drive, as it always was.
/// Syncthing writes what computers send without asking first, so it is told <see cref="KeepFree"/>
/// instead: how much of the drive to leave free, which comes to the same place.
///
/// What Uncloud holds is measured by walking everybody's folders, which is far too slow to do for
/// every megabyte of an upload. So it is walked now and then, and in between whatever the drive has
/// gained or lost since is put down to Uncloud: its own writes are what move it most, and a walk
/// soon puts right whatever something else on the computer did meanwhile.
/// </summary>
public sealed class StorageAllotment(ControlDatabase database, HostService host)
{
    private const string SettingKey = "storage_allotment";

    /// <summary>How full the drive is; replaced in tests.</summary>
    public Func<string, StorageReport?> Drive { get; init; } = Storage.For;

    /// <summary>How much Uncloud holds under the host's folder; replaced in tests.</summary>
    public Func<string, long> Holding { get; init; } = root =>
        UsageService.Walk(Path.Combine(root, UserPaths.UsersDirectory));

    /// <summary>
    /// How long a walk stands before another is started, in the background. Long, because between
    /// walks the drive itself says how much Uncloud has written; all a walk adds is what something
    /// else on the computer wrote or deleted.
    /// </summary>
    public TimeSpan Freshness { get; init; } = TimeSpan.FromMinutes(5);

    private sealed record Measurement(string Root, long Used, long? Free, DateTimeOffset At);

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _walking = new(1, 1);
    private bool _read;
    private Allotment? _allotment;
    private Measurement? _measured;

    public Allotment? Current
    {
        get
        {
            lock (_lock)
            {
                if (!_read)
                {
                    _allotment = Allotment.Parse(database.Setting(SettingKey));
                    _read = true;
                }
                return _allotment;
            }
        }
    }

    /// <summary>
    /// Sets the allotment, or takes it away with null. One below what Uncloud already holds is
    /// allowed: nothing is deleted, and nothing more can be added until it is back under.
    /// </summary>
    public void Set(Allotment? allotment)
    {
        lock (_lock)
        {
            if (allotment is null)
            {
                using var connection = database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM host_settings WHERE key = $key";
                command.Parameters.AddWithValue("$key", SettingKey);
                command.ExecuteNonQuery();
            }
            else database.SetSetting(SettingKey, allotment.Stored);
            _allotment = allotment;
            _read = true;
        }
    }

    /// <summary>
    /// The room Uncloud has to write into at <paramref name="path"/>, as though its share were the
    /// drive, or null when the drive won't say. <see cref="StorageReport.Capped"/> says the
    /// allotment, not the drive, is what is running out.
    /// </summary>
    public StorageReport? Space(string path)
    {
        var drive = Drive(path);
        if (drive is null || Current is not { } allotment) return drive;
        // Only inside the host's folder. A workspace still finishing an import that began before the
        // folder moved is writing somewhere this allotment was never measured against.
        if (host.RootPath is not { } root || !Within(path, root)) return drive;
        return Apply(allotment, Used(root, drive.FreeBytes), drive);
    }

    /// <summary>
    /// Forgets what Uncloud was measured to hold, so the next answer walks again first. For files
    /// moved in from beside the folders it measures: a rename leaves the drive's free space where
    /// it was, so nothing between walks would ever count them.
    /// </summary>
    public void Invalidate()
    {
        lock (_lock) _measured = null;
    }

    /// <summary>How much Uncloud holds under the host's folder, as near as can be said right now.</summary>
    public long Held(string root) => Used(root, Drive(root)?.FreeBytes);

    /// <summary>
    /// The drive as Uncloud's share of it. When the drive would run out first, the allotment
    /// changes nothing and the drive is reported as it is.
    /// </summary>
    internal static StorageReport Apply(Allotment allotment, long used, StorageReport drive)
    {
        var possible = used + drive.FreeBytes;
        var allotted = Allotted(allotment, possible);
        return allotted >= possible
            ? drive
            : new StorageReport(Math.Max(0, allotted - used), allotted, Capped: true);
    }

    /// <summary>
    /// How much free space the drive has to keep for Uncloud to stay within its share, or null when
    /// nothing is allotted or the drive would run out first. Syncthing is told this, because it
    /// writes what computers send without asking Uncloud: it stops bringing a file in when that
    /// would take the drive below it. Uncloud's own writes don't move it, since every byte Uncloud
    /// adds is a byte less free; only something else on the computer, or a new allotment, does.
    /// </summary>
    public long? KeepFree()
    {
        if (Current is not { } allotment || host.RootPath is not { } root || Drive(root) is not { } drive) return null;
        return KeepFree(allotment, Used(root, drive.FreeBytes), drive);
    }

    internal static long? KeepFree(Allotment allotment, long used, StorageReport drive)
    {
        var possible = used + drive.FreeBytes;
        var allotted = Allotted(allotment, possible);
        // Never less than Syncthing keeps free of its own accord, a hundredth of the drive.
        return allotted >= possible ? null : Math.Max(possible - allotted, drive.TotalBytes / 100);
    }

    private static long Allotted(Allotment allotment, long possible) =>
        allotment.Unit == AllotmentUnit.Percent ? possible * allotment.Amount / 100 : allotment.Amount;

    private long Used(string root, long? free)
    {
        var measured = Measured(root);
        // Free space the drive has lost since the walk is taken to be Uncloud's, and free space it
        // has gained to be Uncloud's deleting. Only something else on the computer makes that wrong,
        // and only until the next walk.
        return free is { } now && measured.Free is { } then ? Math.Max(0, measured.Used + then - now) : measured.Used;
    }

    private Measurement Measured(string root)
    {
        var measured = Latest(root);
        if (measured is null)
        {
            // The first answer has nothing to go on, so it waits for a walk. Only one walks; anybody
            // else asking meanwhile has the same answer when it's done.
            _walking.Wait();
            try { return Latest(root) ?? Walk(root); }
            finally { _walking.Release(); }
        }
        if (DateTimeOffset.UtcNow - measured.At >= Freshness && _walking.Wait(0))
            _ = Task.Run(() =>
            {
                try { Walk(root); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // The last walk stands until one succeeds.
                }
                finally { _walking.Release(); }
            });
        return measured;
    }

    private Measurement? Latest(string root)
    {
        lock (_lock) return _measured is { } measured && measured.Root == root ? measured : null;
    }

    private Measurement Walk(string root)
    {
        // The drive is asked before the walk, not after: whatever arrives while it walks is then
        // counted twice rather than not at all, which costs a little room for a while, not the drive.
        var free = Drive(root)?.FreeBytes;
        var measurement = new Measurement(root, Holding(root), free, DateTimeOffset.UtcNow);
        lock (_lock) _measured = measurement;
        return measurement;
    }

    private static bool Within(string path, string root) =>
        path == root || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
