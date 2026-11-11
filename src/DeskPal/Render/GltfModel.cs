using System.Numerics;
using System.Runtime.InteropServices;
using SharpGLTF.Schema2;

namespace DeskPal.Render;

[StructLayout(LayoutKind.Sequential)]
internal struct MeshVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
    public Vector4 Joints;
    public Vector4 Weights;
}

internal enum PartKind { Lit, Glint }

// MorphDeltas holds, per target then per vertex, a position delta followed by a normal delta.
internal sealed record ModelPart(string Name, int Node, bool Skinned, PartKind Kind, MeshVertex[] Vertices, uint[] Indices,
    Vector4 BaseColor, float Roughness, bool Textured, int MorphCount, Vector4[] MorphDeltas);

internal sealed record ModelNode(string Name, int Parent, Vector3 Scale, Quaternion Rotation, Vector3 Translation, float[] Weights, string[] TargetNames);

// CPU-side copy of a glTF character or prop: parts in bind space, the node tree, the optional skin and the clips.
internal sealed class GltfModel
{
    public const int MaxJoints = 24;
    public const int MaxMorphs = 12;

    public required ModelPart[] Parts { get; init; }
    public required ModelNode[] Nodes { get; init; }
    public required int[] Order { get; init; }
    public required int[] JointNodes { get; init; }
    public required Matrix4x4[] InverseBind { get; init; }
    public required Dictionary<string, AnimationClip> Clips { get; init; }
    public byte[]? Texture { get; init; }

    public int NodeIndex(string name) => Array.FindIndex(Nodes, n => n.Name == name);

    public static string? FindModelPath(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", name + ".glb");
        return File.Exists(path) ? path : null;
    }

    public static GltfModel Load(string path)
    {
        var root = ModelRoot.Load(path);
        var logical = root.LogicalNodes;
        var nodes = logical.Select(n =>
        {
            Matrix4x4.Decompose(n.LocalMatrix, out var s, out var r, out var t);
            var count = n.Mesh is null ? 0 : n.Mesh.Primitives.Max(p => p.MorphTargetsCount);
            var weights = new float[Math.Min(count, MaxMorphs)];
            var defaults = n.MorphWeights ?? n.Mesh?.MorphWeights;
            for (var i = 0; defaults is not null && i < weights.Length && i < defaults.Count; i++) weights[i] = defaults[i];
            var names = n.Mesh?.Extras?["targetNames"]?.AsArray().Select(x => x?.GetValue<string>() ?? "").ToArray() ?? [];
            return new ModelNode(n.Name ?? "", n.VisualParent?.LogicalIndex ?? -1, s, r, t, weights, names);
        }).ToArray();

        var depth = new int[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
            for (var p = nodes[i].Parent; p >= 0; p = nodes[p].Parent) depth[i]++;
        var order = Enumerable.Range(0, nodes.Length).OrderBy(i => depth[i]).ToArray();

        var joints = Array.Empty<int>();
        var inverse = Array.Empty<Matrix4x4>();
        if (root.LogicalSkins.FirstOrDefault() is { } skin)
        {
            if (skin.JointsCount > MaxJoints) throw new InvalidDataException($"skin has {skin.JointsCount} joints, at most {MaxJoints} are supported");
            joints = new int[skin.JointsCount];
            inverse = new Matrix4x4[skin.JointsCount];
            for (var i = 0; i < skin.JointsCount; i++)
            {
                var (joint, ibm) = skin.GetJoint(i);
                joints[i] = joint.LogicalIndex;
                inverse[i] = ibm;
            }
        }

        byte[]? texture = null;
        var parts = new List<ModelPart>();
        foreach (var node in logical.Where(n => n.Mesh is not null))
        {
            foreach (var prim in node.Mesh.Primitives)
            {
                var pos = prim.GetVertexAccessor("POSITION").AsVector3Array();
                var nrm = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var uv = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                var jnt = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                var wgt = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                var skinned = node.Skin is not null && jnt is not null && wgt is not null;
                var verts = new MeshVertex[pos.Count];
                for (var v = 0; v < verts.Length; v++)
                {
                    verts[v] = new MeshVertex
                    {
                        Position = pos[v],
                        Normal = nrm?[v] ?? Vector3.UnitY,
                        Uv = uv?[v] ?? Vector2.Zero,
                        Joints = skinned ? jnt![v] : Vector4.Zero,
                        Weights = skinned ? wgt![v] : new Vector4(1, 0, 0, 0),
                    };
                }

                var morphCount = Math.Min(prim.MorphTargetsCount, MaxMorphs);
                var deltas = new Vector4[morphCount * verts.Length * 2];
                for (var t = 0; t < morphCount; t++)
                {
                    var target = prim.GetMorphTargetAccessors(t);
                    var dp = target.TryGetValue("POSITION", out var a) ? a.AsVector3Array() : null;
                    var dn = target.TryGetValue("NORMAL", out var b) ? b.AsVector3Array() : null;
                    for (var v = 0; v < verts.Length; v++)
                    {
                        var at = (t * verts.Length + v) * 2;
                        if (dp is not null) deltas[at] = new Vector4(dp[v], 0);
                        if (dn is not null) deltas[at + 1] = new Vector4(dn[v], 0);
                    }
                }

                var color = Vector4.One;
                var roughness = 0.6f;
                var textured = false;
                var materialName = "";
                if (prim.Material is { } mat)
                {
                    materialName = mat.Name ?? "";
                    if (mat.FindChannel("BaseColor") is { } bc)
                    {
                        color = bc.Color;
                        if (bc.Texture?.PrimaryImage is { } img)
                        {
                            texture ??= img.Content.Content.ToArray();
                            textured = true;
                        }
                    }
                    if (mat.FindChannel("MetallicRoughness") is { } mr)
                        roughness = mr.GetFactor("RoughnessFactor");
                }

                var kind = materialName.Contains("glint", StringComparison.OrdinalIgnoreCase) ? PartKind.Glint : PartKind.Lit;
                parts.Add(new ModelPart(node.Name ?? node.Mesh.Name ?? "", node.LogicalIndex, skinned, kind, verts,
                    prim.GetIndices().ToArray(), color, roughness, textured, morphCount, deltas));
            }
        }

        var clips = root.LogicalAnimations.Select(AnimationClip.FromGltf).ToDictionary(c => c.Name);
        return new GltfModel
        {
            Parts = parts.ToArray(),
            Nodes = nodes,
            Order = order,
            JointNodes = joints,
            InverseBind = inverse,
            Clips = clips,
            Texture = texture,
        };
    }

    public void ComputeWorld(Pose pose, Span<Matrix4x4> world)
    {
        foreach (var i in Order)
        {
            var local = Matrix4x4.CreateScale(pose.S[i]) * Matrix4x4.CreateFromQuaternion(pose.R[i]) * Matrix4x4.CreateTranslation(pose.T[i]);
            var parent = Nodes[i].Parent;
            world[i] = parent >= 0 ? local * world[parent] : local;
        }
    }

    public void ComputeJoints(ReadOnlySpan<Matrix4x4> world, Span<Matrix4x4> joints)
    {
        for (var i = 0; i < JointNodes.Length; i++)
            joints[i] = InverseBind[i] * world[JointNodes[i]];
    }
}
