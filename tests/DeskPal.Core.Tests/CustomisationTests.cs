using System.Text.Json;
using System.Text.Json.Serialization;
using DeskPal.Core;

namespace DeskPal.Core.Tests;

public class CustomisationTests
{
    private static readonly ProState Free = ProState.Free;
    private static readonly ProState Trial = new(ProTier.Trial, 5, true);
    private static readonly ProState Owned = new(ProTier.Owned, 0, true);
    private static readonly ProState Lapsed = new(ProTier.Free, 0, true);

    private static LookSettings Saved()
    {
        var s = new LookSettings();
        s.Set(BloopColour.Mint);
        s.Set(BloopHat.PartyHat);
        s.Set(PotStyle.SkyGlaze);
        return s;
    }

    [Fact]
    public void NewSettingsAreTheDefaultLook()
    {
        Assert.Equal(CharacterLook.Default, new DeskPalSettings().Look.ToLook());
        Assert.True(CharacterLook.Default.IsDefault);
    }

    [Fact]
    public void LockedShowsTheDefaultLook()
    {
        Assert.Equal(CharacterLook.Default, CharacterLook.Effective(Saved(), Free));
    }

    [Fact]
    public void UnlockedShowsTheSavedLook()
    {
        var want = new CharacterLook(BloopColour.Mint, BloopHat.PartyHat, PotStyle.SkyGlaze);
        Assert.Equal(want, CharacterLook.Effective(Saved(), Trial));
        Assert.Equal(want, CharacterLook.Effective(Saved(), Owned));
    }

    [Fact]
    public void SavedLookSurvivesALapseAndComesBack()
    {
        var settings = new DeskPalSettings { Look = Saved() };
        Assert.Equal(CharacterLook.Default, CharacterLook.Effective(settings.Look, Lapsed));
        Assert.Equal("Mint", settings.Look.BloopColour);
        Assert.Equal("PartyHat", settings.Look.Hat);
        Assert.Equal("SkyGlaze", settings.Look.Pot);
        Assert.Equal(BloopHat.PartyHat, CharacterLook.Effective(settings.Look, Owned).Hat);
    }

    [Fact]
    public void LookRoundTripsThroughJson()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var json = JsonSerializer.Serialize(new DeskPalSettings { Look = Saved() }, options);
        var back = JsonSerializer.Deserialize<DeskPalSettings>(json, options)!;
        Assert.Equal(Saved().ToLook(), back.Look.ToLook());
    }

    [Fact]
    public void UnknownSavedValuesShowDefaultsButAreKept()
    {
        var json = """{"Look":{"BloopColour":"Galaxy","Hat":"Crown","Pot":"Gold"}}""";
        var s = JsonSerializer.Deserialize<DeskPalSettings>(json)!;
        Assert.Equal(CharacterLook.Default, s.Look.ToLook());
        Assert.Equal("Galaxy", s.Look.BloopColour);
        Assert.Equal("Crown", s.Look.Hat);
    }

    [Fact]
    public void DefaultColourAndPotLeaveColoursAlone()
    {
        Assert.Null(ColourShift.ForBloop(BloopColour.Blue));
        Assert.Null(ColourShift.ForPot(PotStyle.Terracotta));
        const string svg = "<svg><ellipse cx=\"0\" cy=\"0\" rx=\"42\" ry=\"36\" fill=\"#7FB3E8\"/></svg>";
        Assert.Equal(svg, CharacterSvg.Apply(svg, "bloop-idle", CharacterLook.Default));
    }

    [Fact]
    public void BloopShiftRecoloursBodyButKeepsBlushEyesAndMouth()
    {
        var shift = ColourShift.ForBloop(BloopColour.Mint)!;
        var (r, g, b) = shift.Apply(0x7F, 0xB3, 0xE8);
        var (h, _, _) = ColourShift.ToHsl(r, g, b);
        Assert.InRange(h, 150, 166);
        Assert.Equal(((byte)0xF4, (byte)0xA6, (byte)0xB8), shift.Apply(0xF4, 0xA6, 0xB8)); // blush
        Assert.Equal(((byte)0x40, (byte)0x58, (byte)0x7C), shift.Apply(0x40, 0x58, 0x7C)); // eye
        Assert.Equal(((byte)0x22, (byte)0x32, (byte)0x4A), shift.Apply(0x22, 0x32, 0x4A)); // mouth
        Assert.Equal(((byte)0xFF, (byte)0xFF, (byte)0xFF), shift.Apply(0xFF, 0xFF, 0xFF));
    }

    [Fact]
    public void PotShiftKeepsSproutsFaceAndFlower()
    {
        var shift = ColourShift.ForPot(PotStyle.SkyGlaze)!;
        Assert.NotEqual(((byte)0xC9, (byte)0x7B, (byte)0x4A), shift.Apply(0xC9, 0x7B, 0x4A)); // pot
        Assert.Equal(((byte)0x5E, (byte)0x46, (byte)0x36), shift.Apply(0x5E, 0x46, 0x36)); // eye
        Assert.Equal(((byte)0x3A, (byte)0x2A, (byte)0x20), shift.Apply(0x3A, 0x2A, 0x20)); // mouth
        Assert.Equal(((byte)0xD2, (byte)0x9C, (byte)0x2A), shift.Apply(0xD2, 0x9C, 0x2A)); // flower centre
        Assert.Equal(((byte)0x6B, (byte)0xBF, (byte)0x59), shift.Apply(0x6B, 0xBF, 0x59)); // leaf
    }

    [Fact]
    public void SvgGetsHatInsideTheBodyGroup()
    {
        const string svg = "<svg><g transform=\"rotate(-14)\"><ellipse cx=\"0\" cy=\"0\" rx=\"42\" ry=\"38\" fill=\"url(#bsb3d-body)\"/><circle r=\"1\"/></g><path/></svg>";
        var look = new CharacterLook(BloopColour.Lilac, BloopHat.Beanie, PotStyle.Terracotta);
        var result = CharacterSvg.Apply(svg, "bloop-stretch-side-bend", look);
        var hat = result.IndexOf("translate(0 -38)", StringComparison.Ordinal);
        Assert.True(hat > result.IndexOf("<circle r=\"1\"/>", StringComparison.Ordinal));
        Assert.True(hat < result.IndexOf("</g><path/>", StringComparison.Ordinal));
    }

    [Fact]
    public void FlatSvgGetsHatAfterRecolour()
    {
        const string svg = "<svg><ellipse cx=\"0\" cy=\"0\" rx=\"42\" ry=\"36\" fill=\"#7FB3E8\"/></svg>";
        var result = CharacterSvg.Apply(svg, "bloop-idle", new CharacterLook(BloopColour.Peach, BloopHat.LeafSprig, PotStyle.Cream));
        Assert.DoesNotContain("#7FB3E8", result);
        Assert.Contains("translate(0 -36)", result);
        Assert.EndsWith("</g></svg>", result);
    }

    [Fact]
    public void SproutOnlyChangesItsPot()
    {
        const string svg = "<svg><path fill=\"#C97B4A\"/><path fill=\"#6BBF59\"/></svg>";
        var result = CharacterSvg.Apply(svg, "sprout-mood-happy", new CharacterLook(BloopColour.Sunny, BloopHat.PartyHat, PotStyle.Cream));
        Assert.DoesNotContain("#C97B4A", result);
        Assert.Contains("#6BBF59", result);
        Assert.DoesNotContain("<g", result);
    }
}
