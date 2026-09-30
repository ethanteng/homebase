using Microsoft.Extensions.Logging;

namespace Homebase.Core.Providers;

public enum ImportStage
{
    Measuring,
    Bringing,
    Done,
    Stopped,
    Failed
}

/// <summary>
/// One import, watched from outside. A folder takes as long as it takes, so the work runs on its
/// own rather than inside the request that asked for it: the panel reads this as it goes, and a
/// reload finds the import still running instead of forgetting it.
/// </summary>
public sealed record ImportJob(
    string Id,
    /// <summary>Which place this came from, so the panel can show the right one on a reload.</summary>
    string SourceId,
    string RemotePath,
    string Label,
    ImportStage Stage,
    int TotalFiles,
    int CompletedFiles,
    long Bytes,
    string? CurrentFile,
    ImportResult? Result,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt)
{
    public bool Running => Stage is ImportStage.Measuring or ImportStage.Bringing;
}

/// <summary>Runs one import at a time, so there is always something to show and something to stop.</summary>
public sealed class ImportJobs(ImportService imports, ILogger<ImportJobs> logger)
{
    private readonly Lock _lock = new();
    private ImportJob? _job;
    private CancellationTokenSource? _cancellation;

    public ImportJob? Current
    {
        get { lock (_lock) return _job; }
    }

    public ImportJob Start(IImportSource source, string sourceId, string remotePath, string? label)
    {
        // Checked here so "choose a folder first" answers the request instead of surfacing later
        // as a job that failed for a reason the caller could have been told immediately.
        imports.RequireLibrary();
        CancellationTokenSource cancellation;
        ImportJob job;
        lock (_lock)
        {
            if (_job is { Running: true })
                throw new LibraryException(
                    "Uncloud is already bringing files home. Let that finish, or stop it first.", "busy");
            cancellation = new CancellationTokenSource();
            job = new ImportJob(Guid.NewGuid().ToString("N"), sourceId, remotePath, Name(label, remotePath),
                ImportStage.Measuring, 0, 0, 0, null, null, null, DateTimeOffset.UtcNow, null);
            _cancellation = cancellation;
            _job = job;
        }
        // Deliberately not awaited: the request that started this answers straight away, and the
        // import must not be tied to a connection that closes the moment it does.
        _ = Task.Run(() => RunAsync(job.Id, source, remotePath, cancellation));
        return job;
    }

    public ImportJob? Cancel()
    {
        lock (_lock)
        {
            if (_job is { Running: true }) _cancellation?.Cancel();
            return _job;
        }
    }

    private async Task RunAsync(string id, IImportSource source, string remotePath, CancellationTokenSource cancellation)
    {
        var progress = new Relay(update => Update(id, job => job with
        {
            Stage = ImportStage.Bringing,
            TotalFiles = update.TotalFiles,
            CompletedFiles = update.CompletedFiles,
            Bytes = update.Bytes,
            CurrentFile = update.CurrentFile
        }));
        try
        {
            var result = await imports.ImportAsync(source, remotePath, progress, cancellation.Token);
            Update(id, job => job with
            {
                Stage = ImportStage.Done,
                Result = result,
                CompletedFiles = job.TotalFiles,
                CurrentFile = null,
                FinishedAt = DateTimeOffset.UtcNow
            });
        }
        catch (OperationCanceledException)
        {
            // Whatever arrived before this is recorded and stays; only the rest was dropped.
            Update(id, job => job with
            {
                Stage = ImportStage.Stopped, CurrentFile = null, FinishedAt = DateTimeOffset.UtcNow
            });
        }
        catch (Exception failure)
        {
            logger.LogWarning(failure, "Bringing {RemotePath} home failed", remotePath);
            Update(id, job => job with
            {
                Stage = ImportStage.Failed,
                // Only Uncloud's own wording reaches a person; anything else goes to the log.
                Error = failure is LibraryException
                    ? failure.Message
                    : "Uncloud couldn’t bring these files home.",
                CurrentFile = null,
                FinishedAt = DateTimeOffset.UtcNow
            });
        }
        finally
        {
            lock (_lock)
            {
                if (_cancellation == cancellation) _cancellation = null;
            }
            cancellation.Dispose();
        }
    }

    /// <summary>Only ever changes the job it was started for, so a later import can't be overwritten.</summary>
    private void Update(string id, Func<ImportJob, ImportJob> change)
    {
        lock (_lock)
        {
            if (_job?.Id == id) _job = change(_job);
        }
    }

    private static string Name(string? label, string remotePath)
    {
        if (!string.IsNullOrWhiteSpace(label)) return label;
        var path = remotePath.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        // The whole of a place has no last path segment to be named after, and "Bringing  home"
        // reads as a bug rather than as an import of everything.
        return name.Length > 0 ? name : "everything";
    }

    private sealed class Relay(Action<ImportProgress> report) : IProgress<ImportProgress>
    {
        // Synchronous on purpose: Progress<T> posts to the thread pool, which can deliver updates
        // out of order and show a count going backwards.
        public void Report(ImportProgress value) => report(value);
    }
}
