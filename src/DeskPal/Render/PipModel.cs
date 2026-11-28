using System.Numerics;
using DeskPal.Core;

namespace DeskPal.Render;

// Builds Pip for the live view: soft, low-poly shapes (an egg body, a round head, capsule arms and legs, mitten
// hands, chunky shoes) skinned to PipRig's joints, a face with the same shape keys Bloop's reactions use, and every
// PipMoves move baked into a clip at 30 frames a second. Nothing is loaded from disk, so there is no asset to ship.
internal static class PipModel
{
    private const float S = PipRig.Scale;
    private const float Fps = 30;
    private const int Morphs = 6;
    private const int Blink = 0, EyesHappy = 1, EyesSqueeze = 2, Smile = 3, MouthO = 4, Grin = 5;

    private static readonly Vector4 Mint = Srgb(0x8B, 0xD8, 0xBD);
    private static readonly Vector4 Cream = Srgb(0xFF, 0xF1, 0xD6);
    private static readonly Vector4 Coral = Srgb(0xF2, 0x78, 0x6B);
    private static readonly Vector4 Ink = Srgb(0x2D, 0x2A, 0x3E);
    private static readonly Vector4 Blush = Srgb(0xF5, 0x9A, 0xA6);

    public static GltfModel Build()
    {
        var rest = PipRig.RestPositions();
        var meshNode = PipRig.Count;

        var body = new Builder();
        var head = rest[PipRig.Head] + new Vector3(0, -0.006f * S, 0);
        body.Ellipsoid(head, new Vector3(0.016f * S), 16, 24, _ => (PipRig.Head, PipRig.Head, 0));
        var chestBlend = Torso(rest);
        body.Ellipsoid(rest[PipRig.Body] + new Vector3(0, 0.0085f * S, 0), new Vector3(0.0150f, 0.0145f, 0.0128f) * S, 14, 22, chestBlend);
        foreach (var (upper, elbow, hand) in new[] { (PipRig.ArmL, PipRig.ElbowL, PipRig.HandL), (PipRig.ArmR, PipRig.ElbowR, PipRig.HandR) })
        {
            body.Capsule(rest[upper], rest[elbow], 0.0040f * S, upper);
            body.Capsule(rest[elbow], rest[hand], 0.0036f * S, elbow);
            body.Ellipsoid(rest[hand] + new Vector3(0, -0.0035f * S, 0), new Vector3(0.0045f, 0.0055f, 0.0038f) * S, 8, 12, _ => (hand, hand, 0));
        }
        foreach (var (thigh, knee, foot) in new[] { (PipRig.ThighL, PipRig.KneeL, PipRig.FootL), (PipRig.ThighR, PipRig.KneeR, PipRig.FootR) })
        {
            body.Capsule(rest[thigh], rest[knee], 0.0048f * S, thigh);
            body.Capsule(rest[knee], rest[foot], 0.0044f * S, knee);
        }

        var belly = new Builder();
        belly.Ellipsoid(rest[PipRig.Body] + new Vector3(0, 0.007f, 0.0118f) * S, new Vector3(0.0095f, 0.0092f, 0.0026f) * S, 10, 16, chestBlend);

        var shoes = new Builder();
        foreach (var foot in new[] { PipRig.FootL, PipRig.FootR })
            shoes.Ellipsoid(rest[foot] + new Vector3(0, -0.001f, 0.0025f) * S, new Vector3(0.0058f, 0.0035f, 0.0085f) * S, 8, 14, _ => (foot, foot, 0));

        // The face sits on the head sphere; each feature remembers its centre and size for the shape keys.
        var face = new Builder();
        var glints = new Builder();
        var cheeks = new Builder();
        Vector3 OnHead(Vector3 dir, float inset) => head + Vector3.Normalize(dir) * (0.016f * S - inset);
        var headOnly = (Func<Vector3, (int, int, float)>)(_ => (PipRig.Head, PipRig.Head, 0));
        foreach (var side in new[] { 1f, -1f })
        {
            var eye = OnHead(new Vector3(0.36f * side, 0.10f, 0.93f), 0.0002f * S);
            face.Ellipsoid(eye, new Vector3(0.0029f, 0.0039f, 0.0016f) * S, 8, 12, headOnly, Feature.Eye);
            glints.Ellipsoid(eye + new Vector3(0.0008f, 0.0012f, 0.0014f) * S, new Vector3(0.0008f * S), 5, 8, headOnly, Feature.Glint);
            cheeks.Ellipsoid(OnHead(new Vector3(0.60f * side, -0.15f, 0.785f), 0.0004f * S), new Vector3(0.0030f, 0.0019f, 0.0009f) * S, 6, 10, headOnly);
        }
        face.Ellipsoid(OnHead(new Vector3(0, -0.33f, 0.944f), 0.0003f * S), new Vector3(0.0030f, 0.0011f, 0.0010f) * S, 6, 14, headOnly, Feature.Mouth);

        var parts = new List<ModelPart>
        {
            body.Part("pip.body", meshNode, Mint, 0.62f),
            belly.Part("pip.belly", meshNode, Cream, 0.7f),
            shoes.Part("pip.shoes", meshNode, Coral, 0.5f),
            cheeks.Part("pip.cheeks", meshNode, Blush, 0.9f),
            face.Part("pip.face", meshNode, Ink, 0.35f, withMorphs: true),
            glints.Part("pip.glints", meshNode, Vector4.One, 0.2f, withMorphs: true, PartKind.Glint),
        };

        var nodes = new ModelNode[PipRig.Count + 1];
        for (var i = 0; i < PipRig.Count; i++)
        {
            var j = PipRig.Joints[i];
            // Bloop's hats are sized for his wide head; the socket's scale fits them to Pip's round one.
            var scale = i == PipRig.Socket ? new Vector3(0.36f, 0.44f, 0.42f) : Vector3.One;
            nodes[i] = new ModelNode(j.Name, j.Parent, scale, Quaternion.Identity, j.Offset, [], []);
        }
        nodes[meshNode] = new ModelNode("Pip", -1, Vector3.One, Quaternion.Identity, Vector3.Zero, new float[Morphs], PipRig.MorphNames);

        var depth = new int[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
            for (var p = nodes[i].Parent; p >= 0; p = nodes[p].Parent) depth[i]++;

        return new GltfModel
        {
            Parts = parts.ToArray(),
            Nodes = nodes,
            Order = Enumerable.Range(0, nodes.Length).OrderBy(i => depth[i]).ToArray(),
            JointNodes = Enumerable.Range(0, PipRig.Count).ToArray(),
            InverseBind = rest.Select(p => Matrix4x4.CreateTranslation(-p)).ToArray(),
            Clips = PipMoves.All.Select(m => Bake(m, meshNode)).ToDictionary(c => c.Name),
        };
    }

    // The egg body bends in two: its lower half follows the body joint, its upper half the chest.
    private static Func<Vector3, (int, int, float)> Torso(Vector3[] rest)
    {
        var baseY = rest[PipRig.Body].Y;
        return p => (PipRig.Body, PipRig.Chest, PipMoves.Smooth((p.Y - baseY - 0.006f * S) / (0.008f * S)));
    }

    private static AnimationClip Bake(PipMoves.Move move, int meshNode)
    {
        var frames = (int)MathF.Ceiling(move.Duration * Fps) + 1;
        var times = new float[frames];
        var rot = new Quaternion[PipRig.Count][];
        var pos = new Vector3[PipRig.Count][];
        var weights = new float[frames * Morphs];
        for (var j = 0; j < PipRig.Count; j++) { rot[j] = new Quaternion[frames]; pos[j] = new Vector3[frames]; }
        var f = new PipFrame();
        for (var k = 0; k < frames; k++)
        {
            var t = Math.Min(k / Fps, move.Duration);
            times[k] = t;
            PipMoves.Sample(move.Name, t, f);
            for (var j = 0; j < PipRig.Count; j++)
            {
                var q = f.R[j];
                // Keep neighbouring keys in the same hemisphere so a full spin interpolates the short way each step.
                if (k > 0 && Quaternion.Dot(q, rot[j][k - 1]) < 0) q = -q;
                rot[j][k] = q;
                pos[j][k] = f.T[j];
            }
            Array.Copy(f.Morph, 0, weights, k * Morphs, Morphs);
        }

        var channels = new List<ClipChannel>();
        if (move.Name != "blink")
        {
            for (var j = 0; j < PipRig.Count; j++)
            {
                if (j is PipRig.Socket or PipRig.Front or PipRig.Bottom or PipRig.Head) continue;
                channels.Add(new ClipChannel { Node = j, Path = ChannelPath.Rotation, Step = false, Times = times, Rotations = rot[j] });
            }
            foreach (var j in new[] { PipRig.Root, PipRig.Hips, PipRig.ShoulderL, PipRig.ShoulderR })
                channels.Add(new ClipChannel { Node = j, Path = ChannelPath.Translation, Step = false, Times = times, Vectors = pos[j] });
        }
        channels.Add(new ClipChannel { Node = meshNode, Path = ChannelPath.Weights, Step = false, Times = times, Weights = weights, Stride = Morphs });
        return new AnimationClip { Name = move.Name, Duration = move.Duration, Channels = channels.ToArray() };
    }

    private enum Feature { None, Eye, Mouth, Glint }

    private sealed class Builder
    {
        private readonly List<MeshVertex> _v = [];
        private readonly List<uint> _i = [];
        private readonly List<(Feature Kind, Vector3 Centre, Vector3 Radii)> _tag = [];

        public void Ellipsoid(Vector3 c, Vector3 r, int rings, int segments, Func<Vector3, (int A, int B, float W)> bind, Feature feature = Feature.None)
        {
            var start = (uint)_v.Count;
            for (var y = 0; y <= rings; y++)
            {
                var th = MathF.PI * y / rings;
                for (var x = 0; x <= segments; x++)
                {
                    var ph = MathF.Tau * x / segments;
                    var unit = new Vector3(MathF.Sin(th) * MathF.Sin(ph), MathF.Cos(th), MathF.Sin(th) * MathF.Cos(ph));
                    var p = c + unit * r;
                    if (feature == Feature.Mouth) p.Y += 0.0009f * S * Sq((p.X - c.X) / r.X);
                    Add(p, Vector3.Normalize(unit / r), bind(p), (feature, c, r));
                }
            }
            Grid(start, rings, segments);
        }

        // A capsule from a to b (a is the joint it moves with), rounded at both ends.
        public void Capsule(Vector3 a, Vector3 b, float radius, int joint)
        {
            var axis = b - a;
            var length = axis.Length();
            var dir = axis / length;
            var side = Vector3.Normalize(Vector3.Cross(dir, MathF.Abs(dir.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
            var up = Vector3.Cross(side, dir);
            const int cap = 5, segments = 12;
            var start = (uint)_v.Count;
            var rows = 0;
            for (var half = 0; half < 2; half++)
            {
                for (var k = 0; k <= cap; k++)
                {
                    // Polar angle from the a end (0) to the b end (pi), split in two hemispheres.
                    var th = half == 0 ? MathF.PI / 2 * k / cap : MathF.PI / 2 + MathF.PI / 2 * k / cap;
                    var centre = half == 0 ? a : b;
                    for (var x = 0; x <= segments; x++)
                    {
                        var ph = MathF.Tau * x / segments;
                        var n = -dir * MathF.Cos(th) + (side * MathF.Cos(ph) + up * MathF.Sin(ph)) * MathF.Sin(th);
                        Add(centre + n * radius, n, (joint, joint, 0), (Feature.None, centre, new Vector3(radius)));
                    }
                    rows++;
                }
            }
            Grid(start, rows - 1, segments);
        }

        private void Add(Vector3 p, Vector3 n, (int A, int B, float W) bind, (Feature, Vector3, Vector3) tag)
        {
            _v.Add(new MeshVertex
            {
                Position = p,
                Normal = n,
                Joints = new Vector4(bind.A, bind.B, 0, 0),
                Weights = new Vector4(1 - bind.W, bind.W, 0, 0),
            });
            _tag.Add(tag);
        }

        private void Grid(uint start, int rows, int segments)
        {
            var stride = (uint)segments + 1;
            for (var y = 0u; y < rows; y++)
                for (var x = 0u; x < segments; x++)
                {
                    var a = start + y * stride + x;
                    var b = a + stride;
                    _i.AddRange([a, b, a + 1, a + 1, b, b + 1]);
                }
        }

        public ModelPart Part(string name, int node, Vector4 colour, float roughness, bool withMorphs = false, PartKind kind = PartKind.Lit)
        {
            var verts = _v.ToArray();
            var count = withMorphs ? Morphs : 0;
            var deltas = new Vector4[count * verts.Length * 2];
            for (var t = 0; t < count; t++)
                for (var v = 0; v < verts.Length; v++)
                    deltas[(t * verts.Length + v) * 2] = new Vector4(Delta(t, verts[v].Position, _tag[v]), 0);
            return new ModelPart(name, node, true, kind, verts, _i.ToArray(), colour, roughness, false, count, deltas);
        }

        private static Vector3 Delta(int target, Vector3 p, (Feature Kind, Vector3 C, Vector3 R) tag)
        {
            var (kind, c, r) = tag;
            var dx = p.X - c.X;
            var nx = dx / r.X;
            var dy = p.Y - c.Y;
            switch (kind)
            {
                case Feature.Eye:
                    return target switch
                    {
                        Blink => new Vector3(0, c.Y - 0.0010f * S + dy * 0.12f - p.Y, 0),
                        EyesHappy => new Vector3(0, c.Y + dy * 0.3f + 0.0010f * S * (1 - Sq(nx)) - 0.0004f * S - p.Y, 0),
                        EyesSqueeze => new Vector3(-dx * 0.1f, c.Y + dy * 0.1f - p.Y, 0),
                        _ => Vector3.Zero,
                    };
                case Feature.Glint:
                    return target is Blink or EyesHappy or EyesSqueeze ? c - p : Vector3.Zero;
                case Feature.Mouth:
                {
                    var curve = 0.0009f * S * Sq(nx);
                    var raw = dy - curve;
                    return target switch
                    {
                        Smile => new Vector3(dx * 0.15f, 0.0010f * S * Sq(nx), 0),
                        MouthO => new Vector3(-dx * 0.45f, raw * 1.6f - curve, 0),
                        Grin => new Vector3(dx * 0.35f, raw * 0.9f + 0.0005f * S * Sq(nx), 0),
                        _ => Vector3.Zero,
                    };
                }
                default:
                    return Vector3.Zero;
            }
        }
    }

    private static float Sq(float x) => x * x;

    private static Vector4 Srgb(byte r, byte g, byte b)
    {
        static float L(byte c)
        {
            var v = c / 255f;
            return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        }
        return new Vector4(L(r), L(g), L(b), 1);
    }
}
