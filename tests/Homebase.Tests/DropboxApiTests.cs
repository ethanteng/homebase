using System.Net;
using System.Text;
using Homebase.Core.Providers;

namespace Homebase.Tests;

public sealed class DropboxApiTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));

    public DropboxApiTests() => Directory.CreateDirectory(_temporary);

    [Fact]
    public async Task A_folder_larger_than_one_page_is_listed_in_full()
    {
        var tokens = new DropboxTokenStore(_temporary);
        await tokens.SaveAsync("refresh-token", "Test", CancellationToken.None);
        var handler = new ScriptedDropbox();
        using var client = new HttpClient(handler);
        var api = new DropboxApi(client, tokens, "app-key");

        var entries = await api.ListFolderAsync("", CancellationToken.None);

        Assert.Equal(["first.txt", "second.txt"], entries.Select(entry => entry.Name));
        // The second page is only fetched by following the cursor Dropbox handed back.
        Assert.Contains("/2/files/list_folder/continue", handler.Requested);
        Assert.Equal("page-1-cursor", handler.ContinuedWith);
    }

    [Fact]
    public async Task An_expired_connection_is_reported_rather_than_retried_forever()
    {
        var tokens = new DropboxTokenStore(_temporary);
        await tokens.SaveAsync("refresh-token", "Test", CancellationToken.None);
        using var client = new HttpClient(new ScriptedDropbox { Unauthorized = true });
        var api = new DropboxApi(client, tokens, "app-key");

        var error = await Assert.ThrowsAsync<Homebase.Core.LibraryException>(
            () => api.GetMetadataAsync("/notes/hello.txt", CancellationToken.None));

        Assert.Equal("provider_auth", error.Code);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }

    private sealed class ScriptedDropbox : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];
        public string? ContinuedWith { get; private set; }
        public bool Unauthorized { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requested.Add(path);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            string json;
            if (path.EndsWith("/oauth2/token"))
                json = """{"access_token":"access","expires_in":14400}""";
            else if (Unauthorized)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("expired") };
            else if (path.EndsWith("/list_folder"))
                json = """{"entries":[{".tag":"file","id":"1","name":"first.txt","path_lower":"/first.txt","path_display":"/first.txt","size":1,"rev":"a"}],"cursor":"page-1-cursor","has_more":true}""";
            else if (path.EndsWith("/list_folder/continue"))
            {
                ContinuedWith = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("cursor").GetString();
                json = """{"entries":[{".tag":"file","id":"2","name":"second.txt","path_lower":"/second.txt","path_display":"/second.txt","size":1,"rev":"b"}],"has_more":false}""";
            }
            else
                json = """{".tag":"file","id":"1","name":"hello.txt","path_lower":"/notes/hello.txt","path_display":"/notes/hello.txt","size":1,"rev":"a"}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
