using DeskPal.Core;
using Xunit;

namespace DeskPal.Core.Tests;

public class AppearanceSettingsTests
{
    [Fact]
    public void FileWithoutAppearanceLoadsWithDefaultsAndKeepsEverythingElse()
    {
        var path = Path.Combine(Path.GetTempPath(), "deskpal-appearance-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                { "SettingsVersion": 2, "StretchMinutes": 45, "EyesMinutes": 30, "Water": "Hourly",
                  "AvatarStyle": "3d", "Language": "de-DE", "AnimateTray": false, "HideNudgeSeconds": 60 }
                """);
            var s = JsonFileStore.Load<DeskPalSettings>(path, TimeProvider.System);
            s.Migrate();
            Assert.True(s.Appearance.IsDefault);
            Assert.Equal(ThemeChoice.System, s.Appearance.Theme);
            Assert.Equal(AccentChoice.Windows, s.Appearance.Accent);
            Assert.Equal(MaterialChoice.Mica, s.Appearance.Material);
            Assert.Equal(45, s.StretchMinutes);
            Assert.Equal(30, s.EyesMinutes);
            Assert.Equal(WaterMode.Hourly, s.Water);
            Assert.Equal("3d", s.AvatarStyle);
            Assert.Equal("de-DE", s.Language);
            Assert.False(s.AnimateTray);
            Assert.Equal(60, s.HideNudgeSeconds);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ResetTouchesOnlyAppearance()
    {
        var s = new DeskPalSettings { StretchMinutes = 45, AvatarStyle = "3d" };
        s.Appearance.Theme = ThemeChoice.Dark;
        s.Appearance.TextStep = 2;
        s.Appearance.Reset();
        Assert.True(s.Appearance.IsDefault);
        Assert.Equal(45, s.StretchMinutes);
        Assert.Equal("3d", s.AvatarStyle);
    }

    [Fact]
    public void OutOfRangeValuesAreRepaired()
    {
        var a = new AppearanceSettings { TextStep = 99, Theme = (ThemeChoice)42 };
        Assert.True(a.Clamp());
        Assert.Equal(AppearanceSettings.TextStepMax, a.TextStep);
        Assert.Equal(ThemeChoice.System, a.Theme);
    }
}
