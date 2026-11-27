using System.Globalization;

namespace DeskPal.Core;

// One row of the language list: a language, or the divider between the top group and the rest.
public sealed record LanguageRow(LanguageInfo? Language)
{
    public bool IsDivider => Language is null;
}

public static class LanguageList
{
    // The supported language that best fits a Windows language tag: the full tag first, then the base language.
    // Chinese goes to Traditional for zh-TW, zh-HK, zh-MO and zh-Hant-*, and to Simplified for everything else.
    public static LanguageInfo? Match(string? tag, IReadOnlyList<LanguageInfo> supported)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        var exact = supported.FirstOrDefault(l => string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var parts = tag.Split('-', '_');
        var baseLang = parts[0];
        if (string.Equals(baseLang, "zh", StringComparison.OrdinalIgnoreCase))
        {
            var want = IsSimplifiedChinese(parts) ? "zh-Hans" : "zh-Hant";
            return supported.FirstOrDefault(l => string.Equals(l.Tag, want, StringComparison.OrdinalIgnoreCase));
        }
        return supported.FirstOrDefault(l => string.Equals(l.Tag.Split('-')[0], baseLang, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSimplifiedChinese(string[] parts)
    {
        foreach (var p in parts.Skip(1))
        {
            if (p.Equals("Hant", StringComparison.OrdinalIgnoreCase)) return false;
            if (p.Equals("Hans", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return !parts.Skip(1).Any(p => p.Equals("TW", StringComparison.OrdinalIgnoreCase)
            || p.Equals("HK", StringComparison.OrdinalIgnoreCase) || p.Equals("MO", StringComparison.OrdinalIgnoreCase));
    }

    // The best supported tag for the Windows display languages (in the user's order), or en-US.
    public static string ResolveStartupLanguage(IEnumerable<string> windowsLanguages, IReadOnlyList<LanguageInfo> supported)
    {
        foreach (var tag in windowsLanguages)
        {
            var match = Match(tag, supported);
            if (match is not null) return match.Tag;
        }
        return SupportedLanguages.Fallback;
    }

    // The user's own language first, then English, then a divider, then the rest sorted A-Z by native name.
    // When the user's language is English (or unsupported) it is English, the divider, then the rest.
    public static IReadOnlyList<LanguageRow> Order(IReadOnlyList<LanguageInfo> supported, string? userTag)
    {
        var english = supported.FirstOrDefault(l => l.Tag == SupportedLanguages.Fallback);
        var own = Match(userTag, supported) ?? english;
        var rows = new List<LanguageRow>();
        var used = new HashSet<string>();
        foreach (var top in new[] { own, english })
            if (top is not null && used.Add(top.Tag)) rows.Add(new LanguageRow(top));
        rows.Add(new LanguageRow(null));
        rows.AddRange(supported
            .Where(l => !used.Contains(l.Tag))
            .OrderBy(l => l.NativeName, StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true))
            .Select(l => new LanguageRow(l)));
        return rows;
    }

    // Lower-case ISO region code of the flag shown for a language. The user's own language uses their Windows region
    // when that region speaks it (Portuguese + PT shows Portugal); everything else uses the language's default.
    public static string FlagCode(LanguageInfo language, string? userTag, string? windowsRegion)
    {
        var region = windowsRegion?.Trim().ToLowerInvariant();
        var regionKey = language.Tag == "zh-Hant" ? "zh-Hant" : language.Tag.Split('-')[0].ToLowerInvariant();
        if (!string.IsNullOrEmpty(region)
            && Match(userTag, [language]) is not null
            && RegionsByLanguage.TryGetValue(regionKey, out var regions)
            && Array.IndexOf(regions, region) >= 0)
            return region;
        return language.Tag switch
        {
            "en-US" => "us",
            "es-ES" => "es",
            "de-DE" => "de",
            "fr-FR" => "fr",
            "pt-BR" => "br",
            "it-IT" => "it",
            "nl-NL" => "nl",
            "pl-PL" => "pl",
            "tr-TR" => "tr",
            "ja-JP" => "jp",
            "ko-KR" => "kr",
            "zh-Hans" => "cn",
            "ru-RU" => "ru",
            "zh-Hant" => "tw",
            "ar-SA" => "sa",
            "hi-IN" => "in",
            "id-ID" => "id",
            "vi-VN" => "vn",
            "th-TH" => "th",
            "sv-SE" => "se",
            _ => language.Tag.Split('-').Last().ToLowerInvariant(),
        };
    }

    // Every flag code FlagCode can return; the flag images in Assets\Flags are exactly this set.
    public static readonly IReadOnlyList<string> AllFlagCodes =
    [
        "us", "gb", "au", "ca", "ie", "nz", "za", "in",
        "es", "mx", "ar", "co", "cl", "pe",
        "de", "at", "ch", "fr", "be", "br", "pt", "it", "nl", "pl", "tr", "jp", "kr", "cn", "sg", "ru",
        "tw", "sa", "id", "vn", "th", "se",
    ];

    private static readonly Dictionary<string, string[]> RegionsByLanguage = new()
    {
        ["en"] = ["us", "gb", "au", "ca", "ie", "nz", "za", "in"],
        ["es"] = ["es", "mx", "ar", "co", "cl", "pe"],
        ["de"] = ["de", "at", "ch"],
        ["fr"] = ["fr", "ca", "be", "ch"],
        ["pt"] = ["br", "pt"],
        ["it"] = ["it", "ch"],
        ["nl"] = ["nl", "be"],
        ["zh"] = ["cn", "sg"],
        ["zh-Hant"] = ["tw"],
        ["ar"] = ["sa"],
        ["hi"] = ["in"],
        ["id"] = ["id"],
        ["vi"] = ["vn"],
        ["th"] = ["th"],
        ["sv"] = ["se"],
    };
}
