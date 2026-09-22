using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Providers;

namespace Homebase.Tests;

public sealed class ProviderTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _host;
    private readonly string _config;
    private readonly StubDropbox _dropbox = new();

    public ProviderTests()
    {
        Directory.CreateDirectory(_temporary);
        // Resolved the way the host will resolve it: on macOS /var is a link into /private/var,
        // so a raw temporary path never equals the one the server answers with.
        _host = PathPolicy.NormalizeRoot(
            Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        _config = Path.Combine(_temporary, "Config");
    }

    // One account here, so every workspace gets the same stub: these tests are about importing,
    // not about whose connection is whose, which IsolationTests covers.
    private TestHost CreateApp(StubDropbox? dropbox = null) => new(_config, _ => dropbox ?? _dropbox);

    /// <summary>Signs in, chooses the host's folder, and hands back that account's own folder.</summary>
    private async Task<(HttpClient Client, string Root)> StartAsync(TestHost app)
    {
        var client = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(client, _host);
        return (client, await TestHost.UserRootAsync(client));
    }

    [Fact]
    public async Task A_dropbox_file_is_brought_home_and_shows_up_in_the_browser()
    {
        _dropbox.Add("/notes/hello.txt", "rev1", "First draft.");
        using var app = CreateApp();
        var (client, root) = await StartAsync(app);
        using var _ = client;

        Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>("/api/imports") ?? []);

        var job = await BringHome(client, "/notes/hello.txt");
        Assert.Equal("Done", job.GetProperty("stage").GetString());
        Assert.Equal(1, job.GetProperty("result").GetProperty("importedCount").GetInt32());

        var localFile = Path.Combine(root, "Files", "Dropbox", "notes", "hello.txt");
        Assert.Equal("First draft.", await File.ReadAllTextAsync(localFile));

        // The import is an ordinary file, so the normal browser sees it.
        var listing = await client.GetFromJsonAsync<DirectoryListing>("/api/files?path=Files/Dropbox/notes");
        Assert.Equal("hello.txt", listing!.Entries.Single().Name);
        Assert.Single(await client.GetFromJsonAsync<JsonElement[]>("/api/imports") ?? []);
    }

    [Fact]
    public async Task A_later_dropbox_revision_leaves_the_imported_copy_alone()
    {
        _dropbox.Add("/notes/hello.txt", "rev1", "First draft.");
        using var app = CreateApp();
        var (client, root) = await StartAsync(app);
        using var _ = client;
        await BringHome(client, "/notes/hello.txt");

        _dropbox.Add("/notes/hello.txt", "rev2", "Changed on Dropbox.");
        var again = await BringHome(client, "/notes/hello.txt");

        Assert.Equal(0, again.GetProperty("result").GetProperty("importedCount").GetInt32());
        Assert.Equal("First draft.",
            await File.ReadAllTextAsync(Path.Combine(root, "Files", "Dropbox", "notes", "hello.txt")));
    }

    [Fact]
    public async Task Importing_requires_a_chosen_folder_a_session_and_the_local_request_header()
    {
        _dropbox.Add("/notes/hello.txt", "rev1", "First draft.");
        using var app = CreateApp();
        using var signedIn = await app.SignUpAsync();

        // Signed in, but the host has no folder yet, so there is nowhere to bring anything to.
        Assert.Equal(HttpStatusCode.Conflict,
            (await signedIn.PostAsJsonAsync("/api/imports", new { remotePath = "/notes/hello.txt" })).StatusCode);

        await TestHost.SetHostRootAsync(signedIn, _host);
        using var bare = app.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await bare.PostAsJsonAsync("/api/imports", new { remotePath = "/notes/hello.txt" })).StatusCode);
        using var anonymous = app.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/imports", new { remotePath = "/notes/hello.txt" })).StatusCode);
    }

    [Fact]
    public async Task Starting_an_import_answers_before_the_files_have_arrived()
    {
        _dropbox.Add("/notes/hello.txt", "rev1", "First draft.");
        using var app = CreateApp();
        var (client, _) = await StartAsync(app);
        using var __ = client;

        var response = await client.PostAsJsonAsync("/api/imports",
            new { remotePath = "/notes/hello.txt", label = "hello.txt" });

        response.EnsureSuccessStatusCode();
        var started = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("job");
        // The work carries on past this response, which is the whole point of the job.
        Assert.True(started.GetProperty("running").GetBoolean());
        Assert.Equal("hello.txt", started.GetProperty("label").GetString());
        await Settled(client);
    }

    [Fact]
    public async Task Dropbox_reports_itself_unconfigured_without_an_app_key()
    {
        using var app = CreateApp(new StubDropbox { IsConfigured = false, IsConnected = false });
        var (client, _) = await StartAsync(app);
        using var __ = client;

        var status = await client.GetFromJsonAsync<JsonElement>("/api/providers/dropbox");

        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.False(status.GetProperty("connected").GetBoolean());
        // Connecting is refused rather than sending the browser to a half-built Dropbox URL.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/providers/dropbox/connect", null)).StatusCode);
    }


    /// <summary>Starts an import the way the panel does, and waits for the job behind it to settle.</summary>
    private static async Task<JsonElement> BringHome(HttpClient client, string remotePath)
    {
        (await client.PostAsJsonAsync("/api/imports", new { remotePath })).EnsureSuccessStatusCode();
        return await Settled(client);
    }

    private static async Task<JsonElement> Settled(HttpClient client)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = (await client.GetFromJsonAsync<JsonElement>("/api/imports/job")).GetProperty("job");
            if (!job.GetProperty("running").GetBoolean()) return job;
            await Task.Delay(15);
        }
        throw new TimeoutException("The import never finished.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }



}
