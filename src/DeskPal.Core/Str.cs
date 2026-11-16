using System.Globalization;
using System.Resources;

namespace DeskPal.Core;

// Looks up text from Strings.resx; follows CultureInfo.CurrentUICulture and falls back to the neutral English.
public static class Str
{
    private static readonly ResourceManager Manager = new("DeskPal.Core.Strings", typeof(Str).Assembly);

    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
