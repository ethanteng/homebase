using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Homebase.Core.Providers;

/// <summary>A file attached to a note, as the export file describes it.</summary>
/// <param name="Hash">The MD5 of its contents, in lowercase hex: what the note's text uses to say where it goes.</param>
public sealed record EnexResource(string? FileName, string Mime, long Size, string Hash, string? SourceUrl);

/// <summary>A to-do Evernote keeps beside a note rather than in its text.</summary>
public sealed record EnexTask(string Title, bool Done);

/// <summary>Why a note couldn't all be read.</summary>
public enum EnexProblem
{
    /// <summary>The export file stops partway through it: an export still being written when it was copied.</summary>
    CutOff,
    /// <summary>Something in it isn't what an export should hold.</summary>
    Unreadable,
}

/// <summary>
/// One note, read out of an export file. <paramref name="Content"/> is Evernote's own markup for the
/// note's text, still to be made into a page; <paramref name="Problem"/> says why the note couldn't all
/// be read, when it couldn't, in which case everything else is whatever could be made out.
/// </summary>
public sealed record EnexNote(
    string Title,
    DateTimeOffset? Created,
    DateTimeOffset? Updated,
    IReadOnlyList<string> Tags,
    string? Author,
    string? SourceUrl,
    string Content,
    IReadOnlyList<EnexResource> Resources,
    IReadOnlyList<EnexTask> Tasks,
    EnexProblem? Problem = null);

/// <summary>
/// Reads Evernote's export files (.enex). One holds a notebook, or the notes somebody picked, as a
/// single XML document with every attachment written out in it as base64 — so a large notebook is a
/// file of gigabytes, and reading the whole of it to get at one note would make bringing a notebook
/// home take as long as reading it once per note.
///
/// Instead the file is gone through once for where each note starts and ends, and a note is then
/// read on its own, from its own bytes. A note's start and end are found by looking for the literal
/// <c>&lt;note&gt;</c> and <c>&lt;/note&gt;</c>, which is safe because nothing else in the file can
/// contain them: a note's text is XML in which a "&lt;" is always escaped, attachments are base64,
/// and every other field is escaped text.
///
/// Nothing here follows a DTD or resolves an entity from outside the file. The export names
/// Evernote's DTD by URL, and fetching it — or anything else a crafted file names — would let a file
/// somebody was sent reach out from this computer.
/// </summary>
public static class EvernoteExport
{
    private static ReadOnlySpan<byte> NoteStart => "<note>"u8;
    private static ReadOnlySpan<byte> NoteEnd => "</note>"u8;

    /// <summary>Where one note sits in an export file, in bytes. An unfinished one runs to the end.</summary>
    public readonly record struct Span(long Start, long Length, bool Unfinished);

    internal static XmlReaderSettings Settings => new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        ConformanceLevel = ConformanceLevel.Fragment,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        // Evernote has been known to write characters XML doesn't allow; a note is still worth having.
        CheckCharacters = false,
        CloseInput = true,
        Async = false,
    };

    /// <summary>Whether a file starts the way an Evernote export does, judged from its first few kilobytes.</summary>
    public static bool LooksLikeExport(Stream stream)
    {
        var buffer = new byte[8192];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return buffer.AsSpan(0, read).IndexOf("<en-export"u8) >= 0;
    }

    /// <summary>
    /// Where every note in an export file is. One the file stops partway through — an export that was
    /// still being written when it was copied — is included, marked, so it can be counted and said.
    /// </summary>
    public static IReadOnlyList<Span> FindNotes(Stream stream, CancellationToken cancellationToken)
    {
        var found = new List<Span>();
        var buffer = new byte[1 << 20];
        var carried = 0;
        long offset = 0;
        long start = -1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, carried, buffer.Length - carried);
            if (read == 0) break;
            var length = carried + read;
            var at = 0;
            while (true)
            {
                var marker = start < 0 ? NoteStart : NoteEnd;
                var hit = buffer.AsSpan(at, length - at).IndexOf(marker);
                if (hit < 0) break;
                var position = offset + at + hit;
                if (start < 0) start = position;
                else
                {
                    found.Add(new Span(start, position + marker.Length - start, false));
                    start = -1;
                }
                at += hit + marker.Length;
            }
            // A marker can be split across two reads, so the last few bytes not yet looked past are
            // looked at again with the next read. Fewer than a marker's length, so nothing found is
            // ever found twice.
            var keep = Math.Min(NoteEnd.Length - 1, length - at);
            Array.Copy(buffer, length - keep, buffer, 0, keep);
            offset += length - keep;
            carried = keep;
        }
        if (start >= 0) found.Add(new Span(start, offset + carried - start, true));
        return found;
    }

    /// <summary>
    /// Reads one note from its own bytes. The attachments are gone through for their size and hash,
    /// which is what the note's text refers to them by, but not kept.
    /// </summary>
    public static EnexNote ReadNote(Stream note, bool unfinished, CancellationToken cancellationToken)
    {
        var title = "";
        DateTimeOffset? created = null, updated = null;
        var tags = new List<string>();
        string? author = null, sourceUrl = null;
        var content = "";
        var resources = new List<EnexResource>();
        var tasks = new List<EnexTask>();
        try
        {
            using var reader = XmlReader.Create(note, Settings);
            if (!reader.ReadToFollowing("note")) throw new XmlException("There’s no note here.");
            EachChild(reader, name =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (name)
                {
                    case "title": title = Text(reader); break;
                    case "content": content = Text(reader); break;
                    case "created": created = Date(Text(reader)); break;
                    case "updated": updated = Date(Text(reader)); break;
                    case "tag":
                        var tag = Text(reader).Trim();
                        if (tag.Length > 0) tags.Add(tag);
                        break;
                    case "note-attributes":
                        EachChild(reader, attribute =>
                        {
                            if (attribute == "author") author = Text(reader).Trim();
                            else if (attribute == "source-url") sourceUrl = Text(reader).Trim();
                            else reader.Skip();
                        });
                        break;
                    case "resource": resources.Add(ReadResource(reader, cancellationToken)); break;
                    case "task":
                        var task = ReadTask(reader);
                        if (task is not null) tasks.Add(task);
                        break;
                    default: reader.Skip(); break;
                }
            });
            return new EnexNote(title.Trim(), created, updated, tags, Blank(author), Blank(sourceUrl), content, resources,
                tasks, unfinished ? EnexProblem.CutOff : null);
        }
        catch (Exception failure) when (failure is XmlException or DecoderFallbackException or FormatException)
        {
            return new EnexNote(title.Trim(), created, updated, tags, Blank(author), Blank(sourceUrl), content, resources,
                tasks, unfinished ? EnexProblem.CutOff : EnexProblem.Unreadable);
        }
    }

    /// <summary>
    /// The contents of one attachment, the <paramref name="index"/>th in the note, decoded as it is
    /// read rather than all at once: an attachment can be a video far larger than memory should hold.
    /// The stream owns <paramref name="note"/> and closes it.
    /// </summary>
    public static Stream OpenResource(Stream note, int index)
    {
        var reader = XmlReader.Create(note, Settings);
        try
        {
            if (!reader.ReadToFollowing("note")) throw new XmlException("There’s no note here.");
            var seen = -1;
            var depth = reader.Depth;
            reader.Read();
            while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1)
                {
                    reader.Read();
                    continue;
                }
                if (reader.LocalName == "resource" && ++seen == index) return DataOf(reader);
                // Leaves the reader on whatever follows, which may be the next attachment.
                reader.Skip();
            }
            throw new LibraryException("This attachment is no longer in the exported file.", "not_found");
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private static Stream DataOf(XmlReader reader)
    {
        var depth = reader.Depth;
        while (reader.Read() && reader.Depth > depth)
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1 && reader.LocalName == "data")
                return new Base64Element(reader);
        throw new LibraryException("This attachment is empty in the exported file.", "not_found");
    }

    private static EnexResource ReadResource(XmlReader reader, CancellationToken cancellationToken)
    {
        long size = 0;
        var hash = "";
        var mime = "application/octet-stream";
        string? fileName = null, sourceUrl = null;
        EachChild(reader, name =>
        {
            switch (name)
            {
                case "data":
                    using (var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
                    {
                        var buffer = new byte[1 << 16];
                        int read;
                        while ((read = reader.ReadElementContentAsBase64(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            md5.AppendData(buffer, 0, read);
                            size += read;
                        }
                        hash = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
                    }
                    break;
                case "mime": mime = Text(reader).Trim(); break;
                case "resource-attributes":
                    EachChild(reader, attribute =>
                    {
                        if (attribute == "file-name") fileName = Text(reader).Trim();
                        else if (attribute == "source-url") sourceUrl = Text(reader).Trim();
                        else reader.Skip();
                    });
                    break;
                default: reader.Skip(); break;
            }
        });
        return new EnexResource(Blank(fileName), mime.Length == 0 ? "application/octet-stream" : mime, size, hash, Blank(sourceUrl));
    }

    private static EnexTask? ReadTask(XmlReader reader)
    {
        var title = "";
        var status = "";
        EachChild(reader, name =>
        {
            if (name == "title") title = Text(reader).Trim();
            else if (name == "taskStatus") status = Text(reader).Trim();
            else reader.Skip();
        });
        return title.Length == 0 ? null : new EnexTask(title, status.Equals("completed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Goes through the elements directly inside the one the reader is on, leaving it just past that
    /// element's end. Each child must be read or skipped by <paramref name="child"/>.
    /// </summary>
    private static void EachChild(XmlReader reader, Action<string> child)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return;
        }
        var depth = reader.Depth;
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1) child(reader.LocalName);
            else reader.Read();
        }
        if (!reader.EOF) reader.Read();
    }

    /// <summary>All the text inside the element the reader is on, leaving it just past the element.</summary>
    private static string Text(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return "";
        }
        var depth = reader.Depth;
        var text = new StringBuilder();
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace
                or XmlNodeType.SignificantWhitespace)
                text.Append(reader.Value);
        if (!reader.EOF) reader.Read();
        return text.ToString();
    }

    /// <summary>Evernote writes dates as 20190301T120000Z, always in UTC.</summary>
    internal static DateTimeOffset? Date(string text) =>
        DateTimeOffset.TryParseExact(text.Trim(), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>
    /// The bytes inside one base64 element, decoded as they're read. Owns the reader, and so the file
    /// under it, and closes both.
    /// </summary>
    private sealed class Base64Element(XmlReader reader) : Stream
    {
        private bool _finished;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_finished || count == 0) return 0;
            var read = reader.ReadElementContentAsBase64(buffer, offset, count);
            if (read == 0) _finished = true;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) reader.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>A window onto part of a file: one note's bytes, read as though they were the whole of it.</summary>
internal sealed class FileWindow : Stream
{
    private readonly FileStream _file;
    private readonly long _end;

    public FileWindow(string path, long start, long length)
    {
        // Listed only when it wasn't a link; looked at again in case it has become one since.
        PathPolicy.RejectLink(path);
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        _file.Seek(start, SeekOrigin.Begin);
        _end = Math.Min(start + length, _file.Length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var left = _end - _file.Position;
        if (left <= 0) return 0;
        return _file.Read(buffer, offset, (int)Math.Min(count, left));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _file.Dispose();
        base.Dispose(disposing);
    }
}
