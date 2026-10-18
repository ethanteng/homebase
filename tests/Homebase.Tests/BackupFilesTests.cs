using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Homebase.Tests;

/// <summary>
/// Each person's own files in the backups, from My files: which backups there are, what was in
/// their folder in one, and restoring from it under the bin's rules. Run against the real restic
/// and a repository in a folder, signed in as the people themselves.
/// </summary>
public sealed class BackupFilesTests : IDisposable
{
    private readonly string _temporary = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"))).FullName;

    private TestHost Host() => new(Path.Combine(_temporary, "config"), settings: new Dictionary<string, string?>
    {
        ["Homebase:Backup:Repository"] = Path.Combine(_temporary, "repository"),
        ["Homebase:Backup:Password"] = "correct horse battery",
        ["Homebase:Backup:Restic"] = TestRestic.Path,
        ["Homebase:Backup:Nightly"] = "false"
    });

    private async Task<(HttpClient Admin, HttpClient Member, string MemberRoot, string AdminRoot)> PeopleAsync(TestHost app)
    {
        var admin = await app.SignUpAsync();
        var root = Directory.CreateDirectory(Path.Combine(_temporary, "root")).FullName;
        await TestHost.SetHostRootAsync(admin, root);
        var member = await app.AddUserAsync(admin, "member");
        return (admin, member, await TestHost.UserRootAsync(member), await TestHost.UserRootAsync(admin));
    }

    private static async Task<string> BackUpAsync(HttpClient admin)
    {
        (await admin.PostAsync("/api/host/backup", null)).EnsureSuccessStatusCode();
        await Until(async () => !(await admin.GetFromJsonAsync<JsonElement>("/api/host/backup")).GetProperty("running").GetBoolean());
        var listed = await admin.GetFromJsonAsync<JsonElement>("/api/backups");
        return listed.GetProperty("backups")[0].GetProperty("id").GetString()!;
    }

    private static async Task<string[]> NamesAsync(HttpClient client, string backup, string? path = null)
    {
        var folder = await client.GetFromJsonAsync<JsonElement>(
            $"/api/backups/{backup}/files{(path is null ? "" : $"?path={Uri.EscapeDataString(path)}")}");
        return folder.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!).ToArray();
    }

    [Fact]
    public async Task Each_person_sees_only_their_own_files_in_a_backup()
    {
        using var app = Host();
        var (admin, member, memberRoot, adminRoot) = await PeopleAsync(app);
        File.WriteAllText(Path.Combine(memberRoot, "notes.txt"), "first draft");
        Directory.CreateDirectory(Path.Combine(memberRoot, "Photos", "2024"));
        File.WriteAllText(Path.Combine(memberRoot, "Photos", "beach [1].jpg"), "sand");
        File.WriteAllText(Path.Combine(adminRoot, "diary.txt"), "not for anybody else");
        var backup = await BackUpAsync(admin);

        var listed = await member.GetFromJsonAsync<JsonElement>("/api/backups");
        Assert.True(listed.GetProperty("configured").GetBoolean());
        Assert.Equal(backup, listed.GetProperty("backups")[0].GetProperty("id").GetString());
        Assert.False(listed.GetProperty("backups")[0].TryGetProperty("paths", out _), "where the host keeps everybody's files isn't said");

        Assert.Equal(["Photos", "notes.txt"], await NamesAsync(member, backup));
        Assert.Equal(["2024", "beach [1].jpg"], await NamesAsync(member, backup, "Photos"));
        Assert.Equal(["diary.txt"], await NamesAsync(admin, backup));
        // Nothing above a person's own folder can be named, nor anything hidden in it.
        var adminId = Path.GetFileName(adminRoot);
        Assert.Equal(HttpStatusCode.BadRequest, (await member.GetAsync($"/api/backups/{backup}/files?path=../{adminId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await member.GetAsync($"/api/backups/{backup}/files?path=.homebase")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/backups/{backup}/files?path=Gone")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync("/api/backups/0123456789abcdef/files")).StatusCode);
        var restored = await member.PostAsJsonAsync($"/api/backups/{backup}/restore", new { paths = new[] { $"../{adminId}/diary.txt" } });
        Assert.Equal(HttpStatusCode.BadRequest, restored.StatusCode);
        Assert.False(File.Exists(Path.Combine(memberRoot, "diary.txt")));
    }

    [Fact]
    public async Task A_restore_puts_things_back_where_they_were_and_keeps_what_is_there_now()
    {
        using var app = Host();
        var (admin, member, root, _) = await PeopleAsync(app);
        File.WriteAllText(Path.Combine(root, "notes.txt"), "first draft");
        Directory.CreateDirectory(Path.Combine(root, "Photos", "2024"));
        var photo = RandomNumberGenerator.GetBytes(200 * 1024);
        File.WriteAllBytes(Path.Combine(root, "Photos", "beach [1].jpg"), photo);
        File.WriteAllText(Path.Combine(root, "Photos", "2024", "cost$HOME.jpg"), "a dollar in the name");
        var backup = await BackUpAsync(admin);

        File.WriteAllText(Path.Combine(root, "notes.txt"), "second draft");
        Directory.Delete(Path.Combine(root, "Photos"), recursive: true);

        var answer = await member.PostAsJsonAsync($"/api/backups/{backup}/restore", new { paths = new[] { "notes.txt", "Photos" } });

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var items = (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()
            .Select(item => (item.GetProperty("from").GetString(), item.GetProperty("to").GetString())).ToArray();
        Assert.Equal([("notes.txt", "notes 2.txt"), ("Photos", "Photos")], items);
        // The newer copy kept; the one from the backup beside it.
        Assert.Equal("second draft", File.ReadAllText(Path.Combine(root, "notes.txt")));
        Assert.Equal("first draft", File.ReadAllText(Path.Combine(root, "notes 2.txt")));
        Assert.Equal(photo, File.ReadAllBytes(Path.Combine(root, "Photos", "beach [1].jpg")));
        Assert.Equal("a dollar in the name", File.ReadAllText(Path.Combine(root, "Photos", "2024", "cost$HOME.jpg")));
        // Nothing left on its way in.
        var restoring = Path.Combine(root, ".homebase", "restoring");
        Assert.True(!Directory.Exists(restoring) || Directory.GetFileSystemEntries(restoring).Length == 0);
    }

    [Fact]
    public async Task A_restore_there_isnt_room_for_is_turned_away_before_anything_comes_back()
    {
        using var app = Host();
        var (admin, member, root, _) = await PeopleAsync(app);
        File.WriteAllBytes(Path.Combine(root, "big.bin"), RandomNumberGenerator.GetBytes(512 * 1024));
        var backup = await BackUpAsync(admin);
        File.Delete(Path.Combine(root, "big.bin"));
        // Everyone here may now use only a little more than they have.
        (await admin.PutAsJsonAsync("/api/host/storage", new { bytes = 64 * 1024 })).EnsureSuccessStatusCode();

        var answer = await member.PostAsJsonAsync($"/api/backups/{backup}/restore", new { paths = new[] { "big.bin" } });

        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Contains("restore less at once", await answer.Content.ReadAsStringAsync());
        Assert.False(File.Exists(Path.Combine(root, "big.bin")));
        var restoring = Path.Combine(root, ".homebase", "restoring");
        Assert.True(!Directory.Exists(restoring) || Directory.GetFileSystemEntries(restoring).Length == 0);
    }

    [Fact]
    public async Task Without_backups_there_are_none_to_show()
    {
        using var app = new TestHost(Path.Combine(_temporary, "config"));
        var admin = await app.SignUpAsync();

        var listed = await admin.GetFromJsonAsync<JsonElement>("/api/backups");

        Assert.False(listed.GetProperty("configured").GetBoolean());
        Assert.Equal(0, listed.GetProperty("backups").GetArrayLength());
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Still waiting after a minute.");
            await Task.Delay(100);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
