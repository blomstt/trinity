using System.Numerics;
using System.Runtime.InteropServices;

namespace Trinity.Animation;

[StructLayout(LayoutKind.Sequential)]
public struct SkinnedVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public ushort J0, J1, J2, J3; // joint indices into the skin
    public Vector4 Weights;
    public uint Color; // RGBA8: base colour (sRGB)
}

/// <summary>Where one baked clip sits in the bone-matrix table.</summary>
public readonly record struct BakedClip(string Name, int FirstFrame, int FrameCount, float Duration);

/// <summary>
/// A skinned character from a glTF file, with every animation baked at a fixed rate into one table of
/// bone matrices: frame f, joint j lives at (f * JointCount + j). The GPU skins characters from that table,
/// so playing an animation costs one lookup per bone and nothing on the CPU.
///
/// Reads glTF with <see cref="GltfFile"/>. A rigged model needs mesh nodes named *LOD0 (and
/// optionally *LOD1) sharing one skin. Any other glTF loads as a static model: every mesh, placed by
/// its node transforms, drawn with one still "bone" and a one-frame "static" clip, so the same
/// shaders draw both kinds. Materials give base colours; textures arrive with the renderer (E2).
/// </summary>
public sealed class SkinnedModel
{
    public const int BakeFps = 30;

    public SkinnedVertex[] Vertices = [];
    public uint[] Indices = [];
    public (int FirstIndex, int IndexCount) Lod0, Lod1;
    public int JointCount;
    public readonly List<BakedClip> Clips = [];
    /// <summary>Each bone matrix as three float4 rows of the 3x4 affine transform.</summary>
    public Vector4[] BoneRows = [];

    readonly GltfFile _gltf;
    readonly GltfDocument _doc;

    /// <summary>False for static models (no skeleton).</summary>
    public bool IsRigged { get; private set; }

    public SkinnedModel(string path)
    {
        _gltf = GltfFile.Load(path);
        _doc = _gltf.Doc;

        var vertices = new List<SkinnedVertex>();
        var indices = new List<uint>();
        var lod0 = FindMeshNode("LOD0");
        if (lod0?.Skin is { } skin)
        {
            IsRigged = true;
            var lod1 = FindMeshNode("LOD1") ?? lod0;
            Lod0 = AppendMesh(lod0, vertices, indices, Matrix4x4.Identity);
            Lod1 = AppendMesh(lod1, vertices, indices, Matrix4x4.Identity);
            Vertices = vertices.ToArray();
            Indices = indices.ToArray();
            Bake(_doc.Skins[skin]);
            return;
        }

        // Static: every mesh node, at its place in the scene
        var parent = ParentTable();
        var first = 0;
        for (var n = 0; n < _doc.Nodes.Length; n++)
            if (_doc.Nodes[n].Mesh is not null)
                AppendMesh(_doc.Nodes[n], vertices, indices, RestGlobal(n, parent));
        Lod0 = Lod1 = (first, indices.Count);
        Vertices = vertices.ToArray();
        Indices = indices.ToArray();
        JointCount = 1;
        var id = Matrix4x4.Identity;
        BoneRows = [new(id.M11, id.M21, id.M31, id.M41), new(id.M12, id.M22, id.M32, id.M42), new(id.M13, id.M23, id.M33, id.M43)];
        BoneRows = [.. BoneRows, .. BoneRows]; // two identical frames
        Clips.Add(new BakedClip("static", 0, 2, 0));
    }

    int[] ParentTable()
    {
        var parent = new int[_doc.Nodes.Length];
        Array.Fill(parent, -1);
        for (var n = 0; n < _doc.Nodes.Length; n++)
            foreach (var c in _doc.Nodes[n].Children)
                parent[c] = n;
        return parent;
    }

    Matrix4x4 RestGlobal(int n, int[] parent)
    {
        var node = _doc.Nodes[n];
        var local = node.Matrix is { Length: 16 } matrix
            ? ColumnMajor(matrix, 0)
            : Matrix4x4.CreateScale(node.Scale[0], node.Scale[1], node.Scale[2])
                * Matrix4x4.CreateFromQuaternion(new Quaternion(node.Rotation[0], node.Rotation[1], node.Rotation[2], node.Rotation[3]))
                * Matrix4x4.CreateTranslation(node.Translation[0], node.Translation[1], node.Translation[2]);
        return parent[n] >= 0 ? local * RestGlobal(parent[n], parent) : local;
    }

    GltfNode? FindMeshNode(string suffix) =>
        _doc.Nodes.FirstOrDefault(n => n.Mesh is not null && (n.Name ?? "").EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    (int, int) AppendMesh(GltfNode node, List<SkinnedVertex> vertices, List<uint> indices, Matrix4x4 transform)
    {
        var first = indices.Count;
        foreach (var prim in _doc.Meshes[node.Mesh!.Value].Primitives)
        {
            var baseVertex = (uint)vertices.Count;
            var pos = ReadFloats(prim.Attributes["POSITION"]);
            var nrm = prim.Attributes.TryGetValue("NORMAL", out var ni) ? ReadFloats(ni) : Enumerable.Repeat(new[] { 0f, 1f, 0f }, pos.Length / 3).SelectMany(x => x).ToArray();
            var count = pos.Length / 3;
            var joints = prim.Attributes.TryGetValue("JOINTS_0", out var j) ? ReadFloats(j) : new float[count * 4];
            var weights = prim.Attributes.TryGetValue("WEIGHTS_0", out var w0) ? ReadFloats(w0) : new float[count * 4];

            // Colour from the material
            var color = Vector4.One;
            if (prim.Material is { } m)
            {
                var mat = _doc.Materials[m];
                if (mat.PbrMetallicRoughness?.BaseColorFactor is [var r, var g, var b, ..])
                    color = new Vector4(r, g, b, 1);
            }
            var packed = PackColor(color);

            for (var v = 0; v < pos.Length / 3; v++)
            {
                var w = new Vector4(weights[v * 4], weights[v * 4 + 1], weights[v * 4 + 2], weights[v * 4 + 3]);
                var sum = w.X + w.Y + w.Z + w.W;
                vertices.Add(
                    new SkinnedVertex
                    {
                        Position = Vector3.Transform(new Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]), transform),
                        Normal = Vector3.Normalize(Vector3.TransformNormal(new Vector3(nrm[v * 3], nrm[v * 3 + 1], nrm[v * 3 + 2]), transform)),
                        J0 = (ushort)joints[v * 4], J1 = (ushort)joints[v * 4 + 1],
                        J2 = (ushort)joints[v * 4 + 2], J3 = (ushort)joints[v * 4 + 3],
                        Weights = sum > 0 ? w / sum : new Vector4(1, 0, 0, 0),
                        Color = packed,
                    }
                );
            }
            if (prim.Indices is { } ix)
                foreach (var i in ReadFloats(ix))
                    indices.Add(baseVertex + (uint)i);
            else
                for (var i = 0u; i < count; i++)
                    indices.Add(baseVertex + i);
        }
        return (first, indices.Count - first);
    }

    static uint PackColor(Vector4 linear)
    {
        // glTF base colours are linear; store sRGB bytes so the shader can unpack and linearise once
        static uint Srgb(float c) =>
            (uint)Math.Clamp(MathF.Round(255 * (c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f)), 0, 255);
        return Srgb(linear.X) | Srgb(linear.Y) << 8 | Srgb(linear.Z) << 16 | 255u << 24;
    }

    /// <summary>Reads any accessor as floats, following the byte stride and component type.</summary>
    float[] ReadFloats(int accessorIndex)
    {
        var acc = _doc.Accessors[accessorIndex];
        var view = _doc.BufferViews[acc.BufferView ?? throw new NotSupportedException("accessor without a buffer view")];
        var bytes = _gltf.BufferView(acc.BufferView.Value);
        var comps = acc.Type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT4" => 16,
            _ => throw new NotSupportedException(acc.Type),
        };
        var size = acc.ComponentType switch
        {
            Byte or UnsignedByte => 1,
            Short or UnsignedShort => 2,
            _ => 4,
        };
        var stride = view.ByteStride ?? comps * size;
        var result = new float[acc.Count * comps];
        for (var e = 0; e < acc.Count; e++)
        for (var c = 0; c < comps; c++)
        {
            var o = acc.ByteOffset + e * stride + c * size;
            float v = acc.ComponentType switch
            {
                Float => BitConverter.ToSingle(bytes.Slice(o, 4)),
                UnsignedInt => BitConverter.ToUInt32(bytes.Slice(o, 4)),
                UnsignedShort => BitConverter.ToUInt16(bytes.Slice(o, 2)),
                Short => BitConverter.ToInt16(bytes.Slice(o, 2)),
                UnsignedByte => bytes[o],
                _ => (sbyte)bytes[o],
            };
            if (acc.Normalized)
                v /= acc.ComponentType switch
                {
                    UnsignedByte => 255f,
                    Byte => 127f,
                    UnsignedShort => 65535f,
                    Short => 32767f,
                    _ => 1f,
                };
            result[e * comps + c] = v;
        }
        return result;
    }

    const int Byte = 5120, UnsignedByte = 5121, Short = 5122, UnsignedShort = 5123, UnsignedInt = 5125, Float = 5126;

    // --- Baking ---------------------------------------------------------------------------------

    sealed class Track
    {
        public float[] Times = [];
        public float[] Values = [];
        public int Width; // 3 for translation / scale, 4 for rotation
        public bool Step, Cubic;

        public Vector4 Sample(float t)
        {
            var stride = Cubic ? Width * 3 : Width;
            var offset = Cubic ? Width : 0; // cubic spline keys are (in-tangent, value, out-tangent)
            Vector4 At(int k) => Width == 4
                ? new Vector4(Values[k * stride + offset], Values[k * stride + offset + 1], Values[k * stride + offset + 2], Values[k * stride + offset + 3])
                : new Vector4(Values[k * stride + offset], Values[k * stride + offset + 1], Values[k * stride + offset + 2], 0);
            if (t <= Times[0])
                return At(0);
            var last = Times.Length - 1;
            if (t >= Times[last])
                return At(last);
            var k = Array.BinarySearch(Times, t);
            if (k >= 0)
                return At(k);
            k = ~k - 1;
            if (Step)
                return At(k);
            var f = (t - Times[k]) / (Times[k + 1] - Times[k]);
            if (Width != 4)
                return Vector4.Lerp(At(k), At(k + 1), f);
            var a = At(k);
            var b = At(k + 1);
            var q = Quaternion.Slerp(new Quaternion(a.X, a.Y, a.Z, a.W), new Quaternion(b.X, b.Y, b.Z, b.W), f);
            return new Vector4(q.X, q.Y, q.Z, q.W);
        }
    }

    void Bake(GltfSkin skin)
    {
        var joints = skin.Joints;
        JointCount = joints.Length;
        var ibm = skin.InverseBindMatrices is { } ibmIndex ? ReadFloats(ibmIndex) : null;
        var inverseBind = new Matrix4x4[JointCount];
        for (var j = 0; j < JointCount; j++)
            inverseBind[j] = ibm is null ? Matrix4x4.Identity : ColumnMajor(ibm, j * 16);

        var parent = ParentTable();

        var rows = new List<Vector4>();
        var frame = 0;
        foreach (var anim in _doc.Animations)
        {
            // Per node: translation, rotation, scale tracks (null = rest value)
            var tracks = new Dictionary<(int Node, string Path), Track>();
            float duration = 0;
            foreach (var ch in anim.Channels)
            {
                if (ch.Target.Node is not { } node || ch.Target.Path is not ("translation" or "rotation" or "scale"))
                    continue;
                var sampler = anim.Samplers[ch.Sampler];
                var track = new Track
                {
                    Times = ReadFloats(sampler.Input),
                    Values = ReadFloats(sampler.Output),
                    Width = ch.Target.Path == "rotation" ? 4 : 3,
                    Step = sampler.Interpolation == "STEP",
                    Cubic = sampler.Interpolation == "CUBICSPLINE",
                };
                tracks[(node, ch.Target.Path)] = track;
                duration = MathF.Max(duration, track.Times[^1]);
            }

            var frames = Math.Max(2, (int)MathF.Round(duration * BakeFps) + 1);
            var global = new Matrix4x4[_doc.Nodes.Length];
            var done = new bool[_doc.Nodes.Length];
            for (var f = 0; f < frames; f++)
            {
                var t = f / (float)BakeFps;
                Array.Clear(done);

                Matrix4x4 Global(int n)
                {
                    if (done[n])
                        return global[n];
                    var node = _doc.Nodes[n];
                    var tr = tracks.TryGetValue((n, "translation"), out var tt)
                        ? tt.Sample(t) : new Vector4(node.Translation[0], node.Translation[1], node.Translation[2], 0);
                    var ro = tracks.TryGetValue((n, "rotation"), out var rt)
                        ? rt.Sample(t) : new Vector4(node.Rotation[0], node.Rotation[1], node.Rotation[2], node.Rotation[3]);
                    var sc = tracks.TryGetValue((n, "scale"), out var st)
                        ? st.Sample(t) : new Vector4(node.Scale[0], node.Scale[1], node.Scale[2], 0);
                    var local = Matrix4x4.CreateScale(sc.X, sc.Y, sc.Z)
                        * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(ro.X, ro.Y, ro.Z, ro.W)))
                        * Matrix4x4.CreateTranslation(tr.X, tr.Y, tr.Z);
                    global[n] = parent[n] >= 0 ? local * Global(parent[n]) : local;
                    done[n] = true;
                    return global[n];
                }

                for (var j = 0; j < JointCount; j++)
                {
                    // Row-vector convention: vertex * inverseBind * jointGlobal
                    var m = inverseBind[j] * Global(joints[j]);
                    rows.Add(new Vector4(m.M11, m.M21, m.M31, m.M41));
                    rows.Add(new Vector4(m.M12, m.M22, m.M32, m.M42));
                    rows.Add(new Vector4(m.M13, m.M23, m.M33, m.M43));
                }
            }
            Clips.Add(new BakedClip(anim.Name ?? $"clip{Clips.Count}", frame, frames, duration));
            frame += frames;
        }
        BoneRows = rows.ToArray();
    }

    /// <summary>glTF stores matrices column-major for column vectors; in .NET's row-vector form that's the same 16 numbers in order.</summary>
    static Matrix4x4 ColumnMajor(float[] a, int o) =>
        new(a[o], a[o + 1], a[o + 2], a[o + 3], a[o + 4], a[o + 5], a[o + 6], a[o + 7],
            a[o + 8], a[o + 9], a[o + 10], a[o + 11], a[o + 12], a[o + 13], a[o + 14], a[o + 15]);

    public BakedClip Clip(string name) =>
        Clips.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } c
            ? c
            : throw new Exception($"No '{name}' animation in the model (has: {string.Join(", ", Clips.Select(x => x.Name))})");
}
