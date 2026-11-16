namespace DeskPal.Core;

public sealed record LanguageInfo(string Tag, string NativeName, string EnglishName);

public static class SupportedLanguages
{
    public const string Fallback = "en-US";

    // Append a row here when a translation is added to both resource sets.
    public static readonly IReadOnlyList<LanguageInfo> All =
    [
        new("en-US", "English", "English"),
        new("es-ES", "Español", "Spanish"),
        new("de-DE", "Deutsch", "German"),
        new("fr-FR", "Français", "French"),
        new("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
        new("it-IT", "Italiano", "Italian"),
        new("nl-NL", "Nederlands", "Dutch"),
        new("pl-PL", "Polski", "Polish"),
        new("tr-TR", "Türkçe", "Turkish"),
        new("ja-JP", "日本語", "Japanese"),
        new("ko-KR", "한국어", "Korean"),
        new("zh-Hans", "简体中文", "Chinese (Simplified)"),
        new("ru-RU", "Русский", "Russian"),
    ];
}
