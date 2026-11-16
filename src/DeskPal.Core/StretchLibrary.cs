namespace DeskPal.Core;

public sealed class StretchLibrary
{
    private static IReadOnlyList<StretchInfo>? _all;
    private static string? _allCulture;

    // Rebuilt when the UI culture changes so names and instructions follow the display language.
    public static IReadOnlyList<StretchInfo> All
    {
        get
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
            if (_all is null || _allCulture != culture)
            {
                _all = Build();
                _allCulture = culture;
            }
            return _all;
        }
    }

    private static StretchInfo[] Build() =>
    [
        Make("overhead-reach", "OverheadReach", 30, "bloop-stretch-overhead-reach"),
        Make("side-bend", "SideBend", 30, "bloop-stretch-side-bend"),
        Make("shoulder-shrug", "ShoulderShrug", 20, "bloop-stretch-shoulder-shrug"),
        Make("neck-roll", "NeckRoll", 30, "bloop-idle"),
        Make("seated-twist", "SeatedTwist", 40, "bloop-idle"),
        Make("wrist-stretch", "WristStretch", 30, "bloop-idle"),
        Make("hip-flexor", "HipFlexor", 60, "bloop-idle"),
        Make("calf-raises", "CalfRaises", 30, "bloop-idle"),
    ];

    private static StretchInfo Make(string id, string key, int seconds, string asset) =>
        new(id, Str.Get($"Stretch_{key}_Name"), Str.Get($"Stretch_{key}_Area"), seconds, Str.Get($"Stretch_{key}_Instruction"), asset);
    private string? _lastId;

    public StretchLibrary(string? lastId = null) => _lastId = lastId;

    public string? LastId => _lastId;

    public StretchInfo Next()
    {
        var lastIndex = _lastId is null ? -1 : IndexOf(_lastId);
        var lastArea = lastIndex >= 0 ? All[lastIndex].BodyArea : null;
        for (var step = 1; step <= All.Count; step++)
        {
            var candidate = All[(lastIndex + step + All.Count) % All.Count];
            if (candidate.BodyArea != lastArea)
            {
                _lastId = candidate.Id;
                return candidate;
            }
        }
        _lastId = All[0].Id;
        return All[0];
    }

    private static int IndexOf(string id)
    {
        for (var i = 0; i < All.Count; i++)
            if (All[i].Id == id) return i;
        return -1;
    }
}
