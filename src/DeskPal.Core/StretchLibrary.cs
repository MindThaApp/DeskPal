namespace DeskPal.Core;

public sealed class StretchLibrary
{
    public static readonly IReadOnlyList<StretchInfo> All =
    [
        new("overhead-reach", "Overhead reach", "Upper back", 30, "Interlace your fingers and reach up high, breathing slowly. Lower your arms and let your shoulders settle.", "bloop-stretch-overhead-reach"),
        new("side-bend", "Side bend", "Torso", 30, "Reach one arm overhead and lean gently to the opposite side. Switch sides halfway through.", "bloop-stretch-side-bend"),
        new("shoulder-shrug", "Shoulder shrug", "Shoulders", 20, "Lift both shoulders toward your ears, hold a moment, then let them drop. Repeat a few times.", "bloop-stretch-shoulder-shrug"),
        new("neck-roll", "Neck roll", "Neck", 30, "Slowly tilt your head toward one shoulder and roll it gently across to the other. Keep it easy and relaxed.", "bloop-idle"),
        new("seated-twist", "Seated twist", "Spine", 40, "Sit tall and turn your upper body to one side, holding the chair back. Switch sides after a few breaths.", "bloop-idle"),
        new("wrist-stretch", "Wrist stretch", "Wrists", 30, "Extend one arm with the palm up and gently draw the fingers back with the other hand. Switch hands.", "bloop-idle"),
        new("hip-flexor", "Hip flexor stretch", "Hips", 60, "Stand and step one foot back, shifting your weight forward until you feel a light stretch at the front of the hip. Switch legs.", "bloop-idle"),
        new("calf-raises", "Calf raises", "Legs", 30, "Stand tall and rise onto your toes, then lower slowly. Hold a chair if you like. Repeat several times.", "bloop-idle"),
    ];

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
