using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Accounts;

namespace Homebase.Tests;

/// <summary>
/// How much of the drive an administrator lets Uncloud fill. Every write is checked against the
/// room the allotment leaves rather than the drive's, so these are about that room being right: the
/// same size as Uncloud fills it, never more than the drive has, and current between the walks that
/// measure what Uncloud holds.
/// </summary>
public sealed class AllotmentTests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;

    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;

    public AllotmentTests()
    {
        Directory.CreateDirectory(_temporary);
        _host = PathPolicy.NormalizeRoot(
            Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
    }

    /// <summary>A host whose drive and whose walk of everybody's folders say whatever the test says.</summary>
    private (StorageAllotment Allotment, HostService Host) Create(Func<long> free, Func<long> held, ControlDatabase? database = null)
    {
        database ??= new ControlDatabase(_config);
        var host = new HostService(database);
        host.SelectRoot(_host);
        return (new StorageAllotment(database, host)
        {
            Drive = _ => new StorageReport(free(), 1000 * GB),
            Holding = _ => held()
        }, host);
    }

    private string UserRoot(string id) => UserPaths.RootFor(_host, id);

    [Fact]
    public void A_share_is_of_the_free_space_and_what_Uncloud_already_holds_so_it_stays_the_same_as_Uncloud_fills_it()
    {
        var half = new Allotment(AllotmentUnit.Percent, 50);

        var empty = StorageAllotment.Apply(half, used: 0, new StorageReport(600 * GB, 1000 * GB));
        Assert.Equal(new StorageReport(300 * GB, 300 * GB, Capped: true), empty);

        // A hundred gigabytes later the share is still 300, with 200 of it left.
        var filling = StorageAllotment.Apply(half, used: 100 * GB, new StorageReport(500 * GB, 1000 * GB));
        Assert.Equal(new StorageReport(200 * GB, 300 * GB, Capped: true), filling);
    }

    [Fact]
    public void A_set_amount_the_drive_cant_give_changes_nothing()
    {
        var drive = new StorageReport(50 * GB, 1000 * GB);

        Assert.Equal(drive, StorageAllotment.Apply(new Allotment(AllotmentUnit.Bytes, 500 * GB), used: 20 * GB, drive));
        Assert.Equal(drive, StorageAllotment.Apply(new Allotment(AllotmentUnit.Percent, 100), used: 20 * GB, drive));
    }

    [Fact]
    public void An_allotment_below_what_Uncloud_holds_leaves_no_room_and_takes_nothing_away()
    {
        var room = StorageAllotment.Apply(
            new Allotment(AllotmentUnit.Bytes, 10 * GB), used: 30 * GB, new StorageReport(500 * GB, 1000 * GB));

        Assert.Equal(new StorageReport(0, 10 * GB, Capped: true), room);
        Assert.False(Storage.Fits(1, room.FreeBytes));
    }

    [Fact]
    public void Without_an_allotment_it_is_the_drive_and_nothing_is_walked()
    {
        var walks = 0;
        var (allotment, _) = Create(() => 400 * GB, () => { walks++; return 0; });

        Assert.Equal(new StorageReport(400 * GB, 1000 * GB), allotment.Space(UserRoot("ada")));
        Assert.Equal(0, walks);
    }

    [Fact]
    public void What_Uncloud_writes_between_walks_comes_out_of_its_room_at_once()
    {
        var free = 500 * GB;
        var walks = 0;
        var (allotment, _) = Create(() => free, () => { walks++; return 20 * GB; });
        allotment.Set(new Allotment(AllotmentUnit.Bytes, 100 * GB));

        Assert.Equal(80 * GB, allotment.Space(UserRoot("ada"))!.FreeBytes);
        // An upload under way: the drive goes down, and so does the room, without walking again.
        free -= 30 * GB;
        Assert.Equal(50 * GB, allotment.Space(UserRoot("bo"))!.FreeBytes);
        Assert.Equal(50 * GB, allotment.Held(_host));
        // And deleting gives it back.
        free += 10 * GB;
        Assert.Equal(60 * GB, allotment.Space(UserRoot("ada"))!.FreeBytes);
        Assert.Equal(1, walks);
    }

    [Fact]
    public async Task A_walk_that_has_gone_stale_is_taken_again_without_holding_anybody_up()
    {
        var held = 20 * GB;
        var walked = new SemaphoreSlim(0);
        var database = new ControlDatabase(_config);
        var host = new HostService(database);
        host.SelectRoot(_host);
        var allotment = new StorageAllotment(database, host)
        {
            Drive = _ => new StorageReport(500 * GB, 1000 * GB),
            Holding = _ => { walked.Release(); return held; },
            Freshness = TimeSpan.Zero
        };
        allotment.Set(new Allotment(AllotmentUnit.Bytes, 100 * GB));
        Assert.Equal(80 * GB, allotment.Space(_host)!.FreeBytes);
        Assert.True(await walked.WaitAsync(TimeSpan.FromSeconds(10)));

        // Something put there by hand, which the drive can't say was Uncloud's: the next walk finds it.
        held = 50 * GB;
        allotment.Space(_host);
        Assert.True(await walked.WaitAsync(TimeSpan.FromSeconds(10)));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (allotment.Space(_host)!.FreeBytes != 50 * GB && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(50 * GB, allotment.Space(_host)!.FreeBytes);
    }

    [Fact]
    public void Files_moved_in_without_writing_anything_are_counted_once_it_is_told_to_look_again()
    {
        var held = 20 * GB;
        var (allotment, _) = Create(() => 500 * GB, () => held);
        allotment.Set(new Allotment(AllotmentUnit.Bytes, 100 * GB));
        Assert.Equal(80 * GB, allotment.Space(_host)!.FreeBytes);

        // A folder left at the top of the host's folder, renamed into somebody's: same drive, same
        // free space, so nothing between walks can see it arrive.
        held = 60 * GB;
        Assert.Equal(80 * GB, allotment.Space(_host)!.FreeBytes);
        allotment.Invalidate();
        Assert.Equal(40 * GB, allotment.Space(_host)!.FreeBytes);
    }

    [Fact]
    public void Somewhere_outside_the_hosts_folder_is_left_the_drive()
    {
        var (allotment, _) = Create(() => 500 * GB, () => 0);
        allotment.Set(new Allotment(AllotmentUnit.Bytes, GB));

        // A workspace still finishing an import that began before the folder moved.
        Assert.Equal(new StorageReport(500 * GB, 1000 * GB), allotment.Space(Path.Combine(_temporary, "Elsewhere")));
        Assert.Equal(new StorageReport(500 * GB, 1000 * GB), allotment.Space(_host + "-other"));
        Assert.True(allotment.Space(_host)!.Capped);
    }

    [Fact]
    public void The_allotment_is_kept_across_restarts_until_it_is_taken_away()
    {
        var database = new ControlDatabase(_config);
        var (first, _) = Create(() => 500 * GB, () => 0, database);
        first.Set(new Allotment(AllotmentUnit.Percent, 40));

        var (second, _) = Create(() => 500 * GB, () => 0, database);
        Assert.Equal(new Allotment(AllotmentUnit.Percent, 40), second.Current);

        second.Set(null);
        var (third, _) = Create(() => 500 * GB, () => 0, database);
        Assert.Null(third.Current);
    }

    [Fact]
    public void Only_an_allotment_that_can_be_meant_is_taken()
    {
        Assert.Null(Allotment.From(null, null));
        Assert.Equal(new Allotment(AllotmentUnit.Bytes, 5 * GB), Allotment.From(5 * GB, null));
        Assert.Equal(new Allotment(AllotmentUnit.Percent, 100), Allotment.From(null, 100));
        foreach (var (bytes, percent) in new (long?, int?)[] { (GB, 50), (0, null), (-1, null), (null, 0), (null, 101) })
            Assert.Equal("invalid", Assert.Throws<LibraryException>(() => Allotment.From(bytes, percent)).Code);

        // Anything stored that can't be read back is read as none, not as a reason to refuse every write.
        Assert.Null(Allotment.Parse("percent:0"));
        Assert.Null(Allotment.Parse("gallons:5"));
        Assert.Null(Allotment.Parse("bytes:"));
        Assert.Null(Allotment.Parse(null));
        Assert.Equal(new Allotment(AllotmentUnit.Bytes, 7), Allotment.Parse(new Allotment(AllotmentUnit.Bytes, 7).Stored));
    }

    [Fact]
    public async Task Only_an_administrator_sets_it_and_everybody_is_held_to_it()
    {
        using var app = new TestHost(_config);
        using var admin = await app.SignUpAsync("ada");
        await TestHost.SetHostRootAsync(admin, _host);
        using var member = await app.AddUserAsync(admin, "bo");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/host/storage")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PutAsJsonAsync("/api/host/storage", new { percent = 100 })).StatusCode);

        var before = await admin.GetFromJsonAsync<JsonElement>("/api/host/storage");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("allotment").ValueKind);
        Assert.False(before.GetProperty("capped").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/host/storage", new { percent = 0 })).StatusCode);

        // A megabyte for everybody, however big the drive running these tests is.
        var set = await admin.PutAsJsonAsync("/api/host/storage", new { bytes = 1024 * 1024 });
        set.EnsureSuccessStatusCode();
        var after = await set.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Bytes", after.GetProperty("allotment").GetProperty("unit").GetString());
        Assert.Equal(1024 * 1024, after.GetProperty("totalBytes").GetInt64());
        Assert.True(after.GetProperty("capped").GetBoolean());

        // Everybody sees the allotment as the space there is, and is refused past it.
        var storage = await member.GetFromJsonAsync<JsonElement>("/api/storage");
        Assert.Equal(1024 * 1024, storage.GetProperty("totalBytes").GetInt64());
        Assert.True(storage.GetProperty("capped").GetBoolean());
        var tooBig = await member.PostAsJsonAsync("/api/uploads", new { destination = "", bytes = 2 * 1024 * 1024 });
        Assert.Equal(HttpStatusCode.Conflict, tooBig.StatusCode);

        // Taken away again, it's the drive.
        (await admin.PutAsJsonAsync("/api/host/storage", new { })).EnsureSuccessStatusCode();
        Assert.False((await member.GetFromJsonAsync<JsonElement>("/api/storage")).GetProperty("capped").GetBoolean());
        (await member.PostAsJsonAsync("/api/uploads", new { destination = "", bytes = 2 * 1024 * 1024 })).EnsureSuccessStatusCode();
    }

    public void Dispose() => Directory.Delete(_temporary, recursive: true);
}
