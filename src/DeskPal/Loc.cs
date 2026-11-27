using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace DeskPal;

// Text for code-behind from Strings\<language>\Resources.resw (MRT Core, so it works unpackaged and packaged).
// x:Uid in XAML uses the same files.
internal static class Loc
{
    private static ResourceLoader? _loader;

    // Call once at startup, before any window exists. Empty tag = follow the Windows display language.
    // True when the language the UI runs in is right to left (Arabic); windows mirror their root through Appearance.Register.
    public static bool IsRightToLeft { get; private set; }

    public static void Apply(string? tag)
    {
        var effective = string.IsNullOrWhiteSpace(tag) ? ResolveWindowsLanguage() : tag;
        IsRightToLeft = effective.StartsWith("ar", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(tag))
        {
            // Back to following Windows: drop any override left from an earlier language choice. Nothing to do (and the
            // setter throws unpackaged) when no override is set.
            try
            {
                if (!string.IsNullOrEmpty(Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride))
                    Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "";
            }
            catch (Exception ex) { Logger.Log("language override reset failed: " + ex.Message); }
            return;
        }
        try
        {
            var culture = CultureInfo.GetCultureInfo(tag);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException) { return; }
        try { Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = tag; }
        catch (Exception ex) { Logger.Log("language override failed: " + ex.Message); }
        _loader = null;
    }

    public static string Get(string key)
    {
        try
        {
            _loader ??= new ResourceLoader();
            var value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception) { return key; }
    }

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static IReadOnlyList<string> WindowsLanguages()
    {
        try { return Windows.System.UserProfile.GlobalizationPreferences.Languages.ToList(); }
        catch (Exception) { return [CultureInfo.InstalledUICulture.Name]; }
    }

    public static string ResolveWindowsLanguage() =>
        DeskPal.Core.LanguageList.ResolveStartupLanguage(WindowsLanguages(), DeskPal.Core.SupportedLanguages.All);

    public static string WindowsRegion()
    {
        try { return new Windows.Globalization.GeographicRegion().CodeTwoLetter; }
        catch (Exception) { return ""; }
    }}
