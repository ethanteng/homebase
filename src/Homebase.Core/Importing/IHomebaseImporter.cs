namespace Homebase.Core.Importing;

// Future adapters write ordinary files into their destination, then the normal browser indexes them.
// Implementations must validate destinations, avoid overwrites, and preserve source IDs in a manifest.
// No provider authentication, network access, or importer implementation ships in v0.
public interface IHomebaseImporter
{
    string ProviderId { get; }
    Task<ImportResult> ImportAsync(ImportRequest request, IProgress<ImportProgress>? progress, CancellationToken cancellationToken);
}

public sealed record ImportRequest(string SourcePath, string DestinationDirectory);
public sealed record ImportProgress(int FilesWritten, string? CurrentFile);
public sealed record ImportResult(int FilesWritten, IReadOnlyList<string> Warnings);
