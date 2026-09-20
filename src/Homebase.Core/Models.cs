namespace Homebase.Core;

public sealed record LibraryState(string? RootPath, string? Name);
public sealed record LibraryEntry(string Name, string Path, bool IsDirectory, long? Size, DateTimeOffset ModifiedAt);
public sealed record DirectoryListing(string Path, IReadOnlyList<LibraryEntry> Entries, int SkippedCount, DateTimeOffset IndexedAt);

public sealed class LibraryException(string message, string code = "invalid_path") : Exception(message)
{
    public string Code { get; } = code;
}
