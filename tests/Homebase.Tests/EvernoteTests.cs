using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Homebase.Core;
using Homebase.Core.Providers;

namespace Homebase.Tests;

/// <summary>
/// Bringing notes home from Evernote, which no longer lets new apps sign in, through the export files
/// its own app saves. What matters is that every note arrives as something that opens anywhere —
/// text, pictures and attachments all there — and that nothing in an export file, which may have
/// come from anybody, gets to run or to reach outside the file it came in.
/// </summary>
public sealed class EvernoteTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"));
    private readonly string _library;

    public EvernoteTests()
    {
        _library = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Library")).FullName);
    }

    private static readonly byte[] Photo = Enumerable.Range(0, 300).Select(value => (byte)value).ToArray();

    private sealed record Resource(byte[] Data, string Mime, string? FileName = null)
    {
        public string Hash => Convert.ToHexString(MD5.HashData(Data)).ToLowerInvariant();
    }

    private sealed record Note(
        string Title, string Body, Resource[]? Resources = null, string Created = "20190301T120000Z",
        string Updated = "20200505T080000Z", string[]? Tags = null, string? Extra = null);

    private static string Export(params Note[] notes)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            .Append("<!DOCTYPE en-export SYSTEM \"http://xml.evernote.com/pub/evernote-export4.dtd\">\n")
            .Append("<en-export export-date=\"20261120T100000Z\" application=\"Evernote\" version=\"10.105.4\">\n");
        foreach (var note in notes)
        {
            xml.Append("<note><title>").Append(System.Security.SecurityElement.Escape(note.Title)).Append("</title>")
                .Append("<created>").Append(note.Created).Append("</created>")
                .Append("<updated>").Append(note.Updated).Append("</updated>");
            foreach (var tag in note.Tags ?? []) xml.Append("<tag>").Append(tag).Append("</tag>");
            xml.Append(note.Extra ?? "")
                .Append("<content><![CDATA[<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>\n")
                .Append("<!DOCTYPE en-note SYSTEM \"http://xml.evernote.com/pub/enml2.dtd\">\n<en-note>")
                .Append(note.Body).Append("</en-note>]]></content>");
            foreach (var resource in note.Resources ?? [])
            {
                xml.Append("<resource><data encoding=\"base64\">\n")
                    .Append(Convert.ToBase64String(resource.Data, Base64FormattingOptions.InsertLineBreaks))
                    .Append("\n</data><mime>").Append(resource.Mime).Append("</mime>");
                if (resource.FileName is not null)
                    xml.Append("<resource-attributes><file-name>").Append(System.Security.SecurityElement.Escape(resource.FileName))
                        .Append("</file-name></resource-attributes>");
                xml.Append("</resource>");
            }
            xml.Append("</note>\n");
        }
        return xml.Append("</en-export>\n").ToString();
    }

    private string Save(string name, string export)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_library, EvernoteSource.FolderName)).FullName;
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, export, new UTF8Encoding(false));
        return path;
    }

    private static async Task<string> ReadAllAsync(IImportSource source, string path)
    {
        await using var stream = await source.OpenAsync(path, CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private EvernoteSource Source() => new(_library, new ImportLog());

    /// <summary>
    /// The path the source knows something by, found by the name it's shown with: a note's path is
    /// which note it is, and only its name says where it lands.
    /// </summary>
    private static async Task<string> PathOf(IImportSource source, string folder, string name) =>
        (await source.ListFolderAsync(folder, CancellationToken.None)).Single(entry => entry.Name == name).Path;

    private static async Task<byte[]> BytesAsync(IImportSource source, string path)
    {
        await using var stream = await source.OpenAsync(path, CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    [Fact]
    public async Task Each_export_file_is_a_notebook_and_each_note_a_page_with_its_attachments_beside_it()
    {
        var photo = new Resource(Photo, "image/png", "photo.png");
        var recipe = new Resource(Encoding.UTF8.GetBytes("%PDF-1.4 recipe"), "application/pdf", "Recipe card.pdf");
        Save("Recipes.enex", Export(
            new Note("Pad thai",
                $"<div>Soak the noodles&nbsp;first.</div><en-media hash=\"{photo.Hash}\" type=\"image/png\"/>"
                + $"<div><en-todo checked=\"true\"/>Buy limes</div><en-media hash=\"{recipe.Hash}\" type=\"application/pdf\"/>",
                [photo, recipe], Tags: ["thai", "dinner"]),
            new Note("Shopping list", "<ul><li>Eggs</li></ul>")));
        var source = Source();

        var notebook = Assert.Single(await source.ListFolderAsync("/", CancellationToken.None));
        Assert.Equal("Recipes", notebook.Name);
        Assert.True(notebook.IsFolder);
        Assert.Equal("2 notes", notebook.Detail);

        var inside = await source.ListFolderAsync("/Recipes", CancellationToken.None);
        Assert.Equal(["Pad thai files", "Pad thai.html", "Shopping list.html"], inside.Select(entry => entry.Name));
        var attachments = await PathOf(source, "/Recipes", "Pad thai files");
        var files = await source.ListFolderAsync(attachments, CancellationToken.None);
        Assert.Equal(["photo.png", "Recipe card.pdf"], files.Select(entry => entry.Name));
        Assert.Equal(Photo.Length, files[0].Size);
        // Where each lands is beside its note, under the note's name.
        Assert.Equal("/Recipes/Pad thai files/photo.png", files[0].DisplayPath);

        // The attachment comes out exactly as it went in, decoded from the export.
        Assert.Equal(Photo, await BytesAsync(source, files[0].Path));

        var page = await ReadAllAsync(source, await PathOf(source, "/Recipes", "Pad thai.html"));
        // The size listed is the size that arrives, so the room check and the progress are right.
        Assert.Equal(Encoding.UTF8.GetByteCount(page), inside.Single(entry => entry.Name == "Pad thai.html").Size);
        Assert.Contains("<title>Pad thai</title>", page);
        Assert.Contains("Soak the noodles first.", page);
        Assert.Contains("<img src=\"Pad%20thai%20files/photo.png\" alt=\"photo.png\">", page);
        Assert.Contains("<a class=\"attachment\" href=\"Pad%20thai%20files/Recipe%20card.pdf\">Recipe card.pdf</a>", page);
        Assert.Contains("<input type=\"checkbox\" disabled checked> Buy limes", page);
        Assert.Contains("Created 1 March 2019 · Updated 5 May 2020", page);
        Assert.Contains("Tags: thai, dinner", page);
    }

    [Fact]
    public async Task Nothing_in_a_note_can_run_or_fetch_anything()
    {
        Save("Sent to me.enex", Export(new Note("Harmless",
            "<div onclick=\"steal()\" style=\"color:red\">Hello</div>"
            + "<script>steal()</script>"
            + "<a href=\"javascript:steal()\">click</a>"
            + "<a href=\" java\tscript:steal()\">again</a>"
            + "<a href=\"https://example.com/\" onmouseover=\"steal()\">fine</a>"
            + "<div style=\"background:url(https://tracker.example/pixel)\">styled</div>"
            + "<iframe src=\"https://example.com\">frame</iframe>"
            + "<img src=\"javascript:steal()\"/>"
            + "<form action=\"https://example.com\"><input name=\"password\"/></form>")));
        var source = Source();

        var page = await ReadAllAsync(source, await PathOf(source, "/Sent to me", "Harmless.html"));
        var body = page[page.IndexOf("<main>", StringComparison.Ordinal)..];

        Assert.DoesNotContain("<script", body);
        Assert.DoesNotContain("steal", body);
        Assert.DoesNotContain("onclick", body);
        Assert.DoesNotContain("onmouseover", body);
        Assert.DoesNotContain("tracker.example", body);
        Assert.DoesNotContain("<iframe", body);
        Assert.DoesNotContain("<form", body);
        Assert.DoesNotContain("password", body);
        // What's left is the note: its words, its colour, its one real link.
        Assert.Contains("<div style=\"color:red\">Hello</div>", body);
        Assert.Contains("<a href=\"https://example.com/\">fine</a>", body);
        Assert.Contains("<a>click</a>", body);
        // And the browser is told nothing on the page may run, in case anything were ever missed.
        Assert.Contains("script-src 'none'", page);
    }

    [Fact]
    public async Task An_export_file_cannot_make_Uncloud_read_anything_else()
    {
        var secret = Path.Combine(_temporary, "secret.txt");
        await File.WriteAllTextAsync(secret, "the household's passwords");
        var crafted = Export(new Note("&xxe;", "<div>&xxe;</div>"))
            .Replace("<!DOCTYPE en-export SYSTEM \"http://xml.evernote.com/pub/evernote-export4.dtd\">",
                $"<!DOCTYPE en-export [<!ENTITY xxe SYSTEM \"file://{secret}\">]>");
        Save("Crafted.enex", crafted);
        var source = Source();

        var notes = await source.ListFolderAsync("/Crafted", CancellationToken.None);
        var page = await ReadAllAsync(source, notes.Single(entry => !entry.IsFolder).Path);

        Assert.DoesNotContain("passwords", page);
        Assert.DoesNotContain("passwords", string.Join(" ", notes.Select(entry => entry.Name)));
    }

    [Fact]
    public async Task Notes_with_the_same_title_are_told_apart_and_names_are_safe_to_keep()
    {
        var first = new Resource([1, 2, 3], "image/jpeg");
        var second = new Resource([4, 5, 6], "image/jpeg");
        Save("Journal.enex", Export(
            new Note("Monday", "<div>one</div>", Created: "20240101T090000Z"),
            new Note("monday", "<div>two</div>", Created: "20240108T090000Z"),
            new Note("What next? A/B: <yes>", "<div>three</div>", [first, second]),
            new Note("...hidden", "<div>four</div>"),
            new Note("", "<div>five</div>")));
        var source = Source();

        var names = (await source.ListFolderAsync("/Journal", CancellationToken.None)).Select(entry => entry.Name).ToArray();

        Assert.Contains("Monday (2024-01-01).html", names);
        Assert.Contains("monday (2024-01-08).html", names);
        Assert.Contains("What next A B yes.html", names);
        Assert.Contains("What next A B yes files", names);
        Assert.Contains("hidden.html", names);
        Assert.Contains("Untitled note.html", names);
        // An attachment with no name of its own is named for what it is, and two never collide.
        var pictures = await source.ListFolderAsync(await PathOf(source, "/Journal", "What next A B yes files"), CancellationToken.None);
        Assert.Equal(["Picture.jpg", "Picture 2.jpg"], pictures.Select(entry => entry.Name));
    }

    [Fact]
    public async Task A_large_notebook_is_read_note_by_note()
    {
        // Bigger than one read of the file, so a note's start and end fall in different reads, and
        // the attachment is decoded as it's copied rather than held whole.
        var video = new Resource(RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17), "video/mp4", "Holiday.mp4");
        var notes = Enumerable.Range(1, 40)
            .Select(number => new Note($"Day {number}", $"<p>Day {number} of the trip.</p>",
                number == 20 ? [video] : null))
            .ToArray();
        Save("Trip.enex", Export(notes));
        var source = Source();

        var listed = await source.ListFolderAsync("/Trip", CancellationToken.None);

        Assert.Equal(40, listed.Count(entry => !entry.IsFolder));
        Assert.Equal(video.Data, await BytesAsync(source, await PathOf(source, await PathOf(source, "/Trip", "Day 20 files"), "Holiday.mp4")));
        Assert.Contains("Day 40 of the trip.", await ReadAllAsync(source, await PathOf(source, "/Trip", "Day 40.html")));
        Assert.Contains("Day 21 of the trip.", await ReadAllAsync(source, await PathOf(source, "/Trip", "Day 21.html")));
    }

    [Fact]
    public async Task A_note_that_cant_be_read_is_still_counted_and_said()
    {
        var export = Export(
            new Note("Fine", "<div>All here.</div>"),
            new Note("Broken", "<div>Unclosed <b>bold</div>"),
            new Note("Cut off", "<div>The end of the file</div>"));
        // An export that was still being written when it was copied: it stops partway through a note.
        Save("Partial.enex", export[..(export.LastIndexOf("</note>", StringComparison.Ordinal) - 20)]);
        Save("Not really.enex", "just some text with the wrong ending");
        var source = Source();

        var notebooks = await source.ListFolderAsync("/", CancellationToken.None);
        Assert.Equal("Not an Evernote export", notebooks.Single(entry => entry.Name == "Not really").Detail);
        Assert.Equal("3 notes · 1 couldn’t be fully read", notebooks.Single(entry => entry.Name == "Partial").Detail);

        // Markup that isn't well formed still gives its words.
        var broken = await ReadAllAsync(source, await PathOf(source, "/Partial", "Broken.html"));
        Assert.Contains("This note’s formatting couldn’t be read, so only its text is shown.", broken);
        Assert.Contains("Unclosed bold", broken);
        var cut = await ReadAllAsync(source, await PathOf(source, "/Partial", "Cut off.html"));
        // Cut off in the file itself, so the file isn't offered as the complete copy.
        Assert.Contains("This note is cut off in Partial.enex. Export the notebook again to get all of it.", cut);
    }

    [Fact]
    public async Task Notes_made_the_same_second_with_the_same_title_keep_their_paths_when_another_is_added()
    {
        var first = new Note("Untitled", "<div>one</div>", Created: "20200101T000000Z");
        var second = new Note("Untitled", "<div>two</div>", Created: "20200101T000000Z");
        var third = new Note("Untitled", "<div>three</div>", Created: "20200101T000000Z");
        Save("Imported.enex", Export(first, second));
        var before = (await Source().ListFolderAsync("/Imported", CancellationToken.None)).Select(entry => entry.Path).ToHashSet();

        // A newer export with another of them first: the two already there are known by the same paths.
        Save("Imported.enex", Export(third, first, second));
        var after = (await Source().ListFolderAsync("/Imported", CancellationToken.None)).Select(entry => entry.Path).ToArray();

        Assert.Equal(3, after.Length);
        Assert.Subset(after.ToHashSet(), before);
    }

    [Fact]
    public async Task Names_fit_on_any_disk_however_many_bytes_their_characters_take()
    {
        // 150 characters, each three bytes: under any count of characters, but 450 bytes, and a disk
        // takes 255 for a name.
        var title = new string('漢', 150);
        Save("Notes.enex", Export(new Note(title, "<div>text</div>",
            [new Resource([1, 2, 3], "image/jpeg", new string('写', 120) + ".jpeg")])));
        var source = Source();

        var listed = await source.ListFolderAsync("/Notes", CancellationToken.None);
        var folder = listed.Single(entry => entry.IsFolder);
        var picture = Assert.Single(await source.ListFolderAsync(folder.Path, CancellationToken.None));

        foreach (var name in listed.Select(entry => entry.Name).Append(picture.Name))
            Assert.InRange(Encoding.UTF8.GetByteCount(name), 1, 255);
        // Shortened from its middle-to-end, keeping what kind of file it is.
        Assert.EndsWith(".jpeg", picture.Name);
    }

    [Fact]
    public async Task An_attachment_stays_on_its_page_when_the_note_has_to_be_shown_as_text()
    {
        var photo = new Resource(Photo, "image/png", "photo.png");
        Save("Recipes.enex", Export(new Note("Pad thai",
            $"<div><en-media hash=\"{photo.Hash}\" type=\"image/png\"/></div><div>Unclosed <b>bold</div>", [photo])));
        var source = Source();

        var page = await ReadAllAsync(source, await PathOf(source, "/Recipes", "Pad thai.html"));

        Assert.Contains("This note’s formatting couldn’t be read", page);
        Assert.Contains("<h2>Attachments</h2>", page);
        Assert.Contains("<img src=\"Pad%20thai%20files/photo.png\" alt=\"photo.png\">", page);
    }

    [Fact]
    public async Task Notebooks_come_home_through_Add_files_and_only_new_notes_come_the_second_time()
    {
        var config = Path.Combine(_temporary, "Config");
        var host = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        using var app = new TestHost(config, _ => new StubDropbox());
        using var client = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(client, host);
        var root = await TestHost.UserRootAsync(client);

        // The folder the export files go into is made for them, and making it twice is no problem.
        (await client.PostAsync("/api/imports/evernote/folder", null)).EnsureSuccessStatusCode();
        var made = await client.PostAsync("/api/imports/evernote/folder", null);
        made.EnsureSuccessStatusCode();
        Assert.Equal("Evernote", (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString());
        var sources = await client.GetFromJsonAsync<JsonElement>("/api/imports/sources");
        Assert.Equal("Evernote", sources.GetProperty("evernote").GetProperty("folder").GetString());

        var photo = new Resource(Photo, "image/png", "photo.png");
        var export = Path.Combine(root, "Evernote", "Recipes.enex");
        await File.WriteAllTextAsync(export, Export(
            new Note("Pad thai", $"<div>Noodles</div><en-media hash=\"{photo.Hash}\" type=\"image/png\"/>", [photo])));

        var notebooks = await client.GetFromJsonAsync<JsonElement[]>("/api/imports/sources/evernote/files?path=");
        Assert.Equal("1 note", Assert.Single(notebooks!).GetProperty("detail").GetString());
        var size = await client.GetFromJsonAsync<JsonElement>("/api/imports/sources/evernote/size?path=%2F");
        Assert.Equal(2, size.GetProperty("files").GetInt32());

        var job = await BringHomeAsync(client, "/");
        Assert.Equal("Done", job.GetProperty("stage").GetString());
        Assert.Equal(2, job.GetProperty("result").GetProperty("importedCount").GetInt32());
        Assert.Contains("Noodles", await File.ReadAllTextAsync(Path.Combine(root, "Evernote", "Recipes", "Pad thai.html")));
        Assert.Equal(Photo, await File.ReadAllBytesAsync(Path.Combine(root, "Evernote", "Recipes", "Pad thai files", "photo.png")));
        // The export file stays, beside the notebook made from it: it's the complete copy.
        Assert.True(File.Exists(export));

        // A newer export of the same notebook, with a note added since: only that one is new.
        await File.WriteAllTextAsync(export, Export(
            new Note("Pad thai", $"<div>Noodles</div><en-media hash=\"{photo.Hash}\" type=\"image/png\"/>", [photo]),
            new Note("Green curry", "<div>Paste</div>")));
        var again = await BringHomeAsync(client, "/");
        Assert.Equal(1, again.GetProperty("result").GetProperty("importedCount").GetInt32());
        Assert.True(File.Exists(Path.Combine(root, "Evernote", "Recipes", "Green curry.html")));
    }

    [Fact]
    public async Task A_newer_export_with_a_note_of_the_same_title_brings_it_without_disturbing_the_first()
    {
        var config = Path.Combine(_temporary, "Config");
        var host = PathPolicy.NormalizeRoot(Directory.CreateDirectory(Path.Combine(_temporary, "Host")).FullName);
        using var app = new TestHost(config, _ => new StubDropbox());
        using var client = await app.SignUpAsync();
        await TestHost.SetHostRootAsync(client, host);
        var root = await TestHost.UserRootAsync(client);
        (await client.PostAsync("/api/imports/evernote/folder", null)).EnsureSuccessStatusCode();
        var export = Path.Combine(root, "Evernote", "Recipes.enex");
        var old = new Note("Pad thai", "<div>Grandma’s version</div>", Created: "20190301T120000Z");

        await File.WriteAllTextAsync(export, Export(old));
        await BringHomeAsync(client, "/");
        var page = Path.Combine(root, "Evernote", "Recipes", "Pad thai.html");
        Assert.Contains("Grandma", await File.ReadAllTextAsync(page));

        // A second note with the same title, made later, and first in the newer export: it arrives
        // under a name of its own, and the first keeps the name it already had.
        var newer = new Note("Pad thai", "<div>Quicker version</div>", Created: "20261101T080000Z");
        await File.WriteAllTextAsync(export, Export(newer, old));
        var again = await BringHomeAsync(client, "/");

        Assert.Equal(1, again.GetProperty("result").GetProperty("importedCount").GetInt32());
        Assert.Contains("Grandma", await File.ReadAllTextAsync(page));
        Assert.Contains("Quicker", await File.ReadAllTextAsync(Path.Combine(root, "Evernote", "Recipes", "Pad thai (2026-11-01).html")));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "Evernote", "Recipes")).Length);

        // And the same export again brings nothing.
        var once_more = await BringHomeAsync(client, "/");
        Assert.Equal(0, once_more.GetProperty("result").GetProperty("importedCount").GetInt32());
    }

    private static async Task<JsonElement> BringHomeAsync(HttpClient client, string remotePath)
    {
        var started = await client.PostAsJsonAsync("/api/imports", new { remotePath, source = "evernote", label = "Evernote" });
        started.EnsureSuccessStatusCode();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = (await client.GetFromJsonAsync<JsonElement>("/api/imports/job")).GetProperty("job");
            if (job.ValueKind != JsonValueKind.Null && !job.GetProperty("running").GetBoolean()) return job;
            await Task.Delay(15);
        }
        throw new TimeoutException("The import never finished.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_temporary, true); } catch (IOException) { }
    }
}
