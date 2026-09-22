using System.Net;
using System.Net.Http.Headers;
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
        var tokens = new HeldTokens("refresh-token");
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
        var tokens = new HeldTokens("refresh-token");
        using var client = new HttpClient(new ScriptedDropbox { Unauthorized = true });
        var api = new DropboxApi(client, tokens, "app-key");

        var error = await Assert.ThrowsAsync<Homebase.Core.LibraryException>(
            () => api.GetMetadataAsync("/notes/hello.txt", CancellationToken.None));

        Assert.Equal("provider_auth", error.Code);
    }

    [Fact]
    public async Task A_folder_Dropbox_asks_us_to_slow_down_for_is_tried_again()
    {
        // A rate-limited listing used to surface as a folder that couldn't be read, which left
        // it and its whole subtree out of an import that otherwise reported success.
        var tokens = new HeldTokens("refresh-token");
        var handler = new ScriptedDropbox { RateLimitFirstListing = true };
        using var client = new HttpClient(handler);
        var waited = new List<TimeSpan>();
        var api = new DropboxApi(client, tokens, "app-key")
        {
            Wait = (delay, _) => { waited.Add(delay); return Task.CompletedTask; }
        };

        var entries = await api.ListFolderAsync("/notes", CancellationToken.None);

        Assert.Equal(["first.txt", "second.txt"], entries.Select(entry => entry.Name));
        // Dropbox said how long to wait; a guess of our own would ignore it.
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(waited));
    }

    [Fact]
    public async Task A_refusal_Dropbox_will_repeat_is_not_tried_again()
    {
        var tokens = new HeldTokens("refresh-token");
        var handler = new ScriptedDropbox { Unauthorized = true };
        using var client = new HttpClient(handler);
        var api = new DropboxApi(client, tokens, "app-key")
        {
            Wait = (_, _) => throw new InvalidOperationException("An expired connection must not be waited out.")
        };

        await Assert.ThrowsAsync<Homebase.Core.LibraryException>(
            () => api.GetMetadataAsync("/notes/hello.txt", CancellationToken.None));

        Assert.Equal(1, handler.Requested.Count(path => path.EndsWith("/get_metadata")));
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
        public bool RateLimitFirstListing { get; init; }
        private bool _rateLimited;

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
            else if (RateLimitFirstListing && !_rateLimited && path.EndsWith("/list_folder"))
            {
                _rateLimited = true;
                var refusal = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("too_many_requests")
                };
                refusal.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return refusal;
            }
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

    /// <summary>A connection held in memory, so these tests are about the HTTP client alone.</summary>
    private sealed class HeldTokens(string? refreshToken) : IProviderTokens
    {
        private string? _token = refreshToken;

        public string? Load() => _token;
        public string? LoadAccountName() => "Test";
        public Task SaveAsync(string refresh, string? accountName, CancellationToken cancellationToken)
        {
            _token = refresh;
            return Task.CompletedTask;
        }
        public void Clear() => _token = null;
    }
}
