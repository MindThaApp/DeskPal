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
        if (string.IsNullOrWhiteSpace(tag)) return;
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
}
