using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Homebase.Core.Providers;

/// <summary>
/// Makes one Evernote note into a web page that opens in any browser, now or in twenty years, with
/// no Evernote and no Uncloud: its text and formatting, its pictures shown where they were, and its
/// other attachments linked, all from the files kept beside it.
///
/// A note's text is Evernote's own markup — a small, strict subset of HTML — and an export file is
/// something a person may have been sent, so nothing in it is passed through as it came. Only the
/// elements and attributes a note can have are written out, links only to the web and to email, and
/// nothing that can run: no scripts, no event handlers, no frames, no forms, no styles that can
/// fetch anything. The page says so to the browser as well, in case anything were ever missed.
/// </summary>
public static partial class EvernotePage
{
    /// <summary>An attachment as the page refers to it: where it is from the page, and what it is.</summary>
    public sealed record Attachment(string Href, string Name, string Mime, long Size);

    private static readonly HashSet<string> Kept = new(StringComparer.Ordinal)
    {
        "a", "abbr", "acronym", "address", "b", "bdo", "big", "blockquote", "br", "caption", "center", "cite",
        "code", "col", "colgroup", "dd", "del", "dfn", "div", "dl", "dt", "em", "font", "h1", "h2", "h3", "h4",
        "h5", "h6", "hr", "i", "img", "ins", "kbd", "li", "ol", "p", "pre", "q", "s", "samp", "small", "span",
        "strike", "strong", "sub", "sup", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "tt", "u", "ul",
        "var",
    };

    /// <summary>Elements left out along with everything inside them: what they hold isn't the note's text.</summary>
    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal)
    {
        "script", "style", "title", "head", "meta", "link", "base", "iframe", "frame", "frameset", "object",
        "embed", "applet", "param", "form", "input", "button", "select", "option", "optgroup", "textarea",
        "noscript", "template", "svg", "math", "audio", "video", "source", "track", "canvas", "dialog", "map",
    };

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal) { "br", "hr", "img", "col" };

    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    {
        "style", "title", "dir", "lang", "align", "valign", "width", "height", "colspan", "rowspan",
        "cellpadding", "cellspacing", "border", "bgcolor", "color", "face", "size", "start", "type", "alt",
        "abbr", "scope", "headers", "nowrap", "span", "clear", "compact", "reversed", "value",
    };

    /// <param name="notebook">The notebook it came from, said at the top of the page.</param>
    /// <param name="exportName">The export file it came from, for when it couldn't all be read.</param>
    /// <param name="attachments">The note's attachments, in the order the note has them.</param>
    public static string Render(EnexNote note, string notebook, string exportName, IReadOnlyList<Attachment> attachments)
    {
        var title = note.Title.Length > 0 ? note.Title : "Untitled note";
        var byHash = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < note.Resources.Count && index < attachments.Count; index++)
            byHash.TryAdd(note.Resources[index].Hash, index);
        var shown = new HashSet<int>();

        var body = new StringBuilder();
        var readable = note.Content.Length == 0 || TryContent(note.Content, byHash, attachments, shown, body);
        if (!readable)
        {
            body.Clear();
            body.Append("<p class=\"problem\">This note’s formatting couldn’t be read, so only its text is shown.</p>")
                .Append("<div class=\"plain\">").Append(Escape(PlainText(note.Content))).Append("</div>");
        }

        var page = new StringBuilder();
        page.Append("<!doctype html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            // A note never needs to run anything. Said to the browser too, in case anything got past.
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'none'; object-src 'none'; ")
            .Append("frame-src 'none'; base-uri 'none'; form-action 'none'\">\n")
            .Append("<meta name=\"referrer\" content=\"no-referrer\">\n")
            .Append("<meta name=\"generator\" content=\"Uncloud, from an Evernote export\">\n");
        if (note.Created is { } createdAt) page.Append($"<meta name=\"created\" content=\"{Iso(createdAt)}\">\n");
        if (note.Updated is { } updatedAt) page.Append($"<meta name=\"updated\" content=\"{Iso(updatedAt)}\">\n");
        if (note.Tags.Count > 0) page.Append($"<meta name=\"keywords\" content=\"{Escape(string.Join(", ", note.Tags))}\">\n");
        page.Append("<title>").Append(Escape(title)).Append("</title>\n<style>").Append(Css).Append("</style>\n</head>\n<body>\n<article class=\"page\">\n<header>\n<h1>")
            .Append(Escape(title)).Append("</h1>\n<p class=\"about\">").Append(Escape(notebook));
        if (note.Created is { } created) page.Append(" · Created ").Append(Day(created));
        // Only worth saying when it's a different day: most notes are last changed the day they're made.
        if (note.Updated is { } updated && (note.Created is not { } made || Day(updated) != Day(made)))
            page.Append(" · Updated ").Append(Day(updated));
        page.Append("</p>\n");
        if (note.Tags.Count > 0) page.Append("<p class=\"about\">Tags: ").Append(Escape(string.Join(", ", note.Tags))).Append("</p>\n");
        if (note.Author is { } author) page.Append("<p class=\"about\">By ").Append(Escape(author)).Append("</p>\n");
        if (Web(note.SourceUrl) is { } source)
            page.Append("<p class=\"about\">Saved from <a href=\"").Append(Escape(source)).Append("\">")
                .Append(Escape(Host(source))).Append("</a></p>\n");
        page.Append("</header>\n");
        if (note.Problem is { } problem)
            page.Append("<p class=\"problem\">").Append(problem == EnexProblem.CutOff
                    ? $"This note is cut off in {Escape(exportName)}. Export the notebook again to get all of it."
                    : $"Part of this note couldn’t be read. All of it is still in {Escape(exportName)}.")
                .Append("</p>\n");
        page.Append("<main>\n").Append(body).Append("\n</main>\n");

        if (note.Tasks.Count > 0)
        {
            page.Append("<section class=\"tasks\">\n<h2>Tasks</h2>\n<ul>\n");
            foreach (var task in note.Tasks)
                page.Append("<li><input type=\"checkbox\" disabled").Append(task.Done ? " checked" : "").Append("> ")
                    .Append(Escape(task.Title)).Append("</li>\n");
            page.Append("</ul>\n</section>\n");
        }

        // Attached to the note but never placed in its text: still the note's, so still shown.
        var rest = Enumerable.Range(0, attachments.Count).Where(index => !shown.Contains(index)).ToArray();
        if (rest.Length > 0)
        {
            page.Append("<section>\n<h2>Attachments</h2>\n<ul class=\"attachments\">\n");
            foreach (var index in rest) page.Append("<li>").Append(Media(attachments[index], null)).Append("</li>\n");
            page.Append("</ul>\n</section>\n");
        }
        page.Append("</article>\n</body>\n</html>\n");
        return page.ToString();
    }

    private const string Css =
        ":root{color-scheme:light}"
        + "body{margin:0;background:#fff;color:#1f2328;font:17px/1.55 -apple-system,BlinkMacSystemFont,\"Segoe UI\",Roboto,Helvetica,Arial,sans-serif}"
        + ".page{max-width:46rem;margin:0 auto;padding:2rem 1.25rem 4rem}"
        + "header{border-bottom:1px solid #e4e4e7;margin-bottom:1.5rem;padding-bottom:1rem}"
        + "h1{font-size:1.75rem;line-height:1.25;margin:0 0 .5rem}"
        + "h2{font-size:1.15rem;margin:2rem 0 .5rem}"
        + ".about{color:#5f6368;font-size:.9rem;margin:.2rem 0}"
        + "img,video{max-width:100%;height:auto}"
        + "audio{display:block;margin:.5rem 0}"
        + "table{border-collapse:collapse;max-width:100%}td,th{border:1px solid #d0d7de;padding:.3rem .5rem;vertical-align:top}"
        + "pre,.codeblock{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:.9em;background:#f6f8fa;border-radius:6px;padding:.75rem 1rem;overflow-x:auto}"
        + ".codeblock,.plain{white-space:pre-wrap}"
        + "blockquote{border-left:3px solid #d0d7de;margin-left:0;padding-left:1rem;color:#444}"
        + "a.attachment::before{content:\"\\1F4CE  \"}"
        + ".size{color:#5f6368;font-size:.85em}"
        + ".problem{background:#fff4e5;border:1px solid #f5c26b;border-radius:6px;padding:.6rem .8rem}"
        + ".encrypted{color:#5f6368;font-style:italic}"
        + ".tasks ul,ul.attachments{list-style:none;padding-left:0}";

    /// <summary>
    /// The note's text as a page body, or false when it isn't the markup it should be. Whatever was
    /// written before the problem is left in <paramref name="html"/> for the caller to throw away.
    /// </summary>
    private static bool TryContent(
        string content, IReadOnlyDictionary<string, int> byHash, IReadOnlyList<Attachment> attachments,
        ISet<int> shown, StringBuilder html)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(Prepare(content)), EvernoteExport.Settings);
            var open = new Stack<string?>();
            reader.Read();
            while (!reader.EOF)
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var name = reader.LocalName.ToLowerInvariant();
                        if (name == "en-media")
                        {
                            var hash = reader.GetAttribute("hash") ?? "";
                            if (byHash.TryGetValue(hash, out var index))
                            {
                                shown.Add(index);
                                html.Append(Media(attachments[index], reader));
                            }
                            else html.Append("<span class=\"encrypted\">[Missing attachment]</span>");
                            reader.Skip();
                        }
                        else if (name == "en-todo")
                        {
                            var done = string.Equals(reader.GetAttribute("checked"), "true", StringComparison.OrdinalIgnoreCase);
                            html.Append("<input type=\"checkbox\" disabled").Append(done ? " checked" : "").Append("> ");
                            reader.Skip();
                        }
                        else if (name == "en-crypt")
                        {
                            html.Append("<span class=\"encrypted\">[Encrypted text. You need your Evernote passphrase to read it.]</span>");
                            reader.Skip();
                        }
                        else if (Dropped.Contains(name)) reader.Skip();
                        else if (Kept.Contains(name))
                        {
                            html.Append('<').Append(name);
                            WriteAttributes(reader, name, html);
                            html.Append('>');
                            if (Void.Contains(name)) reader.Skip();
                            else if (reader.IsEmptyElement)
                            {
                                html.Append("</").Append(name).Append('>');
                                reader.Read();
                            }
                            else
                            {
                                open.Push(name);
                                reader.Read();
                            }
                        }
                        else
                        {
                            // Evernote's own wrapper, and anything else unknown: its contents are kept, it isn't.
                            if (reader.IsEmptyElement) reader.Read();
                            else
                            {
                                open.Push(null);
                                reader.Read();
                            }
                        }
                        break;
                    case XmlNodeType.EndElement:
                        if (open.TryPop(out var closing) && closing is not null) html.Append("</").Append(closing).Append('>');
                        reader.Read();
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        html.Append(Escape(reader.Value));
                        reader.Read();
                        break;
                    default:
                        reader.Read();
                        break;
                }
            }
            while (open.TryPop(out var unclosed))
                if (unclosed is not null) html.Append("</").Append(unclosed).Append('>');
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static void WriteAttributes(XmlReader reader, string element, StringBuilder html)
    {
        var codeblock = false;
        if (!reader.MoveToFirstAttribute()) return;
        do
        {
            var name = reader.LocalName.ToLowerInvariant();
            var value = reader.Value;
            string? kept = null;
            if (name == "href" && element == "a") kept = Link(value);
            else if (name == "src" && element == "img") kept = Picture(value);
            else if (name == "style")
            {
                kept = Style(value);
                codeblock |= Regex.IsMatch(value, @"--en-codeblock\s*:\s*true", RegexOptions.IgnoreCase);
            }
            else if (Attributes.Contains(name)) kept = value;
            if (kept is not null) html.Append(' ').Append(name).Append("=\"").Append(Escape(kept)).Append('"');
        }
        while (reader.MoveToNextAttribute());
        reader.MoveToElement();
        if (codeblock) html.Append(" class=\"codeblock\"");
    }

    /// <summary>An attachment where the note placed it: a picture shown, a recording playable, anything else linked.</summary>
    private static string Media(Attachment attachment, XmlReader? placed)
    {
        var href = Escape(attachment.Href);
        var name = Escape(attachment.Name);
        if (attachment.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            var image = new StringBuilder($"<img src=\"{href}\" alt=\"{name}\"");
            if (placed is not null)
            {
                foreach (var dimension in new[] { "width", "height" })
                    if (placed.GetAttribute(dimension) is { } value && Regex.IsMatch(value, @"^\s*\d+(\.\d+)?(px|%)?\s*$"))
                        image.Append(' ').Append(dimension).Append("=\"").Append(Escape(value.Trim())).Append('"');
                if (placed.GetAttribute("style") is { } style && Style(style) is { } safe)
                    image.Append(" style=\"").Append(Escape(safe)).Append('"');
            }
            return image.Append('>').ToString();
        }
        var link = $"<a class=\"attachment\" href=\"{href}\">{name}</a> <span class=\"size\">{Escape(Storage.Describe(attachment.Size))}</span>";
        if (attachment.Mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return $"<audio controls preload=\"none\" src=\"{href}\"></audio>{link}";
        if (attachment.Mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return $"<video controls preload=\"none\" src=\"{href}\"></video>{link}";
        return link;
    }

    /// <summary>
    /// The note's markup, ready for a reader that knows no DTD. Evernote's DTD is what declares the
    /// named entities HTML has — &amp;nbsp; above all, which nearly every note uses — so without it each
    /// is written out as the character it stands for. The declaration and DOCTYPE go too.
    /// </summary>
    private static string Prepare(string content)
    {
        var text = Declarations().Replace(content, "");
        return NamedEntity().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            if (name is "lt" or "gt" or "amp" or "quot" or "apos") return match.Value;
            var decoded = WebUtility.HtmlDecode(match.Value);
            if (decoded == match.Value) return "&amp;" + name + ";";
            var numeric = new StringBuilder();
            foreach (var rune in decoded.EnumerateRunes()) numeric.Append("&#").Append(rune.Value).Append(';');
            return numeric.ToString();
        });
    }

    /// <summary>What's left of a note when its markup can't be read: the words, with the tags taken out.</summary>
    private static string PlainText(string content)
    {
        var text = Declarations().Replace(content, "");
        text = BlockEnd().Replace(text, "\n");
        text = Tag().Replace(text, "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    /// <summary>
    /// A style, unless it could fetch something or be read as something else. Colours, sizes and
    /// highlights — all a note's styles are for — never need any of what's refused here.
    /// </summary>
    private static string? Style(string value)
    {
        var squashed = CssNoise().Replace(value, "").ToLowerInvariant();
        if (squashed.Contains("url(") || squashed.Contains("expression(") || squashed.Contains("javascript:")
            || squashed.Contains("@import") || squashed.Contains("behavior:") || squashed.Contains("-moz-binding")
            || squashed.Contains("image-set(") || value.Contains('\\') || value.Contains('<'))
            return null;
        return value;
    }

    /// <summary>A link to the web, an email address or a phone number, and nothing else.</summary>
    private static string? Link(string value)
    {
        // Browsers ignore spaces and control characters inside a scheme, so "java script:" is read
        // as "javascript:". Taken out before looking, so what is checked is what a browser would see.
        var clean = Squash(value);
        return clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || clean.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
               || clean.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
            ? clean
            : null;
    }

    private static string? Picture(string value)
    {
        var clean = Squash(value);
        return clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || clean.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            ? clean
            : null;
    }

    private static string? Web(string? value) =>
        value is null ? null : Link(value) is { } link && link.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? link : null;

    private static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : url;

    private static string Squash(string value)
    {
        var kept = new StringBuilder(value.Length);
        foreach (var character in value)
            if (character > ' ' && !char.IsControl(character)) kept.Append(character);
        return kept.ToString();
    }

    /// <summary>Escaped for HTML text or a quoted attribute; everything else is written as itself.</summary>
    internal static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var character in text)
            escaped.Append(character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                _ => character.ToString(),
            });
        return escaped.ToString();
    }

    private static string Day(DateTimeOffset date) => date.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
    private static string Iso(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"<\?xml[^>]*\?>|<!DOCTYPE[^>\[]*(\[[^\]]*\])?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex Declarations();

    [GeneratedRegex(@"&([A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex NamedEntity();

    [GeneratedRegex(@"</(p|div|li|tr|h[1-6]|blockquote|pre)\s*>|<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssNoise();
}
