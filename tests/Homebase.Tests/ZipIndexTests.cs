using System.IO.Compression;
using System.Text;
using Homebase.Core;
using Homebase.Core.Providers;

namespace Homebase.Tests;

/// <summary>
/// Reading zips in place. They come from anybody, so what matters beyond reading them right is that
/// nothing in one is believed: not its sizes, not its paths, not that its bytes survived the download.
/// </summary>
public sealed class ZipIndexTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));

    public ZipIndexTests() => Directory.CreateDirectory(_temporary);

    public void Dispose()
    {
        try { Directory.Delete(_temporary, recursive: true); }
        catch (IOException) { }
    }

    private string Zip(Action<ZipArchive> fill, string name = "test.zip")
    {
        var path = Path.Combine(_temporary, name);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) fill(archive);
        return path;
    }

    private static void Add(ZipArchive archive, string name, byte[] contents, CompressionLevel level = CompressionLevel.Optimal,
        DateTimeOffset? modified = null)
    {
        var entry = archive.CreateEntry(name, level);
        if (modified is { } when) entry.LastWriteTime = when;
        using var stream = entry.Open();
        stream.Write(contents);
    }

    private static byte[] ReadAll(string zip, ZipItem item)
    {
        using var stream = ZipIndex.Open(zip, item);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static readonly byte[] Photo = Enumerable.Range(0, 70_000).Select(value => (byte)(value * 7 % 251)).ToArray();

    [Fact]
    public void Files_are_listed_and_read_back_whether_packed_or_stored()
    {
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Bring your files home. ", 500)));
        var zip = Zip(archive =>
        {
            Add(archive, "Photos/IMG_0001.JPG", Photo, CompressionLevel.NoCompression);
            Add(archive, "Notes/readme.txt", text);
            archive.CreateEntry("Empty folder/");
            Add(archive, "Empty.txt", []);
        });

        var contents = ZipIndex.Read(zip);
        Assert.Equal(0, contents.Unreadable);
        Assert.Equal(["Photos/IMG_0001.JPG", "Notes/readme.txt", "Empty.txt"], contents.Items.Select(item => item.Path));
        Assert.Equal(Photo, ReadAll(zip, contents.Items[0]));
        Assert.Equal(text, ReadAll(zip, contents.Items[1]));
        Assert.True(contents.Items[1].CompressedSize < text.Length);
        Assert.Empty(ReadAll(zip, contents.Items[2]));
        Assert.Equal(Photo.Length, contents.Items[0].Size);
    }

    [Fact]
    public void Checksums_agree_with_the_ones_zips_are_made_with()
    {
        var zip = Zip(archive =>
        {
            Add(archive, "a.bin", Photo);
            Add(archive, "b.bin", Photo[..13]);
        });
        using var archive = ZipFile.OpenRead(zip);
        var items = ZipIndex.Read(zip).Items;
        Assert.Equal(archive.Entries.Select(entry => entry.Crc32), items.Select(item => item.Crc));
    }

    [Fact]
    public void Names_in_any_script_and_either_kind_of_separator_come_through_composed()
    {
        // "é" written as e and a combining accent, as some Macs make names.
        var decomposed = "Fe\u0302te/Cafe\u0301.jpg";
        var zip = Zip(archive =>
        {
            Add(archive, decomposed, Photo[..10]);
            Add(archive, "\u7167\u7247\\\u4e8c\u3007\u4e00\u4e5d.heic", Photo[..10]);
        });
        Assert.Equal(["F\u00eate/Caf\u00e9.jpg", "\u7167\u7247/\u4e8c\u3007\u4e00\u4e5d.heic"], ZipIndex.Read(zip).Items.Select(item => item.Path));
    }

    [Fact]
    public void Paths_that_climb_out_and_links_are_passed_over_and_counted()
    {
        var zip = Zip(archive =>
        {
            Add(archive, "../../outside.txt", [1]);
            Add(archive, "Photos/../../outside.txt", [1]);
            var link = archive.CreateEntry("Photos/link");
            link.ExternalAttributes = unchecked((int)(0xA1FF_0000u));
            using (var stream = link.Open()) stream.Write("/etc/passwd"u8);
            Add(archive, "./Photos/./kept.jpg", [2]);
        });

        var contents = ZipIndex.Read(zip);
        Assert.Equal(3, contents.Unreadable);
        Assert.Equal("Photos/kept.jpg", Assert.Single(contents.Items).Path);
    }

    [Fact]
    public void A_damaged_file_says_so_instead_of_arriving_broken()
    {
        var zip = Zip(archive => Add(archive, "IMG_0001.JPG", Photo, CompressionLevel.NoCompression));
        var item = Assert.Single(ZipIndex.Read(zip).Items);
        // One byte in the middle of the photo changed, as a bad download might.
        using (var file = new FileStream(zip, FileMode.Open, FileAccess.ReadWrite))
        {
            var at = item.HeaderOffset + 30 + "IMG_0001.JPG".Length + 1000;
            file.Seek(at, SeekOrigin.Begin);
            var value = file.ReadByte();
            file.Seek(at, SeekOrigin.Begin);
            file.WriteByte((byte)(value ^ 0xFF));
        }

        var failure = Assert.Throws<LibraryException>(() => ReadAll(zip, item));
        Assert.Equal(ZipIndex.DamagedMessage, failure.Message);
    }

    [Fact]
    public void A_file_is_never_longer_than_the_zip_said()
    {
        var zip = Zip(archive => Add(archive, "big.txt", new byte[100_000]));
        var item = Assert.Single(ZipIndex.Read(zip).Items);
        // A table of contents that understates a file, as a zip made to fill a disk would.
        var lying = item with { Size = 10 };

        Assert.Throws<LibraryException>(() => ReadAll(zip, lying));
    }

    [Fact]
    public void A_part_cut_short_is_damaged_rather_than_read_as_much_as_there_is()
    {
        var zip = Zip(archive =>
        {
            Add(archive, "first.jpg", Photo, CompressionLevel.NoCompression);
            Add(archive, "second.jpg", Photo, CompressionLevel.NoCompression);
        });
        var items = ZipIndex.Read(zip).Items;
        var bytes = File.ReadAllBytes(zip);
        var cut = Path.Combine(_temporary, "cut.zip");
        File.WriteAllBytes(cut, bytes[..(int)(items[1].HeaderOffset + 500)]);

        // The table of contents is at the end, which is what's missing.
        Assert.Throws<InvalidDataException>(() => ZipIndex.Read(cut));
        // Read from the whole zip's contents, the second file runs off the end of what arrived.
        Assert.Throws<LibraryException>(() => ReadAll(cut, items[1]));
        Assert.Equal(Photo, ReadAll(cut, items[0]));
    }

    [Fact]
    public void Something_that_isnt_a_zip_is_refused()
    {
        var path = Path.Combine(_temporary, "notes.zip");
        File.WriteAllText(path, "These are not the photos you're looking for.");
        Assert.Throws<InvalidDataException>(() => ZipIndex.Read(path));
        using var head = File.OpenRead(path);
        Assert.False(ZipIndex.LooksLikeZip(head));
    }

    [Fact]
    public void A_zip_with_more_files_than_the_old_format_can_count_is_read_whole()
    {
        // Past 65,535 files a zip has to use its larger format, as a big photo library's will.
        const int count = 65_540;
        var zip = Zip(archive =>
        {
            for (var index = 0; index < count; index++) archive.CreateEntry($"Photos/{index}.jpg", CompressionLevel.NoCompression);
            Add(archive, "Photos/last.jpg", Photo[..100]);
        });

        var items = ZipIndex.Read(zip).Items;
        Assert.Equal(count + 1, items.Count);
        Assert.Equal(Photo[..100], ReadAll(zip, items[^1]));
    }

    [Fact]
    public void When_a_file_changed_comes_from_the_zip()
    {
        // Zips are made with the clock time of where they're made, as this one is.
        var when = new DateTimeOffset(new DateTime(2019, 7, 4, 15, 30, 10, DateTimeKind.Local));
        var zip = Zip(archive => Add(archive, "a.jpg", [1], modified: when));
        var modified = Assert.Single(ZipIndex.Read(zip).Items).Modified;
        Assert.NotNull(modified);
        // The ordinary field keeps two-second steps and no time zone; it has to be the same moment
        // to within that, wherever these tests run.
        Assert.True(Math.Abs((modified.Value - when).TotalSeconds) <= 2, $"{modified} isn't {when}");
    }
}
