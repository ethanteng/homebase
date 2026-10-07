using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Homebase.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Homebase.Tests;

/// <summary>
/// Files sent from the browser. Every account can do it, administrator or not, so the rules that keep
/// one account out of another's folder have to hold for a path the browser chose as much as for one
/// it picked from a listing — and an upload that never finishes must leave nothing in My files.
/// </summary>
public sealed class UploadTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;

    public UploadTests()
    {
        Directory.CreateDirectory(_temporary);
        _host = PathPolicy.NormalizeRoot(
            Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
    }

    /// <summary>An administrator and an ordinary member, the member being who most of these are about.</summary>
    private async Task<(TestHost App, HttpClient Admin, string AdminRoot, HttpClient Member, string MemberRoot)> StartAsync()
    {
        var app = new TestHost(_config);
        var admin = await app.SignUpAsync("ada");
        await TestHost.SetHostRootAsync(admin, _host);
        var member = await app.AddUserAsync(admin, "bo");
        return (app, admin, await TestHost.UserRootAsync(admin), member, await TestHost.UserRootAsync(member));
    }

    private static async Task<string> BeginAsync(HttpClient client, string destination = "", long bytes = 0)
    {
        var response = await client.PostAsJsonAsync("/api/uploads", new { destination, bytes });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string id, string path, string content, long? modified = null)
    {
        var query = $"path={Uri.EscapeDataString(path)}" + (modified is { } ms ? $"&modified={ms}" : "");
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return client.PutAsync($"/api/uploads/{id}?{query}", body);
    }

    private static async Task<(string From, string To)[]> FinishAsync(HttpClient client, string id, string destination = "")
    {
        var response = await client.PostAsJsonAsync($"/api/uploads/{id}/finish", new { destination });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("items").EnumerateArray()
            .Select(item => (item.GetProperty("from").GetString()!, item.GetProperty("to").GetString()!)).ToArray();
    }

    private static string Uploads(string root) => Path.Combine(root, ".homebase", "uploads");

    [Fact]
    public async Task A_member_uploads_files_and_folders_into_their_own_files()
    {
        var (app, admin, adminRoot, member, root) = await StartAsync();
        using var _ = app;
        using var __ = admin;
        using var ___ = member;
        Directory.CreateDirectory(Path.Combine(root, "Archive"));
        await File.WriteAllTextAsync(Path.Combine(root, "Archive", "Notes.txt"), "already there");
        var lastChanged = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var id = await BeginAsync(member, "Archive", bytes: 20);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(member, id, "Notes.txt", "notes", lastChanged.ToUnixTimeMilliseconds())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(member, id, "Photos/beach.jpg", "beach")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(member, id, "Photos/Trips/day.jpg", "day")).StatusCode);
        // Nothing is in My files until the upload is finished.
        Assert.False(Directory.Exists(Path.Combine(root, "Archive", "Photos")));

        var placed = await FinishAsync(member, id, "Archive");

        // A name already taken is kept, and the arrival becomes “name 2”.
        Assert.Equal([("Notes.txt", "Archive/Notes 2.txt"), ("Photos", "Archive/Photos")], placed);
        Assert.Equal("already there", await File.ReadAllTextAsync(Path.Combine(root, "Archive", "Notes.txt")));
        Assert.Equal("notes", await File.ReadAllTextAsync(Path.Combine(root, "Archive", "Notes 2.txt")));
        Assert.Equal(lastChanged.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(root, "Archive", "Notes 2.txt")));
        Assert.Equal("day", await File.ReadAllTextAsync(Path.Combine(root, "Archive", "Photos", "Trips", "day.jpg")));
        // The folder it was gathered in is gone, and nothing reached anybody else.
        Assert.Empty(Directory.EnumerateFileSystemEntries(Uploads(root)));
        Assert.False(Path.Exists(Path.Combine(adminRoot, "Archive")));

        // A folder whose name is taken arrives beside it whole, rather than merged into it.
        var again = await BeginAsync(member, "Archive");
        (await SendAsync(member, again, "Photos/beach.jpg", "beach again")).EnsureSuccessStatusCode();
        Assert.Equal([("Photos", "Archive/Photos 2")], await FinishAsync(member, again, "Archive"));
        Assert.Equal("beach", await File.ReadAllTextAsync(Path.Combine(root, "Archive", "Photos", "beach.jpg")));
        Assert.Equal("beach again", await File.ReadAllTextAsync(Path.Combine(root, "Archive", "Photos 2", "beach.jpg")));
    }

    [Fact]
    public async Task A_path_in_an_upload_cannot_reach_outside_it_or_be_hidden()
    {
        var (app, admin, adminRoot, member, root) = await StartAsync();
        using var _ = app;
        using var __ = admin;
        using var ___ = member;
        var adminId = Path.GetFileName(adminRoot);
        var id = await BeginAsync(member);

        string[] attempts =
        [
            "",
            "../escaped.txt",
            $"../../{adminId}/planted.txt",
            $"../../../users/{adminId}/planted.txt",
            Path.Combine(adminRoot, "planted.txt"),
            "/etc/planted.txt",
            "Photos\\..\\..\\planted.txt",
            ".DS_Store",
            "Photos/.hidden/planted.txt",
            "Photos/./planted.txt",
            "with\0nul.txt"
        ];
        foreach (var attempt in attempts)
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(member, id, attempt, "planted")).StatusCode);

        Assert.Empty(await FinishAsync(member, id));
        Assert.Empty(Directory.EnumerateFiles(_host, "planted.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(_temporary, "escaped.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task One_account_cannot_send_to_finish_or_stop_another_accounts_upload()
    {
        var (app, admin, adminRoot, member, root) = await StartAsync();
        using var _ = app;
        using var __ = admin;
        using var ___ = member;
        var adminsUpload = await BeginAsync(admin);
        (await SendAsync(admin, adminsUpload, "diary.txt", "Ada's diary.")).EnsureSuccessStatusCode();

        // An upload id belongs to the account that started it; to anybody else it isn't there.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(member, adminsUpload, "planted.txt", "planted")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await member.PostAsJsonAsync($"/api/uploads/{adminsUpload}/finish", new { destination = "" })).StatusCode);
        (await member.DeleteAsync($"/api/uploads/{adminsUpload}")).EnsureSuccessStatusCode();
        // Nor can an id be anything but an id.
        foreach (var crafted in new[] { "..", "..%2F..%2F" + Path.GetFileName(adminRoot), "not-an-id" })
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(member, crafted, "planted.txt", "planted")).StatusCode);

        Assert.Equal([("diary.txt", "diary.txt")], await FinishAsync(admin, adminsUpload));
        Assert.Equal("Ada's diary.", await File.ReadAllTextAsync(Path.Combine(adminRoot, "diary.txt")));
        Assert.False(File.Exists(Path.Combine(adminRoot, "planted.txt")));
        Assert.False(File.Exists(Path.Combine(root, "diary.txt")));
    }

    [Fact]
    public async Task A_stopped_upload_leaves_nothing_behind()
    {
        var (app, admin, _, member, root) = await StartAsync();
        using var __ = app;
        using var ___ = admin;
        using var ____ = member;
        var id = await BeginAsync(member);
        (await SendAsync(member, id, "Photos/beach.jpg", "beach")).EnsureSuccessStatusCode();

        (await member.DeleteAsync($"/api/uploads/{id}")).EnsureSuccessStatusCode();

        Assert.Empty(Directory.EnumerateFileSystemEntries(Uploads(root)));
        Assert.False(Path.Exists(Path.Combine(root, "Photos")));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(member, id, "late.jpg", "late")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await member.PostAsJsonAsync($"/api/uploads/{id}/finish", new { destination = "" })).StatusCode);
        // Stopping twice is no different from stopping once.
        (await member.DeleteAsync($"/api/uploads/{id}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task An_upload_the_drive_has_no_room_for_or_with_nowhere_to_go_is_refused_before_anything_is_sent()
    {
        var (app, admin, _, member, root) = await StartAsync();
        using var __ = app;
        using var ___ = admin;
        using var ____ = member;

        var tooBig = await member.PostAsJsonAsync("/api/uploads", new { destination = "", bytes = long.MaxValue / 2 });
        Assert.Equal(HttpStatusCode.Conflict, tooBig.StatusCode);
        Assert.Contains("is free on your Uncloud drive", (await tooBig.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await member.PostAsJsonAsync("/api/uploads", new { destination = "Nowhere", bytes = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await member.PostAsJsonAsync("/api/uploads", new { destination = "../..", bytes = 1 })).StatusCode);
        Assert.False(Directory.Exists(Uploads(root)) && Directory.EnumerateFileSystemEntries(Uploads(root)).Any());

        // The folder is checked again at the end: one removed while files were on their way stops
        // the upload there, with everything still waiting to be put somewhere else.
        Directory.CreateDirectory(Path.Combine(root, "Gone soon"));
        var id = await BeginAsync(member, "Gone soon");
        (await SendAsync(member, id, "a.txt", "a")).EnsureSuccessStatusCode();
        Directory.Delete(Path.Combine(root, "Gone soon"));
        Assert.Equal(HttpStatusCode.NotFound,
            (await member.PostAsJsonAsync($"/api/uploads/{id}/finish", new { destination = "Gone soon" })).StatusCode);
        Assert.Equal([("a.txt", "a.txt")], await FinishAsync(member, id, ""));
    }

    [Fact]
    public async Task A_file_larger_than_an_ordinary_request_arrives_whole()
    {
        using var host = new TestHost(_config);
        // The test server sets no limit on a request's size, so this stands in for the one Kestrel
        // sets: anything else reading past it fails, the way a real host would refuse it.
        using var app = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new SizeLimit(Limit))));
        using var admin = await SignUpAsync(app);
        using var member = await AddUserAsync(app, admin);
        var root = await TestHost.UserRootAsync(member);
        var id = await BeginAsync(member);
        var video = new byte[Limit * 4];
        Random.Shared.NextBytes(video);

        var response = await member.PutAsync($"/api/uploads/{id}?path=video.mov", new ByteArrayContent(video));

        response.EnsureSuccessStatusCode();
        Assert.Equal(video.Length, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bytes").GetInt64());
        await FinishAsync(member, id);
        Assert.Equal(video, await File.ReadAllBytesAsync(Path.Combine(root, "video.mov")));
        // And the stand-in does refuse a request that nothing lifted it for.
        var tooLong = new string('x', Limit * 2);
        Assert.NotEqual(HttpStatusCode.OK,
            (await member.PostAsJsonAsync("/api/files/rename", new { path = "video.mov", name = tooLong })).StatusCode);
    }

    private const int Limit = 64 * 1024;

    private static async Task<HttpClient> SignUpAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Homebase-Request", "1");
        (await client.PostAsJsonAsync("/api/setup", new { username = "ada", displayName = "ada", password = TestHost.Password }))
            .EnsureSuccessStatusCode();
        return client;
    }

    private async Task<HttpClient> AddUserAsync(WebApplicationFactory<Program> app, HttpClient admin)
    {
        await TestHost.SetHostRootAsync(admin, _host);
        (await admin.PostAsJsonAsync("/api/users", new { username = "bo", displayName = "bo", password = TestHost.Password }))
            .EnsureSuccessStatusCode();
        var member = app.CreateClient();
        member.DefaultRequestHeaders.Add("X-Homebase-Request", "1");
        (await member.PostAsJsonAsync("/api/session", new { username = "bo", password = TestHost.Password })).EnsureSuccessStatusCode();
        return member;
    }

    /// <summary>Kestrel's limit on how much of a request is read, which the test server doesn't have.</summary>
    private sealed class SizeLimit(long bytes) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, proceed) =>
            {
                var limit = new Limited { MaxRequestBodySize = bytes };
                context.Features.Set<IHttpMaxRequestBodySizeFeature>(limit);
                context.Request.Body = new LimitedStream(context.Request.Body, limit);
                await proceed(context);
            });
            next(builder);
        };
    }

    private sealed class Limited : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    /// <summary>Asks the limit as it reads, as Kestrel does, so lifting it before reading is what counts.</summary>
    private sealed class LimitedStream(Stream inner, Limited limit) : Stream
    {
        private long _read;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            return Count(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        private int Count(int count)
        {
            _read += count;
            if (limit.MaxRequestBodySize is { } most && _read > most) throw new IOException("Request body too large.");
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_file_that_doesnt_say_how_big_it_is_still_stops_short_of_filling_the_drive()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temporary, "Library")).FullName;
        // 1,000 bytes free, a tenth of it held back: 900 is all a file can take.
        using var library = new LibraryService(root, new MetadataIndex())
        {
            Space = _ => new StorageReport(FreeBytes: 1000, TotalBytes: 8192)
        };
        library.Initialize();
        var id = await library.BeginUploadAsync("", 0, CancellationToken.None);
        var gathered = Path.Combine(Uploads(root), id);

        // Sent without a length, the way a chunked request comes: stopped once it passes the room left.
        var unsaid = await Assert.ThrowsAsync<LibraryException>(() =>
            library.ReceiveAsync(id, "big.bin", new MemoryStream(new byte[2000]), null, null, CancellationToken.None));
        Assert.Equal("unavailable", unsaid.Code);
        Assert.False(File.Exists(Path.Combine(gathered, "big.bin")));
        // Sent with one, refused before any of it is read.
        var said = await Assert.ThrowsAsync<LibraryException>(() =>
            library.ReceiveAsync(id, "big.bin", new MemoryStream(new byte[2000]), 2000, null, CancellationToken.None));
        Assert.Equal("unavailable", said.Code);
        Assert.False(File.Exists(Path.Combine(gathered, "big.bin")));

        // Within the room left, either way is fine.
        Assert.Equal(800, await library.ReceiveAsync(id, "fits.bin", new MemoryStream(new byte[800]), null, null, CancellationToken.None));
        Assert.Equal([new EditedEntry("fits.bin", "fits.bin")], await library.FinishUploadAsync(id, "", CancellationToken.None));
        Assert.Equal(800, new FileInfo(Path.Combine(root, "fits.bin")).Length);
    }

    [Fact]
    public async Task Sending_a_file_again_replaces_what_arrived_of_it_before()
    {
        var (app, admin, _, member, root) = await StartAsync();
        using var __ = app;
        using var ___ = admin;
        using var ____ = member;
        var id = await BeginAsync(member);
        (await SendAsync(member, id, "a.txt", "first try, longer")).EnsureSuccessStatusCode();
        (await SendAsync(member, id, "a.txt", "retry")).EnsureSuccessStatusCode();

        await FinishAsync(member, id);

        Assert.Equal("retry", await File.ReadAllTextAsync(Path.Combine(root, "a.txt")));
    }

    [Fact]
    public async Task An_upload_nobody_finished_is_cleared_away_after_a_day()
    {
        var (app, admin, _, member, root) = await StartAsync();
        using var __ = app;
        using var ___ = admin;
        using var ____ = member;
        var abandoned = await BeginAsync(member);
        (await SendAsync(member, abandoned, "a.txt", "a")).EnsureSuccessStatusCode();
        var recent = await BeginAsync(member);
        Directory.SetLastWriteTimeUtc(Path.Combine(Uploads(root), abandoned),
            DateTime.UtcNow - LibraryService.KeepAbandonedUploadsFor - TimeSpan.FromMinutes(1));

        await BeginAsync(member);

        Assert.False(Directory.Exists(Path.Combine(Uploads(root), abandoned)));
        Assert.True(Directory.Exists(Path.Combine(Uploads(root), recent)));
    }

    public void Dispose() => Directory.Delete(_temporary, recursive: true);
}
