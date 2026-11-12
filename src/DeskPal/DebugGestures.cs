using System.Numerics;
using DeskPal.Core;
using DeskPal.Render;
using Microsoft.UI.Dispatching;

namespace DeskPal;

// Debug only (--debug-gesture=<region>:<gesture>[,...] or "all"): plays scripted pointer samples into a Bloop
// surface's own touch handler (never OS input), one gesture every 3 s, and with --debug-3d-dump asks the live view
// for labelled frames right after each one. Gestures: tap, tap2..tap9, longpress, pet, swipe-left/right/up/down, drag.
internal static class DebugGestures
{
    private const string All = "belly:tap,head:tap,face:tap,cheeks:tap,righthand:tap,feet:tap,belly:tap5,head:longpress,"
        + "head:pet,belly:pet,belly:longpress,belly:swipe-right,belly:swipe-up,belly:swipe-down,righthand:drag";

    public static void Run(BloopTouch touch, Func<IBloopReactor?> reactor, LiveViewport? live)
    {
        var spec = AppData.DebugGesture;
        if (string.IsNullOrWhiteSpace(spec)) return;
        if (spec.Equals("all", StringComparison.OrdinalIgnoreCase)) spec = All;
        var dq = DispatcherQueue.GetForCurrentThread();
        var at = 2.0;
        live?.RequestDumps("baseline", 1.0);
        foreach (var item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(':');
            if (parts.Length != 2 || ParseRegion(parts[0]) is not { } region) { Logger.Log($"debug gesture: bad item {item}"); continue; }
            var gesture = parts[1].ToLowerInvariant();
            var label = $"{parts[0]}-{gesture}".ToLowerInvariant();
            After(dq, at, () => Play(dq, touch, reactor, live, region, gesture, label));
            at += gesture.StartsWith("tap", StringComparison.Ordinal) && gesture.Length > 3 ? 4.5 : 3.0;
        }
    }

    private static BodyRegion? ParseRegion(string s) => s.ToLowerInvariant() switch
    {
        "head" or "headtop" => BodyRegion.HeadTop,
        "face" or "eyes" => BodyRegion.Face,
        "cheek" or "cheeks" => BodyRegion.Cheeks,
        "belly" => BodyRegion.Belly,
        "lefthand" or "leftarm" => BodyRegion.LeftHand,
        "righthand" or "rightarm" => BodyRegion.RightHand,
        "feet" or "bottom" => BodyRegion.Feet,
        _ => null,
    };

    private static void Play(DispatcherQueue dq, BloopTouch touch, Func<IBloopReactor?> reactor, LiveViewport? live,
        BodyRegion region, string gesture, string label)
    {
        if (reactor()?.PointFor(region) is not { } p)
        {
            Logger.Log($"debug gesture {label}: no Bloop on screen");
            return;
        }
        var hit = reactor()!.RegionAt(p);
        Logger.Log($"debug gesture {label} at {p.X:F0},{p.Y:F0} (maps to {hit})");
        void Dumps(params double[] t) => live?.RequestDumps(label, t);

        if (gesture.StartsWith("tap", StringComparison.Ordinal))
        {
            var count = gesture.Length > 3 && int.TryParse(gesture[3..], out var n) ? Math.Clamp(n, 1, 9) : 1;
            for (var i = 0; i < count; i++)
            {
                var t0 = i * 0.25;
                After(dq, t0, () => touch.SampleDown(p));
                After(dq, t0 + 0.08, () => touch.SampleUp(p));
                Dumps(t0 + 0.2);
            }
            if (count == 1) Dumps(0.35, 0.6);
            else Dumps(count * 0.25 + 0.5, count * 0.25 + 2.2);
            return;
        }
        switch (gesture)
        {
            case "longpress":
                After(dq, 0, () => touch.SampleDown(p));
                Dumps(0.7, 1.3);
                After(dq, 1.5, () => touch.SampleUp(p));
                Dumps(1.65, 1.9);
                break;
            case "pet":
                After(dq, 0, () => touch.SampleDown(p));
                for (var i = 1; i <= 24; i++)
                {
                    var q = p + new Vector2(i % 4 is 1 or 2 ? 18 : -18, 0);
                    After(dq, i * 0.06, () => touch.SampleMove(q));
                }
                Dumps(0.8, 1.3);
                After(dq, 1.5, () => touch.SampleUp(p));
                break;
            case "drag":
            {
                After(dq, 0, () => touch.SampleDown(p));
                var dir = p.X >= (reactor()?.PointFor(BodyRegion.Belly)?.X ?? p.X) ? 1 : -1;
                for (var i = 1; i <= 10; i++)
                {
                    var q = p + new Vector2(dir * 4 * i, -7 * i);
                    After(dq, i * 0.06, () => touch.SampleMove(q));
                }
                Dumps(0.9);
                After(dq, 1.1, () => touch.SampleUp(p + new Vector2(dir * 40, -70)));
                Dumps(1.3, 1.7);
                break;
            }
            case "swipe-left":
            case "swipe-right":
            case "swipe-up":
            case "swipe-down":
            {
                var d = gesture[6..] switch { "left" => new Vector2(-1, 0), "right" => new Vector2(1, 0), "up" => new Vector2(0, -1), _ => new Vector2(0, 1) };
                var start = p - d * 45;
                After(dq, 0, () => touch.SampleDown(start));
                for (var i = 1; i <= 6; i++)
                {
                    var q = start + d * 15 * i;
                    After(dq, i * 0.016, () => touch.SampleMove(q));
                }
                After(dq, 0.11, () => touch.SampleUp(start + d * 90));
                Dumps(0.25, 0.45, 0.8);
                break;
            }
            default:
                Logger.Log($"debug gesture: unknown gesture {gesture}");
                break;
        }
    }

    private static void After(DispatcherQueue dq, double seconds, Action action)
    {
        var t = dq.CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromSeconds(Math.Max(0.001, seconds));
        t.Tick += (_, _) => TickGuard.Run("debug-gesture", action);
        t.Start();
    }
}
