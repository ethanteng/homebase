namespace Homebase.Core.Sharing;

/// <summary>
/// Which files the browser shows rather than only offering them to download, and as what, in My files
/// and through a shared link alike. Everything else Uncloud sends is a download, so that nothing anybody puts in their folder
/// ever runs on Uncloud's own address; these are the exceptions, and only kinds that can't run
/// anything there: pictures, sound, video, PDFs and plain text. HTML, SVG and the like are never
/// among them, whatever they're called — a visitor signed in to this Uncloud in the same browser would
/// otherwise be running the page as themselves.
/// </summary>
public static class SharePreview
{
    private static readonly Dictionary<string, (string Kind, string Type)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = ("image", "image/jpeg"),
        [".jpeg"] = ("image", "image/jpeg"),
        [".png"] = ("image", "image/png"),
        [".gif"] = ("image", "image/gif"),
        [".webp"] = ("image", "image/webp"),
        [".avif"] = ("image", "image/avif"),
        [".bmp"] = ("image", "image/bmp"),
        // Only Safari draws these; elsewhere the page says it can't.
        [".heic"] = ("image", "image/heic"),
        [".heif"] = ("image", "image/heif"),
        [".mp4"] = ("video", "video/mp4"),
        [".m4v"] = ("video", "video/mp4"),
        [".mov"] = ("video", "video/quicktime"),
        [".webm"] = ("video", "video/webm"),
        [".ogv"] = ("video", "video/ogg"),
        [".mp3"] = ("audio", "audio/mpeg"),
        [".m4a"] = ("audio", "audio/mp4"),
        [".aac"] = ("audio", "audio/aac"),
        [".wav"] = ("audio", "audio/wav"),
        [".flac"] = ("audio", "audio/flac"),
        [".ogg"] = ("audio", "audio/ogg"),
        [".oga"] = ("audio", "audio/ogg"),
        [".opus"] = ("audio", "audio/ogg"),
        [".pdf"] = ("pdf", "application/pdf"),
        // Sent as plain text whatever they hold, and put on the page as text, never as markup.
        [".txt"] = ("text", "text/plain; charset=utf-8"),
        [".md"] = ("text", "text/plain; charset=utf-8"),
        [".markdown"] = ("text", "text/plain; charset=utf-8"),
        [".csv"] = ("text", "text/plain; charset=utf-8"),
        [".tsv"] = ("text", "text/plain; charset=utf-8"),
        [".log"] = ("text", "text/plain; charset=utf-8"),
        [".json"] = ("text", "text/plain; charset=utf-8")
    };

    /// <summary>How a file of this name is shown: image, video, audio, pdf or text, or null when it isn't.</summary>
    public static string? Kind(string name) =>
        Known.TryGetValue(Path.GetExtension(name), out var known) ? known.Kind : null;

    /// <summary>What it's sent as when shown, or null when it's only ever downloaded.</summary>
    public static string? ContentType(string name) =>
        Known.TryGetValue(Path.GetExtension(name), out var known) ? known.Type : null;

    /// <summary>
    /// The Content-Security-Policy a shown file is sent with, in case it's opened on its own rather
    /// than in the page: nothing loads, nothing runs, and it can't act as this address. A PDF is the
    /// one exception, because a browser's own PDF viewer won't open under a sandbox, and the page
    /// shows it in a frame; the viewer doesn't give a PDF this address to run as either.
    /// </summary>
    public static string Policy(string kind) => kind == "pdf"
        ? "default-src 'none'; frame-ancestors 'self'"
        : "default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'self'; sandbox";
}
