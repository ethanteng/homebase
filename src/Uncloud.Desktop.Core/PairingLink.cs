namespace Uncloud.Desktop;

/// <summary>
/// Where to pair and with what code, however the person brought it: the link Uncloud offers
/// (uncloud://pair?address=…&amp;code=…), or an address and a code typed separately.
/// </summary>
public sealed record PairingLink(Uri Address, string Code)
{
    public const string Scheme = "uncloud";

    public static PairingLink Parse(string link)
    {
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("pair", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("That isn’t an Uncloud pairing link. Copy it again from My computers.");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
                pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "",
                StringComparer.OrdinalIgnoreCase);
        return From(query.GetValueOrDefault("address"), query.GetValueOrDefault("code"));
    }

    public static PairingLink From(string? address, string? code)
    {
        var text = (address ?? "").Trim().TrimEnd('/');
        if (text.Length > 0 && !text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FormatException("Enter the address you open Uncloud at, such as https://uncloud.example.ts.net.");
        if (string.IsNullOrWhiteSpace(code))
            throw new FormatException("Enter the pairing code from My computers.");
        return new PairingLink(new Uri(uri.GetLeftPart(UriPartial.Authority)), code.Trim());
    }

    public string ToLink() =>
        $"{Scheme}://pair?address={Uri.EscapeDataString(Address.ToString().TrimEnd('/'))}&code={Uri.EscapeDataString(Code)}";
}
