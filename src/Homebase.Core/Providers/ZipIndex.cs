using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Homebase.Core.Providers;

/// <summary>One file in a zip, as the zip's own table of contents describes it.</summary>
/// <param name="Path">
/// Where it is in the zip, made safe to think of as a path: forward slashes, no empty, "." or ".."
/// parts, composed Unicode. Nothing is ever written to it; it only says which file this is.
/// </param>
/// <param name="HeaderOffset">Where in the zip the file's own header is, which its contents follow.</param>
/// <param name="Modified">When the zip says the file last changed, if it says.</param>
public sealed record ZipItem(
    string Path,
    long Size,
    long CompressedSize,
    int Method,
    uint Crc,
    long HeaderOffset,
    DateTimeOffset? Modified,
    bool Encrypted);

/// <summary>What one zip holds, and how many entries in it couldn't be taken as files.</summary>
public sealed record ZipContents(IReadOnlyList<ZipItem> Items, int Unreadable);

/// <summary>
/// Reads zips in place, without unpacking them. An export from iCloud or Google arrives as zips of up
/// to 50 GB each, and unpacking one first would need room for it twice over: this reads the zip's
/// table of contents, which is at its end, and then each file's bytes straight from where they lie.
///
/// A zip may have come from anywhere, so nothing in it is believed without checking: every offset
/// and length is held to the file, a file's contents stop at the size the zip gave for it — which is
/// what the room check counted — and are checked against the zip's own checksum on the way out, so
/// a part that was cut short or damaged in the download says so rather than arriving as a broken
/// photo. Paths that climb out of the zip, links, and folders are not files and are passed over.
/// </summary>
public static class ZipIndex
{
    private const uint LocalHeader = 0x04034b50;
    private const uint CentralHeader = 0x02014b50;
    private const uint EndOfDirectory = 0x06054b50;
    private const uint Zip64Locator = 0x07064b50;
    private const uint Zip64End = 0x06064b50;

    public const string DamagedMessage =
        "This file is damaged. Download its zip file again, then add it again.";

    /// <summary>Whether a file starts the way a zip does.</summary>
    public static bool LooksLikeZip(Stream head)
    {
        Span<byte> start = stackalloc byte[4];
        var read = 0;
        while (read < start.Length)
        {
            var got = head.Read(start[read..]);
            if (got == 0) return false;
            read += got;
        }
        var signature = BinaryPrimitives.ReadUInt32LittleEndian(start);
        return signature is LocalHeader or EndOfDirectory;
    }

    /// <summary>Reads a zip's table of contents. Throws <see cref="InvalidDataException"/> if it isn't a zip Uncloud can read.</summary>
    public static ZipContents Read(string path)
    {
        PathPolicy.RejectLink(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1 << 16, FileOptions.RandomAccess);
        var length = file.Length;
        if (length < 22) throw new InvalidDataException("Not a zip.");

        // The end record is the last thing in a zip, give or take a comment of up to 64 KB; the
        // record that points at a large zip's real end comes just before it.
        var tailLength = (int)Math.Min(length, 22 + 0xFFFF + 20);
        var tail = new byte[tailLength];
        file.Seek(length - tailLength, SeekOrigin.Begin);
        file.ReadExactly(tail);
        var at = -1;
        for (var index = tailLength - 22; index >= 0; index--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) == EndOfDirectory
                && index + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20)) <= tailLength)
            {
                at = index;
                break;
            }
        if (at < 0) throw new InvalidDataException("Not a zip, or the end of it is missing.");

        var end = tail.AsSpan(at);
        long disk = U16(end, 4), directoryDisk = U16(end, 6);
        long count = U16(end, 10), directorySize = U32(end, 12), directoryOffset = U32(end, 16);
        var directoryEnd = length - tailLength + at;
        if (count == 0xFFFF || directorySize == 0xFFFFFFFF || directoryOffset == 0xFFFFFFFF || disk == 0xFFFF)
        {
            if (at < 20) throw new InvalidDataException("A large zip whose end is missing.");
            var locator = tail.AsSpan(at - 20);
            if (U32(locator, 0) != Zip64Locator) throw new InvalidDataException("A large zip whose end is missing.");
            if (U32(locator, 16) > 1) throw new InvalidDataException("A zip split across several files.");
            var recordAt = (long)U64(locator, 8);
            if (recordAt < 0 || recordAt + 56 > directoryEnd) throw new InvalidDataException("A large zip whose end is damaged.");
            var record = new byte[56];
            file.Seek(recordAt, SeekOrigin.Begin);
            file.ReadExactly(record);
            if (U32(record, 0) != Zip64End) throw new InvalidDataException("A large zip whose end is damaged.");
            disk = U32(record, 16);
            directoryDisk = U32(record, 20);
            count = (long)U64(record, 32);
            directorySize = (long)U64(record, 40);
            directoryOffset = (long)U64(record, 48);
            directoryEnd = recordAt;
        }
        if (disk != 0 || directoryDisk != 0) throw new InvalidDataException("A zip split across several files.");
        if (directoryOffset < 0 || directorySize < 0 || count < 0
            || directoryOffset + directorySize > directoryEnd || count > directorySize / 46)
            throw new InvalidDataException("A zip whose table of contents is damaged.");

        file.Seek(directoryOffset, SeekOrigin.Begin);
        using var reader = new BufferedStream(file, 1 << 16);
        var fixedPart = new byte[46];
        var items = new List<ZipItem>((int)Math.Min(count, 1 << 20));
        var unreadable = 0;
        long consumed = 0;
        for (long entry = 0; entry < count; entry++)
        {
            reader.ReadExactly(fixedPart);
            if (U32(fixedPart, 0) != CentralHeader) throw new InvalidDataException("A zip whose table of contents is damaged.");
            var flags = U16(fixedPart, 8);
            var method = U16(fixedPart, 10);
            var time = U16(fixedPart, 12);
            var date = U16(fixedPart, 14);
            var crc = U32(fixedPart, 16);
            long compressed = U32(fixedPart, 20), size = U32(fixedPart, 24);
            var nameLength = U16(fixedPart, 28);
            var extraLength = U16(fixedPart, 30);
            var commentLength = U16(fixedPart, 32);
            long diskStart = U16(fixedPart, 34);
            var external = U32(fixedPart, 38);
            long offset = U32(fixedPart, 42);
            var variable = new byte[nameLength + extraLength + commentLength];
            reader.ReadExactly(variable);
            consumed += fixedPart.Length + variable.Length;
            if (consumed > directorySize) throw new InvalidDataException("A zip whose table of contents is damaged.");

            var nameBytes = variable.AsSpan(0, nameLength);
            var extra = variable.AsSpan(nameLength, extraLength);
            var modified = Dos(date, time);
            string? unicodeName = null;
            for (var position = 0; position + 4 <= extra.Length;)
            {
                var id = U16(extra, position);
                var size16 = U16(extra, position + 2);
                if (position + 4 + size16 > extra.Length) break;
                var data = extra.Slice(position + 4, size16);
                switch (id)
                {
                    // Sizes and offsets too big for the ordinary fields, in this order, for those that are.
                    case 0x0001:
                    {
                        var read = 0;
                        if (size == 0xFFFFFFFF && read + 8 <= data.Length) { size = (long)U64(data, read); read += 8; }
                        if (compressed == 0xFFFFFFFF && read + 8 <= data.Length) { compressed = (long)U64(data, read); read += 8; }
                        if (offset == 0xFFFFFFFF && read + 8 <= data.Length) { offset = (long)U64(data, read); read += 8; }
                        if (diskStart == 0xFFFF && read + 4 <= data.Length) diskStart = U32(data, read);
                        break;
                    }
                    // When it last changed, in UTC to the second, which the ordinary field can't say.
                    case 0x5455 when data.Length >= 5 && (data[0] & 1) != 0:
                        modified = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt32LittleEndian(data[1..]));
                        break;
                    // Its name in Unicode, for a zip whose ordinary name field is in an old code page.
                    case 0x7075 when data.Length >= 5 && data[0] == 1 && U32(data, 1) == Crc32.Append(0, nameBytes):
                        unicodeName = StrictUtf8(data[5..]);
                        break;
                }
                position += 4 + size16;
            }

            var name = unicodeName ?? Name(nameBytes, (flags & 0x800) != 0);
            if (name.EndsWith('/') || name.EndsWith('\\')) continue;
            var mode = external >> 16;
            if (Safe(name) is not { } safe || (mode & 0xF000) == 0xA000 || diskStart != 0
                || size < 0 || compressed < 0 || offset < 0 || offset >= directoryOffset
                || (method == 0 && size != compressed && (flags & 1) == 0))
            {
                unreadable++;
                continue;
            }
            items.Add(new ZipItem(safe, size, compressed, method, crc, offset, modified, (flags & 1) != 0));
        }
        return new ZipContents(items, unreadable);
    }

    /// <summary>
    /// A file's contents, unpacked as they're read. They stop at the size the zip gave, and a file
    /// that turns out longer, shorter, or not what its checksum says fails at the end rather than
    /// arriving damaged.
    /// </summary>
    public static Stream Open(string path, ZipItem item)
    {
        if (item.Encrypted)
            throw new LibraryException("This file has a password, so Uncloud can’t open it.", "unsupported");
        if (item.Method is not (0 or 8))
            throw new LibraryException("Uncloud can’t open this file yet.", "unsupported");
        long start;
        PathPolicy.RejectLink(path);
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096,
                   FileOptions.RandomAccess))
        {
            var header = new byte[30];
            if (item.HeaderOffset + header.Length > file.Length) throw Damaged();
            file.Seek(item.HeaderOffset, SeekOrigin.Begin);
            file.ReadExactly(header);
            if (U32(header, 0) != LocalHeader) throw Damaged();
            start = item.HeaderOffset + header.Length + U16(header, 26) + U16(header, 28);
            if (start + item.CompressedSize > file.Length) throw Damaged();
        }
        var window = new FileWindow(path, start, item.CompressedSize);
        try
        {
            Stream inner = item.Method == 0 ? window : new DeflateStream(window, CompressionMode.Decompress);
            return new CheckedStream(inner, item.Size, item.Crc);
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads a whole small file out of a zip, such as a photo's details, or null if it is bigger
    /// than <paramref name="limit"/> or can't be read: details are a help, never a reason to stop.
    /// </summary>
    public static byte[]? ReadSmall(string path, ZipItem item, int limit)
    {
        if (item.Size > limit) return null;
        try
        {
            using var stream = Open(path, item);
            var bytes = new byte[item.Size];
            stream.ReadExactly(bytes);
            // Read to the end, so the checksum is looked at too.
            if (stream.ReadByte() != -1) return null;
            return bytes;
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or LibraryException)
        {
            return null;
        }
    }

    private static LibraryException Damaged() => new(DamagedMessage, "invalid");

    /// <summary>
    /// A path from a zip as parts that can't reach anywhere: separators either way, empty and "."
    /// parts dropped, and null for anything with ".." or a character no file can be called.
    /// </summary>
    internal static string? Safe(string name)
    {
        var parts = new List<string>();
        foreach (var part in name.Normalize(NormalizationForm.FormC).Split('/', '\\'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == ".." || part.Contains('\0')) return null;
            parts.Add(part);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static string Name(ReadOnlySpan<byte> bytes, bool utf8)
    {
        if (utf8) return Encoding.UTF8.GetString(bytes);
        // Most zips made today are UTF-8 whether or not they say so; the rest are in the code page
        // the zip format started with.
        return StrictUtf8(bytes) ?? CodePagesEncodingProvider.Instance.GetEncoding(437)!.GetString(bytes);
    }

    private static string? StrictUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>The date and time in a zip's ordinary field, which has no time zone and so is taken as this computer's.</summary>
    private static DateTimeOffset? Dos(int date, int time)
    {
        int year = 1980 + (date >> 9), month = (date >> 5) & 0xF, day = date & 0x1F;
        int hour = time >> 11, minute = (time >> 5) & 0x3F, second = (time & 0x1F) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return null;
        return new DateTimeOffset(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local));
    }

    private static int U16(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);

    /// <summary>
    /// A file's contents held to what the zip said about them: never more bytes than its size, and a
    /// failure at the end if they were fewer or the checksum is wrong.
    /// </summary>
    private sealed class CheckedStream(Stream inner, long size, uint crc) : Stream
    {
        private readonly long _size = size;
        private long _left = size;
        private uint _crc;
        private bool _checked;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int got;
            try
            {
                if (_left == 0) return Finish(inner.Read(stackalloc byte[1]));
                got = inner.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
            }
            catch (InvalidDataException)
            {
                throw Damaged();
            }
            return Took(buffer[..got]);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int got;
            try
            {
                if (_left == 0) return Finish(await inner.ReadAsync(new byte[1], cancellationToken));
                got = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], cancellationToken);
            }
            catch (InvalidDataException)
            {
                throw Damaged();
            }
            return Took(buffer.Span[..got]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Took(ReadOnlySpan<byte> got)
        {
            if (got.Length == 0) throw Damaged();
            _crc = Crc32.Append(_crc, got);
            _left -= got.Length;
            return got.Length;
        }

        /// <summary>Called once the size has been read: anything more is a file that isn't what the zip says.</summary>
        private int Finish(int more)
        {
            if (more != 0 || (!_checked && _crc != crc)) throw Damaged();
            _checked = true;
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _size;
        public override long Position { get => _size - _left; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>The checksum zips use, eight bytes at a time.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[8 * 256];
        for (uint value = 0; value < 256; value++)
        {
            var crc = value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            table[value] = crc;
        }
        for (var value = 0; value < 256; value++)
            for (var slice = 1; slice < 8; slice++)
            {
                var previous = table[(slice - 1) * 256 + value];
                table[slice * 256 + value] = (previous >> 8) ^ table[previous & 0xFF];
            }
        return table;
    }

    /// <summary>The checksum of what came before, <paramref name="crc"/>, carried on over <paramref name="data"/>.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var table = Table;
        crc = ~crc;
        while (data.Length >= 8)
        {
            var one = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
            var two = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            crc = table[7 * 256 + (one & 0xFF)] ^ table[6 * 256 + ((one >> 8) & 0xFF)]
                  ^ table[5 * 256 + ((one >> 16) & 0xFF)] ^ table[4 * 256 + (one >> 24)]
                  ^ table[3 * 256 + (two & 0xFF)] ^ table[2 * 256 + ((two >> 8) & 0xFF)]
                  ^ table[1 * 256 + ((two >> 16) & 0xFF)] ^ table[two >> 24];
            data = data[8..];
        }
        foreach (var value in data) crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
