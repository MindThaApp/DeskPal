using System.Numerics;
using DeskPal.Core;
using Xunit;

namespace DeskPal.Core.Tests;

public class PipMovesTests
{
    private const float S = PipRig.Scale;

    private static (Vector3[] P, Quaternion[] R) At(string move, float t)
    {
        var f = new PipFrame();
        PipMoves.Sample(move, t, f);
        return PipRig.World(f);
    }

    private static IEnumerable<float> Times(PipMoves.Move m)
    {
        for (var t = 0f; t <= m.Duration + 1e-4f; t += 1 / 30f) yield return Math.Min(t, m.Duration);
    }

    [Fact]
    public void HasEveryMoveTheNudgesAndReactionsAskFor()
    {
        var names = PipMoves.All.Select(m => m.Name).ToHashSet();
        foreach (var n in new[] { "idle_breathe", "blink", "wave", "stretch_overhead", "side_bend", "shoulder_shrug", "walk", "star_jump", "high_five", "celebrate", "poke_squish" })
            Assert.Contains(n, names);
        // Every stretch in the library has its own move (ids with "-" become "_"), except the overhead reach,
        // which is the shared stretch_overhead move.
        foreach (var s in StretchLibrary.All.Where(s => s.Id != "overhead-reach"))
            Assert.Contains(s.Id.Replace('-', '_'), names);
    }

    [Fact]
    public void RestPoseStandsOnTheFloorWithFeetUnderTheHips()
    {
        var (p, _) = At("idle_breathe", 0);
        Assert.InRange(p[PipRig.FootL].Y, PipRig.AnkleHeight - 0.0003f, PipRig.AnkleHeight + 0.0003f);
        Assert.InRange(p[PipRig.FootL].X, PipRig.HipSide - 0.0004f, PipRig.HipSide + 0.0004f);
        Assert.InRange(p[PipRig.FootR].X, -PipRig.HipSide - 0.0004f, -PipRig.HipSide + 0.0004f);
        // Arms hang slightly out, hands below the shoulders.
        Assert.True(p[PipRig.HandL].X > p[PipRig.ShoulderL].X);
        Assert.True(p[PipRig.HandL].Y < p[PipRig.ShoulderL].Y - 0.015f * S);
    }

    [Fact]
    public void FeetNeverSinkIntoTheFloorAndEverythingStaysInFrame()
    {
        var toe = PipRig.ToeContact;
        var heel = new Vector3(0, toe.Y, -0.004f * S);
        foreach (var m in PipMoves.All)
            foreach (var t in Times(m))
            {
                var (p, r) = At(m.Name, t);
                foreach (var foot in new[] { PipRig.FootL, PipRig.FootR })
                {
                    Assert.True(p[foot].Y + Vector3.Transform(toe, r[foot]).Y > -0.0007f, $"{m.Name} {t:F2} toe below floor");
                    Assert.True(p[foot].Y + Vector3.Transform(heel, r[foot]).Y > -0.0007f, $"{m.Name} {t:F2} heel below floor");
                }
                foreach (var i in Enumerable.Range(0, PipRig.Count))
                {
                    Assert.True(float.IsFinite(p[i].X) && float.IsFinite(p[i].Y) && float.IsFinite(p[i].Z), $"{m.Name} {t:F2} joint {i}");
                    // The live view shows about 0.12 m above the floor and 0.09 m either side at the default framing.
                    Assert.InRange(p[i].Y, -0.001f, 0.118f);
                    Assert.InRange(p[i].X, -0.06f, 0.06f);
                }
            }
    }

    [Fact]
    public void PlantedFeetStayPlantedWhileTheHipsSinkInTheLunge()
    {
        var (p, _) = At("hip_flexor", 2.0f);
        var (rest, _) = At("hip_flexor", 0);
        var hipsDrop = rest[PipRig.Hips].Y - p[PipRig.Hips].Y;
        Assert.True(hipsDrop > 0.004f * S, $"hips dropped {hipsDrop}");
        // The front ankle sits at its target height (flat foot on the floor).
        Assert.InRange(p[PipRig.FootL].Y, PipRig.AnkleHeight - 0.0004f, PipRig.AnkleHeight + 0.0004f);
    }

    [Fact]
    public void MoonwalkSlidesTheFlatFootBackwards()
    {
        // In the first half of the cycle the left foot is flat and slides back (towards -Z in Pip's own frame).
        var yaw = Quaternion.Conjugate(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.95f));
        float LocalZ(float t) => Vector3.Transform(At("walk", t).P[PipRig.FootL], yaw).Z;
        Assert.True(LocalZ(0.05f) > LocalZ(0.35f) && LocalZ(0.35f) > LocalZ(0.65f), "left foot should slide back");
        var (_, r) = At("walk", 0.3f);
        var toeDown = Vector3.Transform(Vector3.UnitZ, Quaternion.Conjugate(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.95f)) * r[PipRig.FootL]);
        Assert.InRange(toeDown.Y, -0.05f, 0.05f);
    }

    [Theory]
    [InlineData("walk")]
    [InlineData("idle_breathe")]
    public void LoopingMovesJoinUp(string name)
    {
        var m = PipMoves.All.Single(x => x.Name == name);
        Assert.True(m.Loop);
        var (a, _) = At(name, 0);
        var (b, _) = At(name, m.Duration - 1e-4f);
        for (var i = 0; i < PipRig.Count; i++) Assert.True(Vector3.Distance(a[i], b[i]) < 0.0006f, $"{name} joint {i} jumps");
    }

    [Fact]
    public void OneShotMovesStartAndEndNearRest()
    {
        var (rest, _) = At("idle_breathe", 0);
        foreach (var m in PipMoves.All.Where(m => !m.Loop && m.Name != "blink"))
        {
            var (end, _) = At(m.Name, m.Duration);
            for (var i = 0; i < PipRig.Count; i++)
                Assert.True(Vector3.Distance(rest[i], end[i]) < 0.003f, $"{m.Name} ends away from rest at joint {PipRig.Joints[i].Name}");
        }
    }

    [Fact]
    public void OverheadReachPutsBothHandsAboveTheHead()
    {
        var (p, _) = At("stretch_overhead", 2.0f);
        Assert.True(p[PipRig.HandL].Y > p[PipRig.Head].Y && p[PipRig.HandR].Y > p[PipRig.Head].Y);
    }

    [Fact]
    public void CharacterChoiceIsSavedAndNotGated()
    {
        var old = new LookSettings { BloopColour = "Mint" };
        Assert.Equal(PalCharacter.Bloop, old.ToLook().Character);
        var unknown = new LookSettings { Character = "Robot" };
        Assert.Equal(PalCharacter.Bloop, unknown.ToLook().Character);

        var saved = new LookSettings();
        saved.Set(PalCharacter.Pip);
        saved.Set(BloopColour.Mint);
        var locked = CharacterLook.Effective(saved, ProState.Free);
        Assert.Equal(PalCharacter.Pip, locked.Character);
        Assert.Equal(BloopColour.Blue, locked.Colour);
        Assert.True(locked.IsDefault);
    }
}
