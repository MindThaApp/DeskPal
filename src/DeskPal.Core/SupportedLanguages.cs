namespace DeskPal.Core;

public sealed record LanguageInfo(string Tag, string NativeName, string EnglishName);

public static class SupportedLanguages
{
    // Append a row here when a translation is added to both resource sets.
    public static readonly IReadOnlyList<LanguageInfo> All =
    [
        new("en-US", "English", "English"),
    ];
}
