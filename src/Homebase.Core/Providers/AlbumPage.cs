using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Homebase.Core.Providers;

/// <summary>
/// The page an album from an export becomes. Each photo arrives once, in the folder for the year it
/// was taken, so an album is a page showing its photos where they are, by links beside it in My
/// files, rather than a second copy of every one. It opens in any browser on a computer that syncs
/// with Uncloud, and it is only words and links: nothing in an export, which may have come from
/// anybody, gets to run.
/// </summary>
public static partial class AlbumPage
{
    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".bmp", ".tif", ".tiff",
    };

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".webm",
    };

    /// <param name="album">What the album is called where it came from.</param>
    /// <param name="service">Where it came from, such as iCloud.</param>
    /// <param name="folder">The folder the page goes in, which its links are made from.</param>
    /// <param name="shown">Each photo or video in it, by where it lands and its name.</param>
    public static string Render(string album, string service, string folder, IReadOnlyList<(string Path, string Name)> shown)
    {
        var up = string.Concat(Enumerable.Repeat("../", folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Length));
        var page = new StringBuilder();
        page.Append("<!doctype html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            // An album never needs to run anything. Said to the browser too, in case anything got past.
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'none'; object-src 'none'; ")
            .Append("frame-src 'none'; base-uri 'none'; form-action 'none'\">\n")
            .Append("<meta name=\"referrer\" content=\"no-referrer\">\n")
            .Append("<meta name=\"generator\" content=\"Uncloud, from an export of ").Append(Escape(service)).Append("\">\n")
            .Append("<title>").Append(Escape(album)).Append("</title>\n<style>").Append(Css).Append("</style>\n</head>\n<body>\n")
            .Append("<main class=\"page\">\n<h1>").Append(Escape(album)).Append("</h1>\n<p class=\"about\">")
            .Append(shown.Count == 1 ? "1 photo or video" : $"{shown.Count:N0} photos and videos")
            .Append(" in this album on ").Append(Escape(service))
            .Append(". Each one is kept once, in the folder for the year it was taken; this page shows them together.</p>\n")
            .Append("<ul class=\"grid\">\n");
        foreach (var (path, name) in shown)
        {
            var link = up + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            var ending = Path.GetExtension(name);
            page.Append("<li><a href=\"").Append(Escape(link)).Append("\">");
            if (Pictures.Contains(ending))
                page.Append("<img src=\"").Append(Escape(link)).Append("\" loading=\"lazy\" alt=\"\">");
            else if (Videos.Contains(ending))
                page.Append("<video src=\"").Append(Escape(link)).Append("\" preload=\"metadata\" muted></video>");
            page.Append("<span>").Append(Escape(name)).Append("</span></a></li>\n");
        }
        return page.Append("</ul>\n</main>\n</body>\n</html>\n").ToString();
    }

    /// <summary>
    /// What a page made by <see cref="Render"/> shows, by where each photo is relative to the
    /// export's folder and its name, read back from its links. Anything else in it is passed over.
    /// </summary>
    public static IReadOnlyList<(string Path, string Name)> Shown(string page, string folder)
    {
        var up = folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        var shown = new List<(string, string)>();
        foreach (Match match in Item().Matches(page))
        {
            var parts = WebUtility.HtmlDecode(match.Groups["link"].Value).Split('/');
            if (parts.Length <= up || parts.Take(up).Any(part => part != "..")) continue;
            var path = string.Join('/', parts.Skip(up).Select(Uri.UnescapeDataString));
            if (path.Split('/').Any(part => part is "" or "." or "..")) continue;
            shown.Add((path, WebUtility.HtmlDecode(match.Groups["name"].Value)));
        }
        return shown;
    }

    [GeneratedRegex("""<li><a href="(?<link>[^"]*)">.*?<span>(?<name>[^<]*)</span></a></li>""")]
    private static partial Regex Item();

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    private const string Css =
        ":root{color-scheme:light}"
        + "body{margin:0;background:#fff;color:#1f2328;font:16px/1.5 -apple-system,BlinkMacSystemFont,\"Segoe UI\",Roboto,Helvetica,Arial,sans-serif}"
        + ".page{max-width:72rem;margin:0 auto;padding:2rem 1.25rem 4rem}"
        + "h1{font-size:1.9rem;line-height:1.2;margin:0 0 .4rem}"
        + ".about{color:#59636e;margin:0 0 1.5rem}"
        + ".grid{list-style:none;margin:0;padding:0;display:grid;grid-template-columns:repeat(auto-fill,minmax(10rem,1fr));gap:1rem}"
        + ".grid a{display:grid;gap:.35rem;color:inherit;text-decoration:none}"
        + ".grid img,.grid video{width:100%;aspect-ratio:1;object-fit:cover;border-radius:.4rem;background:#eef1f4}"
        + ".grid span{font-size:.8rem;color:#59636e;overflow-wrap:anywhere}";
}
