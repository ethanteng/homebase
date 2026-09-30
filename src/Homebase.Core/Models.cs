namespace Homebase.Core;

public sealed record LibraryState(string? RootPath, string? Name);
public sealed record LibraryEntry(string Name, string Path, bool IsDirectory, long? Size, DateTimeOffset ModifiedAt);
public sealed record DirectoryListing(string Path, IReadOnlyList<LibraryEntry> Entries, int SkippedCount, DateTimeOffset IndexedAt);
/// <summary>Where one copied or moved item ended up, which is a different name when its own was taken.</summary>
public sealed record EditedEntry(string From, string To);
/// <summary>What a folder holds: the bytes and the number of files under it, however deep.</summary>
public sealed record FolderSize(long Bytes, int Files);
/// <summary>Something deleted from My files, kept until it is put back or its time runs out.</summary>
/// <param name="Path">Where it was, which is where putting it back returns it.</param>
public sealed record BinEntry(
    string Id, string Name, string Path, bool IsDirectory, long Size, DateTimeOffset DeletedAt, DateTimeOffset ExpiresAt);
public sealed record BinListing(IReadOnlyList<BinEntry> Entries, long Bytes);

public sealed class LibraryException(string message, string code = "invalid_path") : Exception(message)
{
    public string Code { get; } = code;
}
