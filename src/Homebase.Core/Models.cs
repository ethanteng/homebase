namespace Homebase.Core;

public sealed record LibraryState(string? RootPath, string? Name);
public sealed record LibraryEntry(string Name, string Path, bool IsDirectory, long? Size, DateTimeOffset ModifiedAt);
public sealed record DirectoryListing(string Path, IReadOnlyList<LibraryEntry> Entries, int SkippedCount, DateTimeOffset IndexedAt);
/// <summary>Where one copied or moved item ended up, which is a different name when its own was taken.</summary>
public sealed record EditedEntry(string From, string To);
/// <summary>What a folder holds: the bytes and the number of files under it, however deep.</summary>
public sealed record FolderSize(long Bytes, int Files);
/// <summary>One file an upload is about to send, as the browser describes it beforehand.</summary>
/// <param name="Path">Where it sits in the upload: its name, or its place in a folder being sent.</param>
/// <param name="Modified">When it was last changed where it is, in milliseconds since 1970, when the browser says.</param>
public sealed record UploadFile(string? Path, long Bytes, long? Modified);
/// <summary>What of an upload is already where it's going, each file by its path in the upload. A file not named is new there.</summary>
/// <param name="Same">Already there, the same size and last changed at the same time: there's no need to send it.</param>
/// <param name="Changed">Already there, but this one is different and not older: sending it replaces the one there.</param>
/// <param name="Newer">Already there, and changed there since this one was: sending it would undo that.</param>
/// <param name="Folders">Folders at the top of the upload that are already there, for a merge to add to.</param>
public sealed record UploadComparison(
    IReadOnlyList<string> Same, IReadOnlyList<string> Changed, IReadOnlyList<string> Newer, IReadOnlyList<string> Folders);
/// <summary>An upload begun, or one of the same files picked up again where it stopped.</summary>
/// <param name="Id">What every file in it is sent under.</param>
/// <param name="Arrived">Which of the files about to be sent are in it already, from before, and needn't be sent again.</param>
public sealed record BegunUpload(string Id, IReadOnlyList<string> Arrived);
/// <summary>An upload nobody finished, and what of it arrived, kept until <paramref name="KeptUntil"/>.</summary>
/// <param name="Destination">Where it was going, or null for one begun before uploads said.</param>
/// <param name="Merge">Whether it was to bring what's there up to date, which is how it's to be finished.</param>
/// <param name="Names">What's at the top of it: the folder being added, usually.</param>
/// <param name="Arriving">Whether a file of it is arriving now, from a page still sending it.</param>
public sealed record UnfinishedUpload(
    string Id, string? Destination, bool Merge, IReadOnlyList<string> Names, int Files, long Bytes,
    DateTimeOffset LastArrived, DateTimeOffset KeptUntil, bool Arriving);
/// <summary>Where an upload went once it was put in place.</summary>
/// <param name="Items">Where each thing at the top of it went, and anything deeper a merge had to give a new name.</param>
/// <param name="Replaced">Files already there that a merge put something in place of, which are in the bin.</param>
public sealed record FinishedUpload(IReadOnlyList<EditedEntry> Items, int Replaced);
/// <summary>Something deleted from My files, kept until it is put back or its time runs out.</summary>
/// <param name="Path">Where it was, which is where putting it back returns it.</param>
public sealed record BinEntry(
    string Id, string Name, string Path, bool IsDirectory, long Size, DateTimeOffset DeletedAt, DateTimeOffset ExpiresAt);
public sealed record BinListing(IReadOnlyList<BinEntry> Entries, long Bytes);

public sealed class LibraryException(string message, string code = "invalid_path") : Exception(message)
{
    public string Code { get; } = code;
}
