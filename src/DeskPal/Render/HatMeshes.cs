using System.Numerics;
using DeskPal.Core;

namespace DeskPal.Render;

// Bloop's hats for the live model, built as small meshes when first worn. Each hat is drawn in the space of the
// "socket.head" node of bloop.glb: origin at the top of the head, Y up, Z towards the camera, metres. The node is
// under jelly.top, so hats follow every move and wobble with the head. Shapes match the hat pictures in
// CharacterSvg, and Bloop's body near the head is an ellipsoid of about 69 x 34 x 35 mm (half sizes) centred
// 34 mm below the socket, which the beanie follows so it sits snugly.
internal static class HatMeshes
{
    private const int Rings = 14, Segments = 28;
    private static readonly Vector3 HeadCentre = new(0, -0.0342f, 0);
    private static readonly Vector3 HeadRadii = new(0.0692f, 0.0342f, 0.0352f);

    public static GltfModel? Build(BloopHat hat)
    {
        var parts = new List<ModelPart>();
        switch (hat)
        {
            case BloopHat.Beanie: Beanie(parts); break;
            case BloopHat.PartyHat: PartyHat(parts); break;
            case BloopHat.LeafSprig: LeafSprig(parts); break;
            default: return null;
        }
        return new GltfModel
        {
            Parts = parts.ToArray(),
            Nodes = [new ModelNode("hat", -1, Vector3.One, Quaternion.Identity, Vector3.Zero, [], [])],
            Order = [0],
            JointNodes = [],
            InverseBind = [],
            Clips = [],
        };
    }

    private static void Beanie(List<ModelPart> parts)
    {
        const float cut = -0.014f, crown = 1.7f, t = 0.0014f, snugX = 0.59f, snugZ = 1.12f;
        var r = HeadRadii + new Vector3(t);
        var cosMax = (cut - HeadCentre.Y) / r.Y;
        var thetaMax = MathF.Acos(Math.Clamp(cosMax, -1, 1));
        Vector3 Shell(float u, float v)
        {
            var th = u * thetaMax;
            var ph = v * MathF.Tau;
            var p = HeadCentre + new Vector3(snugX * r.X * MathF.Sin(th) * MathF.Cos(ph), r.Y * MathF.Cos(th), snugZ * r.Z * MathF.Sin(th) * MathF.Sin(ph));
            p.Y = cut + crown * (p.Y - cut);
            return p;
        }
        var top = Shell(0, 0).Y;
        parts.Add(Surface("beanie", Shell, p => new Vector3(0, cut, 0) + (p with { Y = 0 }) * 0.2f, Srgb(0xEE, 0x7F, 0x86), 0.85f));

        var ringX = snugX * r.X * MathF.Sin(thetaMax);
        var ringZ = snugZ * r.Z * MathF.Sin(thetaMax);
        const float tubeY = 0.0042f, tubeOut = 0.0030f;
        Vector3 Path(float v) => new(ringX * MathF.Cos(v * MathF.Tau), cut + 0.0012f, ringZ * MathF.Sin(v * MathF.Tau));
        Vector3 Cuff(float u, float v)
        {
            var c = Path(v);
            var outward = Vector3.Normalize(c with { Y = 0 });
            var a = u * MathF.Tau;
            return c + outward * (tubeOut * MathF.Cos(a)) + Vector3.UnitY * (tubeY * MathF.Sin(a));
        }
        parts.Add(Surface("beanie-cuff", Cuff, p =>
        {
            var v = MathF.Atan2(p.Z / ringZ, p.X / ringX) / MathF.Tau;
            return Path(v);
        }, Srgb(0xD9, 0x5F, 0x68), 0.85f, wrapU: true));

        parts.Add(Sphere("beanie-pompom", new Vector3(0, top + 0.0055f, 0), new Vector3(0.0085f), Srgb(0xFF, 0xF4, 0xE6), 0.95f));
    }

    private static void PartyHat(List<ModelPart> parts)
    {
        const float height = 0.022f, rx = 0.0174f, rz = 0.0145f, sink = 0.004f;
        var tilt = Matrix4x4.CreateRotationZ(-10f * MathF.PI / 180, new Vector3(0, -sink, 0));
        float[] bands = [0, 0.22f, 0.34f, 0.56f, 0.68f, 1];
        for (var b = 0; b < bands.Length - 1; b++)
        {
            var from = bands[b];
            var to = bands[b + 1];
            var colour = b % 2 == 0 ? Srgb(0x8C, 0x7C, 0xF0) : Srgb(0xFF, 0xD4, 0x5C);
            Vector3 Band(float u, float v)
            {
                var h = from + (to - from) * u; // 0 at the base, 1 at the tip
                var s = 1 - h;
                var ph = v * MathF.Tau;
                return Vector3.Transform(new Vector3(rx * s * MathF.Cos(ph), -sink + height * h, rz * s * MathF.Sin(ph)), tilt);
            }
            parts.Add(Surface("party-band", Band, p => Vector3.Transform(new Vector3(0, -sink + height * 0.3f, 0), tilt), colour, 0.55f));
        }
        Vector3 Base(float u, float v)
        {
            var ph = v * MathF.Tau;
            return Vector3.Transform(new Vector3(rx * u * MathF.Cos(ph), -sink, rz * u * MathF.Sin(ph)), tilt);
        }
        parts.Add(Surface("party-base", Base, _ => Vector3.Transform(new Vector3(0, 0.02f, 0), tilt), Srgb(0x8C, 0x7C, 0xF0), 0.55f));
        parts.Add(Sphere("party-pompom", Vector3.Transform(new Vector3(0, -sink + height + 0.002f, 0), tilt), new Vector3(0.0050f),
            Srgb(0xFF, 0x8F, 0xA8), 0.9f));
    }

    private static void LeafSprig(List<ModelPart> parts)
    {
        const float stemTop = 0.030f, stemR = 0.0022f;
        Vector3 Stem(float u, float v)
        {
            var ph = v * MathF.Tau;
            var y = -0.004f + (stemTop + 0.004f) * u;
            var bend = -0.002f * u * u;
            return new Vector3(bend + stemR * MathF.Cos(ph), y, stemR * MathF.Sin(ph));
        }
        parts.Add(Surface("sprig-stem", Stem, p => new Vector3(-0.002f * ((p.Y + 0.004f) / (stemTop + 0.004f)) * ((p.Y + 0.004f) / (stemTop + 0.004f)), p.Y, 0),
            Srgb(0x4E, 0x9A, 0x51), 0.6f));
        parts.Add(Leaf("sprig-leaf-left", new Vector3(-0.014f, 0.022f, 0.001f), 28, Srgb(0x6B, 0xBF, 0x59)));
        parts.Add(Leaf("sprig-leaf-right", new Vector3(0.014f, 0.029f, -0.001f), -24, Srgb(0x8A, 0xD1, 0x7A)));
        parts.Add(Sphere("sprig-bud", new Vector3(-0.002f, stemTop + 0.001f, 0), new Vector3(0.0034f), Srgb(0x8A, 0xD1, 0x7A), 0.6f));
    }

    private static ModelPart Leaf(string name, Vector3 centre, float degrees, Vector4 colour)
    {
        var place = Matrix4x4.CreateRotationZ(degrees * MathF.PI / 180) * Matrix4x4.CreateRotationY(0.25f) * Matrix4x4.CreateTranslation(centre);
        Matrix4x4.Invert(place, out var back);
        var radii = new Vector3(0.0165f, 0.0026f, 0.0085f);
        // Pointed at both ends and gently cupped along its length.
        static float Cup(float x, float rx) => -0.0025f * (x / rx) * (x / rx);
        Vector3 Shape(float u, float v)
        {
            var th = u * MathF.PI;
            var ph = v * MathF.Tau;
            var x = radii.X * MathF.Cos(th);
            var w = MathF.Pow(MathF.Sin(th), 1.4f);
            return Vector3.Transform(new Vector3(x, Cup(x, radii.X) + radii.Y * w * MathF.Sin(ph), radii.Z * w * MathF.Cos(ph)), place);
        }
        Vector3 Midline(Vector3 p)
        {
            var x = Math.Clamp(Vector3.Transform(p, back).X, -radii.X, radii.X);
            return Vector3.Transform(new Vector3(x, Cup(x, radii.X), 0), place);
        }
        return Surface(name, Shape, Midline, colour, 0.6f);
    }
    private static ModelPart Sphere(string name, Vector3 centre, Vector3 radii, Vector4 colour, float roughness) =>
        Surface(name, (u, v) =>
        {
            var th = u * MathF.PI;
            var ph = v * MathF.Tau;
            return centre + new Vector3(radii.X * MathF.Sin(th) * MathF.Cos(ph), radii.Y * MathF.Cos(th), radii.Z * MathF.Sin(th) * MathF.Sin(ph));
        }, _ => centre, colour, roughness);

    // A grid surface over u (Rings steps) and v (Segments, wrapping). Normals are averaged face normals turned to point
    // away from inside(p). Both windings are emitted so the hat draws whatever the culling mode.
    private static ModelPart Surface(string name, Func<float, float, Vector3> f, Func<Vector3, Vector3> inside, Vector4 colour, float roughness,
        bool wrapU = false)
    {
        var rows = wrapU ? Rings : Rings + 1;
        var verts = new MeshVertex[rows * Segments];
        for (var i = 0; i < rows; i++)
            for (var j = 0; j < Segments; j++)
                verts[i * Segments + j] = new MeshVertex
                {
                    Position = f(i / (float)Rings, j / (float)Segments),
                    Uv = new Vector2(j / (float)Segments, i / (float)Rings),
                    Weights = new Vector4(1, 0, 0, 0),
                };

        var front = new List<uint>();
        var quadsU = Rings;
        for (var i = 0; i < quadsU; i++)
        {
            var i1 = wrapU ? (i + 1) % rows : i + 1;
            for (var j = 0; j < Segments; j++)
            {
                var j1 = (j + 1) % Segments;
                uint a = (uint)(i * Segments + j), b = (uint)(i * Segments + j1), c = (uint)(i1 * Segments + j), d = (uint)(i1 * Segments + j1);
                front.AddRange([a, c, b, b, c, d]);
            }
        }

        var normals = new Vector3[verts.Length];
        for (var k = 0; k < front.Count; k += 3)
        {
            var p0 = verts[front[k]].Position;
            var n = Vector3.Cross(verts[front[k + 1]].Position - p0, verts[front[k + 2]].Position - p0);
            normals[front[k]] += n;
            normals[front[k + 1]] += n;
            normals[front[k + 2]] += n;
        }
        for (var k = 0; k < verts.Length; k++)
        {
            var p = verts[k].Position;
            var n = normals[k];
            n = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.UnitY;
            var away = p - inside(p);
            if (Vector3.Dot(n, away) < 0) n = -n;
            verts[k].Normal = n;
        }

        var indices = new uint[front.Count * 2];
        for (var k = 0; k < front.Count; k += 3)
        {
            indices[k] = front[k];
            indices[k + 1] = front[k + 1];
            indices[k + 2] = front[k + 2];
            indices[front.Count + k] = front[k];
            indices[front.Count + k + 1] = front[k + 2];
            indices[front.Count + k + 2] = front[k + 1];
        }
        return new ModelPart(name, 0, false, PartKind.Lit, verts, indices, colour, roughness, false, 0, []);
    }

    // glTF base colours are linear; the pictures use sRGB.
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
