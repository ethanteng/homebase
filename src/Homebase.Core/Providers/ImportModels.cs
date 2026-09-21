namespace Homebase.Core.Providers;

/// <summary>
/// A file Homebase copied out of a provider. Once imported, the copy in the library is the
/// one that counts: Homebase does not go back to the provider for it. The revision and hash
/// are kept as provenance — what was imported, from where, and when.
/// </summary>
public sealed record ImportedFile(
    string Provider,
    string RemotePath,
    string RemoteRev,
    string LocalPath,
    long Size,
    string ContentHash,
    DateTimeOffset ImportedAt);

public sealed record ImportedItem(string LocalPath, string RemotePath, long Size);

public sealed record SkippedItem(string RemotePath, string Reason);

public sealed record ImportResult(
    IReadOnlyList<ImportedItem> Imported,
    IReadOnlyList<SkippedItem> Skipped,
    long Bytes)
{
    public int ImportedCount => Imported.Count;
    public int SkippedCount => Skipped.Count;
}
