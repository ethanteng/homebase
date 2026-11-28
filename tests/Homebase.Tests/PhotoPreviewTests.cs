using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Accounts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homebase.Tests;

/// <summary>
/// Photos browsers can't draw — HEIC, which is what iPhones take — shown anyway, as a JPEG the host
/// makes of each the first time it's looked at. A stand-in does the converting here, so the suite
/// answers the same with or without sips; the one test of sips itself runs only on a Mac.
/// </summary>
public sealed class PhotoPreviewTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;

    public PhotoPreviewTests()
    {
        Directory.CreateDirectory(_temporary);
        _host = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Writes "JPEG of" and what it was given, and counts how often it's asked.</summary>
    private sealed class StandIn : IImageConverter
    {
        public int Made;
        public int LongestSide;
        public bool Fails;
        /// <summary>Held until a test lets it go, to ask for the same photo twice while it's being made.</summary>
        public TaskCompletionSource? Hold;

        public bool Available => true;

        public async Task ConvertAsync(string source, string destination, int longestSide, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Made);
            LongestSide = longestSide;
            if (Hold is not null) await Hold.Task.WaitAsync(cancellationToken);
            if (Fails) throw new IOException("Not a picture.");
            await File.WriteAllTextAsync(destination, "JPEG of " + await File.ReadAllTextAsync(source, cancellationToken), cancellationToken);
        }
    }

    private async Task<(TestHost App, HttpClient Owner, string Root)> StartAsync(IImageConverter? images)
    {
        var app = new TestHost(_config, images: images);
        var owner = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(owner, _host);
        var root = await TestHost.UserRootAsync(owner);
        Directory.CreateDirectory(Path.Combine(root, "Photos"));
        await File.WriteAllTextAsync(Path.Combine(root, "Photos", "IMG_0001.HEIC"), "first photo");
        return (app, owner, root);
    }

    [Fact]
    public async Task A_HEIC_photo_is_shown_as_a_JPEG_made_once_and_made_again_when_it_changes()
    {
        var converter = new StandIn();
        var (app, owner, root) = await StartAsync(converter);
        using var _ = app;
        using var __ = owner;

        var shown = await owner.GetAsync("/api/files/file?path=Photos/IMG_0001.HEIC");
        shown.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", shown.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", shown.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("IMG_0001.jpg", shown.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Contains("sandbox", shown.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("JPEG of first photo", await shown.Content.ReadAsStringAsync());
        Assert.Equal(PhotoPreviews.LongestSide, converter.LongestSide);

        // Looked at again, it's the one already made.
        Assert.Equal("JPEG of first photo", await owner.GetStringAsync("/api/files/file?path=Photos/IMG_0001.HEIC"));
        Assert.Equal(1, converter.Made);
        // It's kept out of sight, with the account's other workings.
        var kept = Assert.Single(Directory.GetFiles(Path.Combine(root, ".homebase", PhotoPreviews.FolderName)));
        Assert.EndsWith(".jpg", kept);
        var listing = await owner.GetFromJsonAsync<JsonElement>("/api/files");
        Assert.DoesNotContain(listing.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("name").GetString() == ".homebase");

        // Downloading still gives the photo itself, untouched.
        Assert.Equal("first photo", await owner.GetStringAsync("/api/files/download?path=Photos/IMG_0001.HEIC"));

        // Changed in place, it's made again rather than shown as it was.
        await File.WriteAllTextAsync(Path.Combine(root, "Photos", "IMG_0001.HEIC"), "the photo, edited");
        Assert.Equal("JPEG of the photo, edited", await owner.GetStringAsync("/api/files/file?path=Photos/IMG_0001.HEIC"));
        Assert.Equal(2, converter.Made);
    }

    [Fact]
    public async Task Asked_for_twice_while_it_is_being_made_it_is_made_once()
    {
        var converter = new StandIn { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (app, owner, _) = await StartAsync(converter);
        using var __ = app;
        using var ___ = owner;

        // Its details and the viewer, opened together.
        var details = owner.GetStringAsync("/api/files/file?path=Photos/IMG_0001.HEIC");
        var viewer = owner.GetStringAsync("/api/files/file?path=Photos/IMG_0001.HEIC");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref converter.Made) == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await Task.Delay(200);
        converter.Hold.SetResult();

        Assert.Equal(["JPEG of first photo", "JPEG of first photo"], await Task.WhenAll(details, viewer));
        Assert.Equal(1, converter.Made);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_a_JPEG_the_photo_is_sent_as_it_is_for_a_browser_that_draws_it(bool converterFails)
    {
        // A host with nothing to convert with (Linux), or whose converter couldn't read the photo.
        var (app, owner, _) = await StartAsync(converterFails ? new StandIn { Fails = true } : null);
        using var __ = app;
        using var ___ = owner;
        var shown = await owner.GetAsync("/api/files/file?path=Photos/IMG_0001.HEIC");
        shown.EnsureSuccessStatusCode();
        Assert.Equal("image/heic", shown.Content.Headers.ContentType?.MediaType);
        Assert.Equal("first photo", await shown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Photos_shared_by_link_are_shown_as_JPEGs_too()
    {
        var converter = new StandIn();
        var (app, owner, _) = await StartAsync(converter);
        using var __ = app;
        using var ___ = owner;
        var response = await owner.PostAsJsonAsync("/api/shares", new { paths = new[] { "Photos" }, canDownload = false });
        response.EnsureSuccessStatusCode();
        var url = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        var token = url[(url.IndexOf("/s/", StringComparison.Ordinal) + 3)..];

        using var visitor = app.CreateClient();
        var shown = await visitor.GetAsync($"/api/shared/{token}/file?path=Photos/IMG_0001.HEIC");
        shown.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", shown.Content.Headers.ContentType?.MediaType);
        Assert.Equal("JPEG of first photo", await shown.Content.ReadAsStringAsync());
    }

    [Fact]
    public void The_JPEGs_made_dont_count_as_space_the_account_uses()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temporary, "Account")).FullName;
        File.WriteAllBytes(Path.Combine(root, "photo.heic"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(root, ".homebase", PhotoPreviews.FolderName)).FullName, "a.jpg"), new byte[500]);
        File.WriteAllBytes(Path.Combine(root, ".homebase", "index.db"), new byte[20]);
        Assert.Equal(1020, new UsageService().UsedBytes(root));
    }

    [Fact]
    public async Task Past_their_budget_the_ones_looked_at_longest_ago_are_thrown_away()
    {
        var root = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Account")).FullName);
        var previews = new PhotoPreviews(new StandIn(), NullLogger<PhotoPreviews>.Instance) { Budget = 100, TidyEvery = TimeSpan.Zero };
        var shown = new List<string>();
        for (var number = 1; number <= 6; number++)
        {
            var photo = Path.Combine(root, $"IMG_{number}.HEIC");
            // Each JPEG the stand-in writes is 27 bytes: "JPEG of " and these 19.
            await File.WriteAllTextAsync(photo, $"photo number {number} here");
            await using var stream = File.OpenRead(photo);
            var made = await previews.JpegForAsync(root, stream, CancellationToken.None);
            // The one just made is never among those thrown away.
            Assert.NotNull(made);
            Assert.True(File.Exists(made));
            shown.Add(made);
            // Looked at in the order they were made, a minute apart.
            for (var index = 0; index < shown.Count; index++)
                if (File.Exists(shown[index])) File.SetLastWriteTimeUtc(shown[index], DateTime.UtcNow.AddMinutes(index - 10));
            // The fourth takes them to 108 bytes against a budget of 100: the oldest go until no
            // more than 75 are left, and no more than that.
            if (number == 4) Assert.Equal(new[] { false, false, true, true }, shown.Select(File.Exists).ToArray());
        }
        // The sixth does the same again.
        Assert.Equal(new[] { false, false, false, false, true, true }, shown.Select(File.Exists).ToArray());
    }

    [Fact]
    public async Task A_conversion_that_takes_too_long_leaves_the_photo_to_be_shown_as_itself()
    {
        var root = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Slow")).FullName);
        var photo = Path.Combine(root, "IMG_0001.HEIC");
        await File.WriteAllTextAsync(photo, "first photo");
        // Held until the limit, the way a photo sips can't get through is.
        var previews = new PhotoPreviews(
            new StandIn { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) },
            NullLogger<PhotoPreviews>.Instance)
        { Limit = TimeSpan.FromMilliseconds(200) };

        await using var stream = File.OpenRead(photo);
        Assert.Null(await previews.JpegForAsync(root, stream, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, ".homebase", PhotoPreviews.FolderName), "*.jpg"));
    }

    [Fact]
    public async Task On_a_Mac_sips_makes_a_smaller_JPEG_and_never_a_bigger_one()
    {
        var sips = new SipsImageConverter();
        if (!OperatingSystem.IsMacOS())
        {
            Assert.False(sips.Available);
            return;
        }
        Assert.True(sips.Available);
        // A 64×48 picture, written as a BMP and made into a HEIC the way an iPhone's would be.
        var bmp = Path.Combine(_temporary, "picture.bmp");
        await File.WriteAllBytesAsync(bmp, Bitmap(64, 48));
        var heic = Path.Combine(_temporary, "picture.heic");
        Run("/usr/bin/sips", "-s", "format", "heic", bmp, "--out", heic);

        var smaller = Path.Combine(_temporary, "smaller.jpg");
        await sips.ConvertAsync(heic, smaller, 32, CancellationToken.None);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, (await File.ReadAllBytesAsync(smaller))[..3]);
        Assert.Equal((32, 24), Size(smaller));

        var same = Path.Combine(_temporary, "same.jpg");
        await sips.ConvertAsync(heic, same, PhotoPreviews.LongestSide, CancellationToken.None);
        Assert.Equal((64, 48), Size(same));

        await Assert.ThrowsAsync<IOException>(() =>
            sips.ConvertAsync(Path.Combine(_temporary, "missing.heic"), Path.Combine(_temporary, "never.jpg"), 32, CancellationToken.None));
    }

    private static (int Width, int Height) Size(string picture)
    {
        var lines = Run("/usr/bin/sips", "-g", "pixelWidth", "-g", "pixelHeight", picture).Split('\n');
        int Read(string name) => int.Parse(lines.Single(line => line.Trim().StartsWith(name + ":", StringComparison.Ordinal)).Split(':')[1]);
        return (Read("pixelWidth"), Read("pixelHeight"));
    }

    private static string Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        return output;
    }

    /// <summary>An uncompressed 24-bit BMP of a gradient.</summary>
    private static byte[] Bitmap(int width, int height)
    {
        var row = (width * 3 + 3) & ~3;
        var pixels = row * height;
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.ASCII);
        writer.Write("BM"u8);
        writer.Write(54 + pixels);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(pixels);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                writer.Write(new[] { (byte)(x * 4), (byte)(y * 5), (byte)128 });
            for (var pad = width * 3; pad < row; pad++) writer.Write((byte)0);
        }
        return memory.ToArray();
    }
}
