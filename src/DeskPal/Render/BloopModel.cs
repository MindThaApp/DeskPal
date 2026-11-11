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

internal enum PartKind { Body, Eye, Glint, Other }

internal sealed record ModelPart(string Name, PartKind Kind, MeshVertex[] Vertices, uint[] Indices, Vector4 BaseColor, float Roughness, bool Textured);

internal sealed record ModelNode(string Name, int Parent, Vector3 Scale, Quaternion Rotation, Vector3 Translation);

// CPU-side copy of a skinned glTF character: parts in bind space, the node tree and the skin.
// Joint matrices are rebuilt per frame from node TRS so a procedural pose (or a clip later) can drive them.
internal sealed class BloopModel
{
    public required ModelPart[] Parts { get; init; }
    public required ModelNode[] Nodes { get; init; }
    public required int[] JointNodes { get; init; }
    public required Matrix4x4[] InverseBind { get; init; }
    public byte[]? Texture { get; init; }

    public int NodeIndex(string name) => Array.FindIndex(Nodes, n => n.Name == name);

    public static string? FindModelPath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Models");
        var path = Path.Combine(dir, "bloop.glb");
        if (File.Exists(path)) return path;
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "bloop*.glb").FirstOrDefault() : null;
    }

    public static BloopModel Load(string path)
    {
        var root = ModelRoot.Load(path);
        var logical = root.LogicalNodes;
        var nodes = logical.Select(n =>
        {
            Matrix4x4.Decompose(n.LocalMatrix, out var s, out var r, out var t);
            return new ModelNode(n.Name ?? "", n.VisualParent?.LogicalIndex ?? -1, s, r, t);
        }).ToArray();

        var skin = root.LogicalSkins.FirstOrDefault() ?? throw new InvalidDataException("model has no skin");
        var joints = new int[skin.JointsCount];
        var inverse = new Matrix4x4[skin.JointsCount];
        for (var i = 0; i < skin.JointsCount; i++)
        {
            var (joint, ibm) = skin.GetJoint(i);
            joints[i] = joint.LogicalIndex;
            inverse[i] = ibm;
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
                var verts = new MeshVertex[pos.Count];
                for (var v = 0; v < verts.Length; v++)
                {
                    verts[v] = new MeshVertex
                    {
                        Position = pos[v],
                        Normal = nrm?[v] ?? Vector3.UnitY,
                        Uv = uv?[v] ?? Vector2.Zero,
                        Joints = jnt?[v] ?? Vector4.Zero,
                        Weights = wgt?[v] ?? new Vector4(1, 0, 0, 0),
                    };
                }

                var color = Vector4.One;
                var roughness = 0.6f;
                var textured = false;
                if (prim.Material is { } mat)
                {
                    var baseColor = mat.FindChannel("BaseColor");
                    if (baseColor is { } bc)
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

                var name = node.Mesh.Name ?? node.Name ?? "";
                var kind = name.Contains("glint", StringComparison.OrdinalIgnoreCase) ? PartKind.Glint
                    : name.Contains("eye", StringComparison.OrdinalIgnoreCase) ? PartKind.Eye
                    : name.Contains("body", StringComparison.OrdinalIgnoreCase) ? PartKind.Body
                    : PartKind.Other;
                parts.Add(new ModelPart(name, kind, verts, prim.GetIndices().ToArray(), color, roughness, textured));
            }
        }

        return new BloopModel { Parts = parts.ToArray(), Nodes = nodes, JointNodes = joints, InverseBind = inverse, Texture = texture };
    }

    public void ComputeJointMatrices(Func<int, Matrix4x4?> overrideLocal, Span<Matrix4x4> joints)
    {
        var world = new Matrix4x4[Nodes.Length];
        var done = new bool[Nodes.Length];
        for (var i = 0; i < JointNodes.Length; i++)
            joints[i] = InverseBind[i] * World(JointNodes[i]);

        Matrix4x4 World(int index)
        {
            if (done[index]) return world[index];
            var n = Nodes[index];
            var local = overrideLocal(index) ?? Local(n);
            var w = n.Parent >= 0 ? local * World(n.Parent) : local;
            world[index] = w;
            done[index] = true;
            return w;
        }
    }

    public static Matrix4x4 Local(ModelNode n) =>
        Matrix4x4.CreateScale(n.Scale) * Matrix4x4.CreateFromQuaternion(n.Rotation) * Matrix4x4.CreateTranslation(n.Translation);
}
