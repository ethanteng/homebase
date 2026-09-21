namespace Homebase.Core.Providers;

public enum SyncState
{
    // The local copy matches the revision Homebase last downloaded.
    Current,
    // Dropbox reports a newer revision than the local copy.
    RemoteChanged,
    // The local copy was edited after Homebase wrote it, so Homebase will not overwrite it.
    LocalEdited,
    // The local copy is gone.
    LocalMissing
}

/// <summary>
/// One file Homebase mirrors from a provider. <paramref name="LocalSize"/> and
/// <paramref name="LocalModifiedAt"/> record what Homebase itself wrote, which is how a later
/// local edit is detected and protected rather than silently overwritten.
/// </summary>
public sealed record SyncedFile(
    string Provider,
    string RemotePath,
    string RemoteRev,
    string LocalPath,
    long Size,
    long LocalSize,
    DateTimeOffset LocalModifiedAt,
    DateTimeOffset SyncedAt);

public sealed record SyncedFileStatus(SyncedFile File, SyncState State, string? RemoteRev);

public sealed record SyncOutcome(string LocalPath, SyncState State, bool Downloaded, string? Detail);
