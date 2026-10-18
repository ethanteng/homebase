using Homebase.Core.Backup;

namespace Homebase.Core;

/// <summary>
/// Files coming back from a backup. restic restores them out of sight in the account's own
/// <c>.homebase</c> folder, on the same drive, and from there they're put in place the way the bin
/// puts things back: where they were, the folders above them made again if they've gone, and a
/// name taken there since kept, the one coming back becoming “name 2”. Nothing is overwritten,
/// so restoring an older copy of something never costs the newer one.
/// </summary>
public sealed partial class LibraryService
{
    /// <summary>A folder of its own for one restore to arrive in.</summary>
    public string RestoreFolder()
    {
        var restoring = Path.Combine(Path.GetDirectoryName(PathPolicy.PrepareMetadata(root))!, "restoring");
        PathPolicy.RejectLink(restoring);
        var folder = Path.Combine(restoring, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return folder;
    }

    /// <summary>Refuses a restore there isn't room for, before anything is fetched.</summary>
    public void NeedRoomForRestore(long bytes) => NeedRoomFor(bytes, "restore less at once");

    /// <summary>Puts what was restored in place, and says where each went.</summary>
    public async Task<IReadOnlyList<EditedEntry>> PutBackFromBackupAsync(IReadOnlyList<RestoredItem> items, CancellationToken cancellationToken)
    {
        await _edits.WaitAsync(cancellationToken);
        try
        {
            var placed = items.Select(item => (Item: item, Folder: Path.GetDirectoryName(PathPolicy.Resolve(root, item.Path))!)).ToArray();
            // Everything is looked at before anything is moved, so a restore either puts all of it
            // back or none of it, and trying again never makes a second copy of what came first.
            foreach (var (item, folder) in placed)
            {
                AwayFromImport([], folder, "restored");
                var name = Path.GetFileName(item.Path);
                if (item.IsDirectory ? !Directory.Exists(item.Restored) : !File.Exists(item.Restored))
                    throw new LibraryException($"“{name}” didn’t come back from the backup. Try again in a minute.", "unavailable");
                for (var above = folder; above.Length > root.Length; above = Path.GetDirectoryName(above)!)
                    if (File.Exists(above))
                        throw new LibraryException(
                            $"“{name}” was in a folder called “{Path.GetFileName(above)}”, and there’s a file by that name "
                            + "there now. Move or rename the file, then try again.", "conflict");
            }

            var restored = new List<EditedEntry>();
            foreach (var (item, folder) in placed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(folder);
                var placedAs = Place(item.Restored, folder, Path.GetFileName(item.Path), item.IsDirectory, copy: false);
                restored.Add(new EditedEntry(item.Path, Join(folder, placedAs)));
            }
            return restored;
        }
        finally { _edits.Release(); }
    }

    /// <summary>
    /// Takes away whatever of a restore is left in its folder: anything that couldn't be put in
    /// place, or all of it if the restore stopped. restic gives back read-only files and folders as
    /// they were, which deleting would otherwise refuse.
    /// </summary>
    public static void ForgetRestoreFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        PathPolicy.RejectLink(folder);
        Backup.BackupService.MakeDeletable(folder);
        Directory.Delete(folder, recursive: true);
    }
}
