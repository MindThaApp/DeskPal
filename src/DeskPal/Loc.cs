using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace DeskPal;

// Text for code-behind from Strings\<language>\Resources.resw (MRT Core, so it works unpackaged and packaged).
// x:Uid in XAML uses the same files.
internal static class Loc
{
    private static ResourceLoader? _loader;

    // Call once at startup, before any window exists. Empty tag = follow the Windows display language.
    public static void Apply(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            // Back to following Windows: drop any override left from an earlier language choice.
            try { Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = ""; }
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

    private static ResourceManager? _manager;

    // The text in a specific language, whatever language the app runs in (empty tag = the Windows display language).
    // Used by the keep-this-language prompt to show the previous language next to the new one.
    public static string GetFor(string key, string? tag)
    {
        try
        {
            var language = string.IsNullOrWhiteSpace(tag) ? ResolveWindowsLanguage() : tag;
            var manager = _manager ??= new ResourceManager();
            var context = manager.CreateResourceContext();
            context.QualifierValues["Language"] = language;
            var value = manager.MainResourceMap.TryGetValue("Resources/" + key, context)?.ValueAsString;
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception) { return key; }
    }

    public static string FormatFor(string key, string? tag, params object[] args) =>
        string.Format(CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(tag) ? ResolveWindowsLanguage() : tag), GetFor(key, tag), args);

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
