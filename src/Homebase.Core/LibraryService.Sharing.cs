using System.IO.Compression;

namespace Homebase.Core;

/// <summary>
/// What sharing by link needs from one account's files: which of them can be shared, what each is
/// now, and a folder of them as one zip. Everything goes through the same path policy as browsing,
/// so a link can only ever lead to what its owner could open themselves.
/// </summary>
public sealed partial class LibraryService
{
    /// <summary>
    /// The files and folders a request names, as they're kept for a link: each one there, inside My
    /// files and not the whole of it, and something inside another of them left out, since the other
    /// already shares it.
    /// </summary>
    public IReadOnlyList<string> ShareablePaths(IReadOnlyList<string>? paths) =>
        Items(paths).Select(item => item.Relative).ToArray();

    /// <summary>What is at this path in My files now, or null when nothing is, or nothing may be.</summary>
    public LibraryEntry? Find(string path)
    {
        try
        {
            var fullPath = PathPolicy.Resolve(root, path);
            var relative = Relative(fullPath);
            if (relative.Length == 0) return null;
            if (Directory.Exists(fullPath))
                return new LibraryEntry(Path.GetFileName(fullPath), relative, true, null, Directory.GetLastWriteTimeUtc(fullPath));
            if (File.Exists(fullPath))
            {
                var file = new FileInfo(fullPath);
                return new LibraryEntry(file.Name, relative, false, file.Length, file.LastWriteTimeUtc);
            }
            return null;
        }
        catch (Exception error) when (error is LibraryException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // What's already squeezed gains nothing from being squeezed again, and a slow host has better things to do.
    private static readonly HashSet<string> Compressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".heic", ".heif", ".mp4", ".m4v", ".mov",
        ".webm", ".mkv", ".avi", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".zip", ".gz", ".7z",
        ".rar", ".bz2", ".xz", ".dmg", ".pdf", ".docx", ".xlsx", ".pptx", ".pages", ".numbers", ".key"
    };

    /// <summary>
    /// Writes these as one zip, each under its name: a file as itself, a folder with everything in it.
    /// What browsing leaves out — hidden entries and links — is left out here too. The zip is written as
    /// it's read, so nothing is gathered on the drive first, however big it is. A file that can't be
    /// read partway through is left out rather than ending the rest.
    /// </summary>
    /// <param name="items">Under what name each goes in the zip, and where it is in My files.</param>
    public async Task ZipAsync(IReadOnlyList<(string Name, string Path)> items, Stream output, CancellationToken cancellationToken)
    {
        var resolved = items.Select(item => (item.Name, FullPath: PathPolicy.Resolve(root, item.Path))).ToArray();
        var deferred = new DeferredWrites(output);
        await using (var zip = await ZipArchive.CreateAsync(deferred, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken))
        {
            foreach (var (name, fullPath) in resolved)
            {
                if (Directory.Exists(fullPath)) await AddFolderAsync(zip, new DirectoryInfo(fullPath), name, cancellationToken);
                else if (File.Exists(fullPath)) await AddFileAsync(zip, new FileInfo(fullPath), name, cancellationToken);
            }
        }
        // The zip's own directory, at the end, is among what it writes without waiting.
        await deferred.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// A zip being written to somebody's browser. ZipArchive still writes a few small things without
    /// waiting — each file's sizes after it, and the directory at the end — and a web server won't let
    /// anything block on the network, so those are held here and sent ahead of the next write that
    /// can wait. The files themselves are written by waiting, so only those few bytes are ever held.
    /// </summary>
    private sealed class DeferredWrites(Stream inner) : Stream
    {
        private readonly MemoryStream _held = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => _held.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _held.Write(buffer);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await SendHeldAsync(cancellationToken);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await SendHeldAsync(cancellationToken);
            await inner.FlushAsync(cancellationToken);
        }

        // Nothing can be sent without waiting, so it's kept for the next FlushAsync.
        public override void Flush() { }

        private async Task SendHeldAsync(CancellationToken cancellationToken)
        {
            if (_held.Length == 0) return;
            await inner.WriteAsync(_held.GetBuffer().AsMemory(0, (int)_held.Length), cancellationToken);
            _held.SetLength(0);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static async Task AddFolderAsync(ZipArchive zip, DirectoryInfo folder, string name, CancellationToken cancellationToken)
    {
        var any = false;
        foreach (var entry in folder.EnumerateFileSystemInfos("*", Countable).OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Unseen(entry)) continue;
            any = true;
            if (entry is DirectoryInfo inner) await AddFolderAsync(zip, inner, $"{name}/{inner.Name}", cancellationToken);
            else await AddFileAsync(zip, (FileInfo)entry, $"{name}/{entry.Name}", cancellationToken);
        }
        // An empty folder is still something that was shared.
        if (!any) zip.CreateEntry($"{name}/").LastWriteTime = ZipTime(folder.LastWriteTimeUtc);
    }

    private static async Task AddFileAsync(ZipArchive zip, FileInfo file, string name, CancellationToken cancellationToken)
    {
        FileStream source;
        try
        {
            source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }
        await using (source)
        {
            var entry = zip.CreateEntry(name,
                Compressed.Contains(file.Extension) ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
            entry.LastWriteTime = ZipTime(file.LastWriteTimeUtc);
            await using var into = await entry.OpenAsync(cancellationToken);
            await source.CopyToAsync(into, cancellationToken);
        }
    }

    /// <summary>A zip can only say dates from 1980 to 2107.</summary>
    private static DateTimeOffset ZipTime(DateTime utc)
    {
        var earliest = new DateTime(1980, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var latest = new DateTime(2107, 12, 30, 0, 0, 0, DateTimeKind.Utc);
        return new DateTimeOffset(utc < earliest ? earliest : utc > latest ? latest : utc);
    }
}
