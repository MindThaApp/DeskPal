namespace DeskPal.Core.Integration;

// A parsed deskpal: link. Only "open" is supported; a link never changes state, it only shows a window.
public sealed record DeskPalLink(string Page, string? Section, bool Supported)
{
    public static readonly IReadOnlyList<string> Pages = ["home", "today", "bloop", "garden", "settings", "about"];

    public static bool IsLinkArg(string? arg) =>
        arg is not null && arg.TrimStart().StartsWith("deskpal:", StringComparison.OrdinalIgnoreCase);

    // The first deskpal: argument, or null.
    public static string? FindArg(IEnumerable<string> args) => args.FirstOrDefault(IsLinkArg)?.Trim();

    public static string NormalizePage(string? page)
    {
        var p = page?.Trim().ToLowerInvariant();
        return p is not null && Pages.Contains(p) ? p : "home";
    }

    public static string? NormalizeSection(string? page, string? section) =>
        page == "settings" && string.Equals(section?.Trim(), "automation", StringComparison.OrdinalIgnoreCase) ? "automation" : null;

    // Anything that isn't deskpal://open (or a bad value) falls back to Home; Supported says whether the verb was understood.
    public static DeskPalLink Parse(string? uri)
    {
        var fallback = new DeskPalLink("home", null, false);
        if (!IsLinkArg(uri)) return fallback;
        var rest = uri!.Trim()["deskpal:".Length..].TrimStart('/');
        var q = rest.IndexOf('?');
        var frag = rest.IndexOf('#');
        if (frag >= 0 && (q < 0 || frag < q)) { rest = rest[..frag]; q = -1; }
        var verb = (q >= 0 ? rest[..q] : rest).Trim('/').ToLowerInvariant();
        if (verb != "open") return fallback;
        string? page = null, section = null;
        if (q >= 0)
        {
            var query = rest[(q + 1)..];
            var f = query.IndexOf('#');
            if (f >= 0) query = query[..f];
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Decode(eq >= 0 ? pair[..eq] : pair).ToLowerInvariant();
                var value = eq >= 0 ? Decode(pair[(eq + 1)..]) : "";
                if (key == "page") page ??= value;
                else if (key == "section") section ??= value;
            }
        }
        var normalized = NormalizePage(page);
        return new DeskPalLink(normalized, NormalizeSection(normalized, section), true);
    }

    private static string Decode(string s)
    {
        try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
        catch (UriFormatException) { return s; }
    }
}
