using System.Numerics;

namespace DeskPal.Core;

// Pip's skeleton: a small jointed figure with arms and legs, in the same space as Bloop (metres, Y up, facing +Z,
// feet on y = 0, his left on +X). Every joint rests with no rotation, so a joint's rest frame lines up with the model
// and bind space is just the rest position. Node names Bloop's springs and gestures use (root, body, jelly.*,
// arm.*.1, hand.*, socket.head) mean the same thing here, so taps, pats, drags and the webcam mirror work on Pip too.
public readonly record struct PipJoint(string Name, int Parent, Vector3 Offset);

public static class PipRig
{
    public const float Scale = 0.9f;
    public const float Thigh = 0.0125f * Scale, Shin = 0.0125f * Scale, UpperArm = 0.015f * Scale, Forearm = 0.0145f * Scale;
    public const float HipsHeight = 0.0305f * Scale, HipSide = 0.0085f * Scale, AnkleHeight = 0.0045f * Scale;
    // The foot's front contact point below and ahead of the ankle; it stays put when the heel lifts.
    public static readonly Vector3 ToeContact = new(0, -0.0045f * Scale, 0.010f * Scale);

    public const int Root = 0, Hips = 1, Body = 2, Chest = 3, Neck = 4, Head = 5, Socket = 6,
        ShoulderL = 7, ArmL = 8, ElbowL = 9, HandL = 10, ShoulderR = 11, ArmR = 12, ElbowR = 13, HandR = 14,
        ThighL = 15, KneeL = 16, FootL = 17, ThighR = 18, KneeR = 19, FootR = 20, Front = 21, Bottom = 22;

    public static readonly PipJoint[] Joints =
    [
        J("root", -1, 0, 0, 0),
        J("hips", Root, 0, 0.0305f, 0),
        J("body", Hips, 0, 0.004f, 0),
        J("chest", Body, 0, 0.013f, 0),
        J("neck", Chest, 0, 0.010f, 0),
        J("jelly.top", Neck, 0, 0.020f, 0),
        J("socket.head", Head, 0, 0.010f, 0),
        J("jelly.L", Chest, 0.0125f, 0.0055f, 0),
        J("arm.L.1", ShoulderL, 0.0015f, 0, 0),
        J("arm.L.2", ArmL, 0, -0.015f, 0),
        J("hand.L", ElbowL, 0, -0.0145f, 0),
        J("jelly.R", Chest, -0.0125f, 0.0055f, 0),
        J("arm.R.1", ShoulderR, -0.0015f, 0, 0),
        J("arm.R.2", ArmR, 0, -0.015f, 0),
        J("hand.R", ElbowR, 0, -0.0145f, 0),
        J("leg.L.1", Hips, 0.0085f, -0.0015f, 0),
        J("leg.L.2", ThighL, 0, -0.0125f, 0),
        J("foot.L", KneeL, 0, -0.0125f, 0),
        J("leg.R.1", Hips, -0.0085f, -0.0015f, 0),
        J("leg.R.2", ThighR, 0, -0.0125f, 0),
        J("foot.R", KneeR, 0, -0.0125f, 0),
        J("jelly.front", Body, 0, 0.007f, 0.0135f),
        J("jelly.bottom", Hips, 0, -0.004f, 0),
    ];

    // Face shapes, in the order the head's morph targets are built. The names match Bloop's so his face reactions
    // (blink, happy eyes, smiles) drive Pip's face as well.
    public static readonly string[] MorphNames = ["blink", "eyes_happy", "eyes_squeeze", "mouth_smile", "mouth_o", "mouth_grin"];

    public static int Count => Joints.Length;

    public static int Index(string name) => Array.FindIndex(Joints, j => j.Name == name);

    private static PipJoint J(string name, int parent, float x, float y, float z) => new(name, parent, new Vector3(x, y, z) * Scale);

    // Rest position of every joint in model space.
    public static Vector3[] RestPositions()
    {
        var p = new Vector3[Count];
        for (var i = 0; i < Count; i++) p[i] = Joints[i].Offset + (Joints[i].Parent >= 0 ? p[Joints[i].Parent] : Vector3.Zero);
        return p;
    }

    // Forward kinematics for a frame: model-space positions and rotations of every joint.
    public static (Vector3[] Positions, Quaternion[] Rotations) World(PipFrame f)
    {
        var pos = new Vector3[Count];
        var rot = new Quaternion[Count];
        for (var i = 0; i < Count; i++)
        {
            var parent = Joints[i].Parent;
            if (parent < 0)
            {
                pos[i] = f.T[i];
                rot[i] = f.R[i];
                continue;
            }
            pos[i] = pos[parent] + Vector3.Transform(f.T[i], rot[parent]);
            rot[i] = Quaternion.Normalize(rot[parent] * f.R[i]);
        }
        return (pos, rot);
    }
}

// One sampled pose: local rotation and translation per joint, plus the face shape weights.
public sealed class PipFrame
{
    public readonly Quaternion[] R = new Quaternion[PipRig.Count];
    public readonly Vector3[] T = new Vector3[PipRig.Count];
    public readonly float[] Morph = new float[PipRig.MorphNames.Length];

    public void Rest()
    {
        for (var i = 0; i < R.Length; i++)
        {
            R[i] = Quaternion.Identity;
            T[i] = PipRig.Joints[i].Offset;
        }
        Array.Clear(Morph);
    }
}
