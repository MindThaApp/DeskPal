using DeskPal.Core;

namespace DeskPal.Core.Tests;

public class LanguageTests
{
    private static readonly IReadOnlyList<LanguageInfo> All = SupportedLanguages.All;

    private static string[] Tags(IReadOnlyList<LanguageRow> rows) =>
        rows.Select(r => r.Language?.Tag ?? "|").ToArray();

    [Fact]
    public void Order_PutsUserLanguageThenEnglishThenDividerThenAlphabetical()
    {
        var rows = Tags(LanguageList.Order(All, "de-AT"));
        Assert.Equal(["de-DE", "en-US", "|"], rows.Take(3));
        var rest = LanguageList.Order(All, "de-AT").Skip(3).Select(r => r.Language!.NativeName).ToList();
        Assert.Equal(All.Count - 2, rest.Count);
        Assert.Equal(rest.OrderBy(n => n, StringComparer.InvariantCultureIgnoreCase).ToList(), rest);
    }

    [Fact]
    public void Order_EnglishUser_EnglishFirstThenDivider()
    {
        var rows = Tags(LanguageList.Order(All, "en-GB"));
        Assert.Equal(["en-US", "|"], rows.Take(2));
        Assert.Equal(All.Count + 1, rows.Length);
    }

    [Fact]
    public void Order_UnsupportedUserLanguage_FallsBackToEnglishFirst()
    {
        var rows = Tags(LanguageList.Order(All, "sv-SE"));
        Assert.Equal(["en-US", "|"], rows.Take(2));
    }

    [Fact]
    public void Match_FullTagThenBaseLanguage()
    {
        Assert.Equal("pt-BR", LanguageList.Match("pt-PT", All)!.Tag);
        Assert.Equal("es-ES", LanguageList.Match("es-MX", All)!.Tag);
        Assert.Equal("ja-JP", LanguageList.Match("ja", All)!.Tag);
        Assert.Equal("fr-FR", LanguageList.Match("FR-fr", All)!.Tag);
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    public void Match_SimplifiedChinese(string windows, string expected) =>
        Assert.Equal(expected, LanguageList.Match(windows, All)!.Tag);

    [Theory]
    [InlineData("zh-TW")]
    [InlineData("zh-Hant")]
    [InlineData("zh-HK")]
    [InlineData("")]
    [InlineData(null)]
    public void Match_NoMatch(string? windows) => Assert.Null(LanguageList.Match(windows, All));

    [Fact]
    public void ResolveStartupLanguage_FirstSupportedInUserOrder()
    {
        Assert.Equal("de-DE", LanguageList.ResolveStartupLanguage(["sv-SE", "de-DE", "fr-FR"], All));
        Assert.Equal("en-US", LanguageList.ResolveStartupLanguage(["sv-SE", "zh-TW"], All));
        Assert.Equal("en-US", LanguageList.ResolveStartupLanguage([], All));
    }

    [Theory]
    [InlineData("pt-BR", "pt-PT", "PT", "pt")]
    [InlineData("pt-BR", "pt-BR", "BR", "br")]
    [InlineData("en-US", "en-GB", "GB", "gb")]
    [InlineData("en-US", "en-US", "US", "us")]
    [InlineData("es-ES", "es-MX", "MX", "mx")]
    [InlineData("es-ES", "es-ES", "US", "es")]
    [InlineData("de-DE", "en-US", "DE", "de")]
    [InlineData("ja-JP", "ja-JP", "US", "jp")]
    [InlineData("zh-Hans", "zh-CN", "SG", "sg")]
    [InlineData("ko-KR", "ko-KR", "KR", "kr")]
    public void FlagCode_UsesRegionOnlyForOwnLanguage(string language, string userTag, string region, string expected) =>
        Assert.Equal(expected, LanguageList.FlagCode(All.First(l => l.Tag == language), userTag, region));

    [Fact]
    public void FlagCode_EveryResultIsAShippedFlag()
    {
        foreach (var l in All)
            Assert.Contains(LanguageList.FlagCode(l, null, null), LanguageList.AllFlagCodes);
    }

    private static DeskPalSettings Switched(DateTimeOffset at)
    {
        var s = new DeskPalSettings { Language = "fr-FR" };
        LanguageRevert.Begin(s, "ja-JP", at);
        return s;
    }

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Begin_RemembersPreviousAndSetsNew()
    {
        var s = Switched(T0);
        Assert.Equal("fr-FR", s.PendingLanguageRevert);
        Assert.Equal("ja-JP", s.Language);
        Assert.Equal(T0, s.PendingLanguageRevertAt);
    }

    [Fact]
    public void PreviousText_RoundTripsThroughBeginAndConfirm()
    {
        var s = new DeskPalSettings { Language = "fr-FR" };
        var text = new LanguagePromptText { Title = "Garder ?", Body = "b", Countdown = "Retour dans {0} s.", Keep = "Garder", GoBack = "Retour" };
        LanguageRevert.Begin(s, "ja-JP", T0, text);
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<DeskPalSettings>(json)!;
        Assert.Equal("Garder ?", back.PendingLanguageRevertText!.Title);
        Assert.Equal("Retour dans 7 s.", back.PendingLanguageRevertText.CountdownFor(7));
        LanguageRevert.Confirm(back);
        Assert.Null(back.PendingLanguageRevertText);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<DeskPalSettings>("{\"Language\":\"fr-FR\"}")!.PendingLanguageRevertText);
    }

    [Fact]
    public void Begin_FromFollowWindows_RevertsToEmpty()
    {
        var s = new DeskPalSettings();
        LanguageRevert.Begin(s, "ja-JP", T0);
        Assert.Equal("", s.PendingLanguageRevert);
        LanguageRevert.Revert(s);
        Assert.Equal("", s.Language);
        Assert.Null(s.PendingLanguageRevert);
    }

    [Fact]
    public void Evaluate_NothingPending()
    {
        Assert.Equal(RevertAction.None, LanguageRevert.Evaluate(new DeskPalSettings(), T0).Action);
    }

    [Fact]
    public void Evaluate_WithinWindow_AsksWithRemainingTime()
    {
        var d = LanguageRevert.Evaluate(Switched(T0), T0.AddSeconds(5));
        Assert.Equal(RevertAction.AskToKeep, d.Action);
        Assert.Equal(TimeSpan.FromSeconds(25), d.Remaining);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(3600)]
    [InlineData(-5)]
    public void Evaluate_OldOrFuture_Reverts(int seconds) =>
        Assert.Equal(RevertAction.RevertNow, LanguageRevert.Evaluate(Switched(T0), T0.AddSeconds(seconds)).Action);

    [Fact]
    public void Evaluate_MissingTimestamp_Reverts()
    {
        var s = Switched(T0);
        s.PendingLanguageRevertAt = null;
        Assert.Equal(RevertAction.RevertNow, LanguageRevert.Evaluate(s, T0).Action);
    }

    [Fact]
    public void Confirm_KeepsNewLanguageAndClearsPending()
    {
        var s = Switched(T0);
        LanguageRevert.Confirm(s);
        Assert.Equal("ja-JP", s.Language);
        Assert.Null(s.PendingLanguageRevert);
        Assert.Null(s.PendingLanguageRevertAt);
    }

    [Fact]
    public void Revert_RestoresPreviousAndClearsPending()
    {
        var s = Switched(T0);
        LanguageRevert.Revert(s);
        Assert.Equal("fr-FR", s.Language);
        Assert.Null(s.PendingLanguageRevert);
    }

    [Fact]
    public void OldSettingsFileLoadsWithNoPending()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<DeskPalSettings>("{\"Language\":\"de-DE\"}")!;
        Assert.Null(s.PendingLanguageRevert);
        Assert.Equal(RevertAction.None, LanguageRevert.Evaluate(s, T0).Action);
    }
}
