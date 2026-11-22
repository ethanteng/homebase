using System.IO.Compression;
using System.Text;
using Homebase.Core;
using Homebase.Core.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homebase.Tests;

/// <summary>
/// Bringing everything home from the zips iCloud and Google export, neither of which can be signed in
/// to. What matters is that each photo arrives once, in the year it was taken and carrying that date
/// rather than the day of the export; that albums and folders survive; that what was deleted stays
/// apart; and that adding a part later brings only what's new, under names nothing has yet.
/// The layouts here are the ones real exports have, as tools that read them describe.
/// </summary>
public sealed class ExportTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly LibraryService _library;
    private readonly ImportLog _log = new();
    private readonly ImportService _imports;

    public ExportTests()
    {
        _root = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Library")).FullName);
        _library = new LibraryService(_root, new MetadataIndex());
        _library.Initialize();
        _imports = new ImportService(_library, _log, NullLogger<ImportService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, recursive: true); }
        catch (IOException) { }
    }

    private static byte[] Photo(string what) => Encoding.UTF8.GetBytes($"a photo of {what}, honestly");

    private void Zip(string folder, string name, params (string Path, byte[] Data)[] entries)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, folder)).FullName;
        using var archive = ZipFile.Open(Path.Combine(directory, name), ZipArchiveMode.Create);
        foreach (var (path, data) in entries)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
            using var stream = entry.Open();
            stream.Write(data);
        }
    }

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private ExportSource ICloud() => new(_root, _log, new AppleExport());
    private ExportSource Google() => new(_root, _log, new GoogleTakeout());

    private string Local(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static async Task<IReadOnlyList<string>> Names(IImportSource source, string folder) =>
        (await source.ListFolderAsync(folder, CancellationToken.None)).Select(entry => entry.Name).ToArray();

    private const string PhotoHeader = "imgName,fileChecksum,favorite,hidden,deleted,originalCreationDate,viewCount,importDate\n";

    private static string Row(string name, string date, bool deleted = false) =>
        $"{name},AcQTH38VUJ8ryqOCgiKuos3A5eCo,no,no,{(deleted ? "yes" : "no")},\"{date}\",0,\"Wednesday September 4,2013 9:10 PM GMT\"\n";

    [Fact]
    public async Task ICloud_photos_arrive_once_each_in_the_year_they_were_taken_dated_from_their_own_part()
    {
        // The same name in two parts is two different photos, and each part's spreadsheet is about its own.
        Zip(AppleExport.Folder, "iCloud Photos Part 1 of 2.zip",
            ("iCloud Photos Part 1 of 2/Photos/IMG_0001.HEIC", Photo("a beach")),
            ("iCloud Photos Part 1 of 2/Photos/IMG_0002.JPG", Photo("a cake")),
            ("iCloud Photos Part 1 of 2/Photos/Photo Details.csv", Text(PhotoHeader
                + Row("IMG_0001.HEIC", "Thursday August 29,2013 7:08 AM GMT")
                + Row("IMG_0002.JPG", "Saturday June 1,2019 1:23 PM GMT"))),
            ("iCloud Photos Part 1 of 2/Photos/Photo Details-1.csv", []));
        Zip(AppleExport.Folder, "iCloud Photos Part 2 of 2.zip",
            ("iCloud Photos Part 2 of 2/Photos/IMG_0001.HEIC", Photo("a mountain")),
            ("iCloud Photos Part 2 of 2/Photos/Photo Details.csv", Text(PhotoHeader
                + Row("IMG_0001.HEIC", "Monday March 4,2024 10:12 AM GMT")
                + Row("IMG_0009.JPG", "Friday May 1,2020 8:00 AM GMT", deleted: true))),
            ("iCloud Photos Part 2 of 2/Recently Deleted/IMG_0009.JPG", Photo("a mistake")),
            // Albums can be in a different part from their photos, and name them only by name.
            ("iCloud Photos Part 2 of 2/Albums/Birthdays.csv", Text("Images\nIMG_0002.JPG\n")),
            ("iCloud Photos Part 2 of 2/Memories/Summer.csv", Text("imageName\nIMG_0001.HEIC\n")),
            ("iCloud Photos Part 2 of 2/Photos/Shared Library Details.csv", Text("imgName,contributedByMe\nIMG_0001.HEIC,yes\n")));
        var source = ICloud();

        var top = await source.ListFolderAsync("/", CancellationToken.None);
        Assert.Equal(["Albums", "Photos", "Recently Deleted"], top.Select(entry => entry.Name));
        Assert.Equal("3 photos and videos", top.Single(entry => entry.Name == "Photos").Detail);
        Assert.Equal(["2013", "2019", "2024"], await Names(source, "/Photos"));

        var result = await _imports.ImportAsync(source, "/", CancellationToken.None);

        Assert.Empty(result.Skipped);
        Assert.Equal(Photo("a beach"), await File.ReadAllBytesAsync(Local("iCloud/Photos/2013/IMG_0001.HEIC")));
        Assert.Equal(Photo("a mountain"), await File.ReadAllBytesAsync(Local("iCloud/Photos/2024/IMG_0001.HEIC")));
        Assert.Equal(new DateTime(2013, 8, 29, 7, 8, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Local("iCloud/Photos/2013/IMG_0001.HEIC")));
        Assert.Equal(new DateTime(2019, 6, 1, 13, 23, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Local("iCloud/Photos/2019/IMG_0002.JPG")));
        // What was deleted is kept, but apart.
        Assert.Equal(Photo("a mistake"), await File.ReadAllBytesAsync(Local("iCloud/Recently Deleted/IMG_0009.JPG")));
        // An album is a page of the photos where they are, not a second copy of them.
        var page = await File.ReadAllTextAsync(Local("iCloud/Albums/Birthdays.html"));
        Assert.Contains("href=\"../Photos/2019/IMG_0002.JPG\"", page);
        Assert.Contains("script-src 'none'", page);
        // The spreadsheets are read, not brought home.
        Assert.Empty(Directory.GetFiles(Local("iCloud"), "*.csv", SearchOption.AllDirectories));
        Assert.Equal(5, result.Imported.Count);
    }

    [Fact]
    public async Task A_part_added_later_brings_only_what_is_new_under_a_name_nothing_has()
    {
        Zip(AppleExport.Folder, "iCloud Photos Part 1 of 2.zip",
            ("iCloud Photos Part 1 of 2/Photos/IMG_0001.HEIC", Photo("a beach")),
            ("iCloud Photos Part 1 of 2/Photos/Photo Details.csv", Text(PhotoHeader + Row("IMG_0001.HEIC", "Saturday June 1,2019 1:23 PM GMT"))));
        var source = ICloud();
        Assert.Equal("1 photo or video · part 2 of 2 not added yet",
            (await source.ListFolderAsync("/", CancellationToken.None)).Single().Detail);
        Assert.Single((await _imports.ImportAsync(source, "/", CancellationToken.None)).Imported);

        // Another photo with the same name, from the same year, in the part that was missing.
        Zip(AppleExport.Folder, "iCloud Photos Part 2 of 2.zip",
            ("iCloud Photos Part 2 of 2/Photos/IMG_0001.HEIC", Photo("a different beach")),
            ("iCloud Photos Part 2 of 2/Photos/Photo Details.csv", Text(PhotoHeader + Row("IMG_0001.HEIC", "Sunday June 2,2019 9:00 AM GMT"))));
        var again = await _imports.ImportAsync(ICloud(), "/", CancellationToken.None);

        var arrived = Assert.Single(again.Imported);
        Assert.Equal("iCloud/Photos/2019/IMG_0001 2.HEIC", arrived.LocalPath);
        Assert.Equal(Photo("a beach"), await File.ReadAllBytesAsync(Local("iCloud/Photos/2019/IMG_0001.HEIC")));
        Assert.Equal(Photo("a different beach"), await File.ReadAllBytesAsync(Local("iCloud/Photos/2019/IMG_0001 2.HEIC")));
        Assert.Equal(1, again.AlreadyHere);
    }

    [Fact]
    public async Task ICloud_Drive_keeps_its_folders_and_the_dates_its_spreadsheet_gives()
    {
        Zip(AppleExport.Folder, "iCloud Drive.zip",
            ("iCloud Drive/Drive/Taxes/2025 return.pdf", Text("%PDF-1.7 numbers")),
            ("iCloud Drive/Drive/Notes.txt", Text("Buy milk")),
            ("iCloud Drive/Drive/Drive Details.csv", Text(
                "Title, Base Hash, Type, Size, Created On, Modified On, Last Opened On, Favourite, Executable, Package Signature\n"
                + "2025 return.pdf,AAAA,File,16,03-01-2026 10:00:00,04-14-2026 21:30:05,,No,No,\n")));
        var source = ICloud();

        Assert.Equal(["iCloud Drive"], await Names(source, "/"));
        Assert.Equal(["Taxes", "Notes.txt"], await Names(source, "/iCloud Drive"));
        await _imports.ImportAsync(source, "/", CancellationToken.None);

        Assert.Equal("Buy milk", await File.ReadAllTextAsync(Local("iCloud/iCloud Drive/Notes.txt")));
        Assert.Equal(AppleExport.DriveDate("04-14-2026 21:30:05")!.Value.UtcDateTime,
            File.GetLastWriteTimeUtc(Local("iCloud/iCloud Drive/Taxes/2025 return.pdf")));
        Assert.False(File.Exists(Local("iCloud/iCloud Drive/Drive Details.csv")));
    }

    [Fact]
    public async Task Parts_in_another_language_are_known_by_what_is_in_them()
    {
        Zip(AppleExport.Folder, "iCloud-bilder Del 1 av 2.zip",
            ("iCloud-bilder Del 1 av 2/Photos/IMG_0100.HEIC", Photo("a fjord")),
            ("iCloud-bilder Del 1 av 2/Photos/Photo Details.csv", Text(PhotoHeader + Row("IMG_0100.HEIC", "Tuesday July 7,2015 6:00 PM GMT"))));

        var top = Assert.Single(await ICloud().ListFolderAsync("/", CancellationToken.None));
        Assert.Equal("Photos", top.Name);
        Assert.Equal("1 photo or video · part 2 of 2 not added yet", top.Detail);
    }

    [Fact]
    public async Task Spreadsheets_without_photos_say_why()
    {
        // With Advanced Data Protection on, Apple sends the spreadsheets and none of the photos.
        Zip(AppleExport.Folder, "iCloud Photos.zip",
            ("iCloud Photos/Photos/Photo Details.csv", Text(PhotoHeader + Row("IMG_0001.HEIC", "Saturday June 1,2019 1:23 PM GMT"))));

        var top = Assert.Single(await ICloud().ListFolderAsync("/", CancellationToken.None));
        Assert.Contains("Advanced Data Protection", top.Detail);
    }

    [Fact]
    public async Task A_zip_from_somewhere_else_is_shown_with_why_and_brings_nothing()
    {
        Zip(AppleExport.Folder, "takeout-20260406T202814Z-001.zip", ("Takeout/Drive/a.txt", Text("a")));
        var folder = Path.Combine(_root, AppleExport.Folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "half.zip"), "still downloading");
        var source = ICloud();

        var top = await source.ListFolderAsync("/", CancellationToken.None);
        Assert.Equal(["half.zip", "takeout-20260406T202814Z-001.zip"], top.Select(entry => entry.Name));
        Assert.Contains("Uncloud can’t open this zip file", top[0].Detail);
        Assert.Equal("This is from Google. Add it under Google instead.", top[1].Detail);
        Assert.Empty(await source.ListFolderAsync(top[1].Path, CancellationToken.None));
        var result = await _imports.ImportAsync(source, "/", CancellationToken.None);
        Assert.Empty(result.Imported);
    }

    [Theory]
    [InlineData("Thursday August 29,2013 7:08 AM GMT", "2013-08-29T07:08:00Z")]
    [InlineData("Saturday June 1,2019 12:05 PM GMT", "2019-06-01T12:05:00Z")]
    [InlineData("Monday March 4, 2024 12:30 AM UTC", "2024-03-04T00:30:00Z")]
    public void Apple_dates_are_read_as_written(string written, string meant) =>
        Assert.Equal(DateTimeOffset.Parse(meant), AppleExport.Date(written));

    private static byte[] Details(string title, DateTimeOffset? taken, bool trashed = false)
    {
        var time = taken is { } when ? $",\"photoTakenTime\":{{\"timestamp\":\"{when.ToUnixTimeSeconds()}\",\"formatted\":\"whenever\"}}" : "";
        return Text($"{{\"title\":\"{title}\",\"creationTime\":{{\"timestamp\":\"1697872351\"}}{time}{(trashed ? ",\"trashed\":true" : "")}}}");
    }

    private static readonly DateTimeOffset Christmas = new(2019, 12, 25, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Spring = new(2018, 4, 2, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Google_photos_arrive_once_dated_from_their_details_however_Takeout_named_them()
    {
        const string year = "Takeout/Google Photos/Photos from 2019/";
        const string longName = "A very long name for a photo from the Christmas morning.jpg";
        // The whole of a details file's name stops at 51 characters, cut from the end.
        var cut = (longName + ".supplemental-metadata")[..46] + ".json";
        Zip(GoogleTakeout.Folder, "takeout-20260406T202814Z-001.zip",
            (year + "IMG_1.jpg", Photo("a tree")),
            (year + "IMG_1.jpg.json", Details("IMG_1.jpg", Christmas)),
            (year + "IMG_2.jpg", Photo("presents")),
            (year + "IMG_2.jpg.supplemental-metadata.json", Details("IMG_2.jpg", Christmas)),
            // Two photos called IMG_2.jpg: Takeout numbers the second, and moves the number in its details.
            (year + "IMG_2(1).jpg", Photo("more presents")),
            (year + "IMG_2.jpg.supplemental-metadata(1).json", Details("IMG_2.jpg", Spring)),
            // An edited copy has no details of its own, and shares the original's.
            (year + "IMG_2-edited.jpg", Photo("presents, brighter")),
            (year + longName, Photo("a long morning")),
            (year + cut, Details(longName, Christmas)),
            // A live photo's video shares the photo's details.
            (year + "IMG_3.HEIC", Photo("a candle")),
            (year + "IMG_3.MP4", Photo("a flickering candle")),
            (year + "IMG_3.HEIC.supplemental-metadata.json", Details("IMG_3.HEIC", Christmas)),
            // Nothing says when this one was taken, but Google filed it under a year.
            ("Takeout/Google Photos/Photos from 2017/scan.jpg", Photo("an old print")),
            // The same photo is in the album's folder too, and arrives once.
            ("Takeout/Google Photos/Christmas - 2019/metadata.json", Text("{\"title\":\"Christmas / 2019\"}")),
            ("Takeout/Google Photos/Christmas - 2019/IMG_1.jpg", Photo("a tree")),
            ("Takeout/Google Photos/Christmas - 2019/IMG_1.jpg.json", Details("IMG_1.jpg", Christmas)),
            ("Takeout/Google Photos/Trash/IMG_9.jpg", Photo("a thumb")),
            ("Takeout/Google Photos/Trash/IMG_9.jpg.supplemental-metadata.json", Details("IMG_9.jpg", Spring, trashed: true)),
            ("Takeout/archive_browser.html", Text("<html></html>")));
        var source = Google();

        Assert.Equal(["Albums", "Photos", "Trash"], await Names(source, "/"));
        Assert.Equal(["2017", "2018", "2019"], await Names(source, "/Photos"));
        Assert.Equal(["IMG_2(1).jpg"], await Names(source, "/Photos/2018"));
        Assert.Equal(["A very long name for a photo from the Christmas morning.jpg", "IMG_1.jpg", "IMG_2-edited.jpg",
            "IMG_2.jpg", "IMG_3.HEIC", "IMG_3.MP4"], await Names(source, "/Photos/2019"));

        var result = await _imports.ImportAsync(source, "/", CancellationToken.None);

        Assert.Empty(result.Skipped);
        foreach (var name in new[] { "IMG_1.jpg", "IMG_2.jpg", "IMG_2-edited.jpg", "IMG_3.HEIC", "IMG_3.MP4", longName })
            Assert.Equal(Christmas.UtcDateTime, File.GetLastWriteTimeUtc(Local($"Google/Photos/2019/{name}")));
        Assert.Equal(Spring.UtcDateTime, File.GetLastWriteTimeUtc(Local("Google/Photos/2018/IMG_2(1).jpg")));
        Assert.Equal(Photo("an old print"), await File.ReadAllBytesAsync(Local("Google/Photos/2017/scan.jpg")));
        Assert.Equal(Photo("a thumb"), await File.ReadAllBytesAsync(Local("Google/Trash/IMG_9.jpg")));
        // The album's copy of the tree isn't brought a second time; its page shows the one there is.
        var page = await File.ReadAllTextAsync(Local("Google/Albums/Christmas 2019.html"));
        Assert.Contains("<h1>Christmas / 2019</h1>", page);
        Assert.Contains("href=\"../Photos/2019/IMG_1.jpg\"", page);
        Assert.False(Directory.Exists(Local("Google/Christmas - 2019")));
        Assert.Empty(Directory.GetFiles(Local("Google"), "*.json", SearchOption.AllDirectories));
        Assert.False(File.Exists(Local("Google/archive_browser.html")));
    }

    [Fact]
    public async Task An_album_with_only_details_in_its_folder_shows_the_photos_they_describe()
    {
        Zip(GoogleTakeout.Folder, "takeout-20260406T202814Z-001.zip",
            ("Takeout/Google Fotos/Fotos von 2020/IMG_5.jpg", Photo("a kite")),
            ("Takeout/Google Fotos/Fotos von 2020/IMG_5.jpg.supplemental-metadata.json", Details("IMG_5.jpg", new DateTimeOffset(2020, 3, 1, 0, 0, 0, TimeSpan.Zero))),
            ("Takeout/Google Fotos/Drachen/Metadaten.json", Text("{\"title\":\"Drachen\"}")),
            ("Takeout/Google Fotos/Drachen/IMG_5.jpg.supplemental-metadata.json", Details("IMG_5.jpg", new DateTimeOffset(2020, 3, 1, 0, 0, 0, TimeSpan.Zero))));
        var source = Google();

        Assert.Equal(["Albums", "Photos"], await Names(source, "/"));
        await _imports.ImportAsync(source, "/", CancellationToken.None);
        Assert.Contains("href=\"../Photos/2020/IMG_5.jpg\"", await File.ReadAllTextAsync(Local("Google/Albums/Drachen.html")));
    }

    [Fact]
    public async Task Drive_keeps_its_folders_and_dates_and_a_missing_part_is_noticed()
    {
        var changed = new DateTimeOffset(new DateTime(2025, 2, 3, 14, 15, 16, DateTimeKind.Local));
        var directory = Directory.CreateDirectory(Path.Combine(_root, GoogleTakeout.Folder)).FullName;
        using (var archive = ZipFile.Open(Path.Combine(directory, "takeout-20260406T202814Z-001.zip"), ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("Takeout/Drive/Taxes/2025 return.docx");
            entry.LastWriteTime = changed;
            using var stream = entry.Open();
            stream.Write(Text("numbers"));
        }
        Zip(GoogleTakeout.Folder, "takeout-20260406T202814Z-003.zip", ("Takeout/Drive/Recipes/Pad thai.pdf", Text("%PDF")));
        var source = Google();

        var drive = Assert.Single(await source.ListFolderAsync("/", CancellationToken.None));
        Assert.Equal("2 files · a part is missing", drive.Detail);
        await _imports.ImportAsync(source, "/", CancellationToken.None);

        Assert.Equal("numbers", await File.ReadAllTextAsync(Local("Google/Drive/Taxes/2025 return.docx")));
        Assert.True(Math.Abs((File.GetLastWriteTimeUtc(Local("Google/Drive/Taxes/2025 return.docx")) - changed.UtcDateTime).TotalSeconds) <= 2);
        Assert.True(File.Exists(Local("Google/Drive/Recipes/Pad thai.pdf")));
    }

    [Fact]
    public async Task A_damaged_photo_is_skipped_with_why_and_the_rest_still_arrive()
    {
        Zip(GoogleTakeout.Folder, "takeout-20260406T202814Z-001.zip",
            ("Takeout/Drive/good.txt", Text("fine")),
            ("Takeout/Drive/bad.txt", Text(new string('x', 5000))));
        var zip = Path.Combine(_root, GoogleTakeout.Folder, "takeout-20260406T202814Z-001.zip");
        var item = ZipIndex.Read(zip).Items.Single(candidate => candidate.Path.EndsWith("bad.txt", StringComparison.Ordinal));
        // Somewhere in the middle of its packed bytes, as a bad download would.
        using (var file = new FileStream(zip, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Seek(item.HeaderOffset + 30 + "Takeout/Drive/bad.txt".Length + item.CompressedSize / 2, SeekOrigin.Begin);
            file.WriteByte(0x5A);
            file.WriteByte(0xA5);
        }

        var result = await _imports.ImportAsync(Google(), "/", CancellationToken.None);

        Assert.Equal("Google/Drive/good.txt", Assert.Single(result.Imported).LocalPath);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("/Drive/bad.txt", skipped.RemotePath);
        Assert.Equal(ZipIndex.DamagedMessage, skipped.Reason);
        Assert.False(File.Exists(Local("Google/Drive/bad.txt")));
    }

    [Fact]
    public async Task An_album_page_gains_the_photos_a_later_part_brings_and_never_loses_any()
    {
        var day = new DateTimeOffset(2021, 7, 4, 12, 0, 0, TimeSpan.Zero);
        (string, byte[])[] Photo(string name, string what) =>
        [
            ($"Takeout/Google Photos/Photos from 2021/{name}", ExportTests.Photo(what)),
            ($"Takeout/Google Photos/Photos from 2021/{name}.supplemental-metadata.json", Details(name, day)),
            ($"Takeout/Google Photos/Picnic/{name}", ExportTests.Photo(what)),
            ($"Takeout/Google Photos/Picnic/{name}.supplemental-metadata.json", Details(name, day)),
        ];
        Zip(GoogleTakeout.Folder, "takeout-20260406T202814Z-001.zip",
            [("Takeout/Google Photos/Picnic/metadata.json", Text("{\"title\":\"Picnic\"}")), .. Photo("IMG_A.jpg", "a blanket")]);
        await _imports.ImportAsync(Google(), "/", CancellationToken.None);
        var page = Local("Google/Albums/Picnic.html");
        Assert.Contains("IMG_A.jpg", await File.ReadAllTextAsync(page));

        // The part with the album's other photo arrives later, and the page shows it too.
        Zip(GoogleTakeout.Folder, "takeout-20260406T202814Z-002.zip", Photo("IMG_B.jpg", "a basket"));
        var again = await _imports.ImportAsync(Google(), "/", CancellationToken.None);
        Assert.Contains(again.Imported, item => item.LocalPath == "Google/Albums/Picnic.html");
        var both = await File.ReadAllTextAsync(page);
        Assert.Contains("href=\"../Photos/2021/IMG_A.jpg\"", both);
        Assert.Contains("href=\"../Photos/2021/IMG_B.jpg\"", both);

        // With the first zips deleted, a later part still adds to the page rather than replacing it.
        File.Delete(Path.Combine(_root, GoogleTakeout.Folder, "takeout-20260406T202814Z-002.zip"));
        Zip(GoogleTakeout.Folder, "takeout-20260406T202814Z-003.zip", Photo("IMG_C.jpg", "a wasp"));
        await _imports.ImportAsync(Google(), "/", CancellationToken.None);
        var all = await File.ReadAllTextAsync(page);
        foreach (var name in new[] { "IMG_A.jpg", "IMG_B.jpg", "IMG_C.jpg" })
            Assert.Contains($"href=\"../Photos/2021/{name}\"", all);

        // A page somebody has changed is theirs, and is left alone.
        await File.WriteAllTextAsync(page, "My own notes about the picnic.");
        Zip(GoogleTakeout.Folder, "takeout-20260406T202814Z-004.zip", Photo("IMG_D.jpg", "a sunset"));
        var last = await _imports.ImportAsync(Google(), "/", CancellationToken.None);
        Assert.Equal("My own notes about the picnic.", await File.ReadAllTextAsync(page));
        Assert.Equal("Google/Photos/2021/IMG_D.jpg", Assert.Single(last.Imported).LocalPath);
    }

    [Fact]
    public async Task A_photo_taken_before_1970_keeps_its_year()
    {
        var then = new DateTimeOffset(1962, 5, 20, 15, 0, 0, TimeSpan.Zero);
        Zip(GoogleTakeout.Folder, "takeout-20260406T202814Z-001.zip",
            ("Takeout/Google Photos/Photos from 1962/grandma.jpg", Photo("a wedding")),
            // Uploaded much later, which is what creationTime says; when it was taken is below zero.
            ("Takeout/Google Photos/Photos from 1962/grandma.jpg.supplemental-metadata.json", Details("grandma.jpg", then)));

        await _imports.ImportAsync(Google(), "/", CancellationToken.None);

        Assert.Equal(then.UtcDateTime, File.GetLastWriteTimeUtc(Local("Google/Photos/1962/grandma.jpg")));
    }

    [Fact]
    public async Task Sizes_are_added_up_from_what_was_read_rather_than_walked()
    {
        Zip(GoogleTakeout.Folder, "takeout-20260406T202814Z-001.zip",
            ("Takeout/Drive/a/one.txt", Text("12345")),
            ("Takeout/Drive/a/b/two.txt", Text("1234567")));
        var source = Google();

        Assert.Equal(new FolderSize(12, 2), await source.SizeAsync("/Drive", CancellationToken.None));
        Assert.Equal(new FolderSize(7, 1), await source.SizeAsync("/Drive/a/b", CancellationToken.None));
    }
}
