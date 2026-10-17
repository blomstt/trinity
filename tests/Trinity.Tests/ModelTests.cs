using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Trinity.Animation;
using Xunit;

namespace Trinity.Tests;

/// <summary>Loading glTF models: malformed files, static mesh transforms, rigs without animations, cubic curves.</summary>
public sealed class ModelTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("trinity-models-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A small glTF written by hand: float accessors in one buffer, embedded as a data: URI.</summary>
    sealed class Gltf
    {
        readonly List<byte> _bytes = [];
        readonly List<string> _views = [], _accessors = [];

        public int Floats(string type, params float[] values)
        {
            var comps = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => 16 };
            var offset = _bytes.Count;
            foreach (var v in values)
                _bytes.AddRange(BitConverter.GetBytes(v));
            _views.Add($$"""{ "buffer": 0, "byteOffset": {{offset}}, "byteLength": {{values.Length * 4}} }""");
            _accessors.Add($$"""{ "bufferView": {{_views.Count - 1}}, "componentType": 5126, "count": {{values.Length / comps}}, "type": "{{type}}" }""");
            return _accessors.Count - 1;
        }

        public string Json(string rest) =>
            $$"""
            {
              "asset": { "version": "2.0" },
              "buffers": [{ "byteLength": {{_bytes.Count}}, "uri": "data:application/octet-stream;base64,{{Convert.ToBase64String(_bytes.ToArray())}}" }],
              "bufferViews": [{{string.Join(",", _views)}}],
              "accessors": [{{string.Join(",", _accessors)}}],
              {{rest}}
            }
            """;
    }

    string Write(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>One triangle in the plane x + y = 0, wound so its face points along its normal (1, 1, 0)/√2.</summary>
    static string Triangle(Gltf g)
    {
        var n = 1 / MathF.Sqrt(2);
        var pos = g.Floats("VEC3", 0, 0, 0, 1, -1, 0, 0, 0, 1);
        var nrm = g.Floats("VEC3", n, n, 0, n, n, 0, n, n, 0);
        var idx = g.Floats("SCALAR", 0, 2, 1);
        return $$"""{ "primitives": [{ "attributes": { "POSITION": {{pos}}, "NORMAL": {{nrm}} }, "indices": {{idx}} }] }""";
    }

    /// <summary>The face direction from a baked triangle's winding.</summary>
    static Vector3 FaceNormal(SkinnedModel m) =>
        Vector3.Cross(m.Vertices[m.Indices[1]].Position - m.Vertices[m.Indices[0]].Position, m.Vertices[m.Indices[2]].Position - m.Vertices[m.Indices[0]].Position);

    [Fact]
    public void Static_normals_stay_perpendicular_under_non_uniform_scale()
    {
        var g = new Gltf();
        var mesh = Triangle(g);
        var path = Write("stretched.gltf", g.Json($$"""
            "meshes": [{{mesh}}],
            "nodes": [{ "mesh": 0, "scale": [4, 1, 1] }]
            """));
        var m = new SkinnedModel(path);
        var edge = m.Vertices[1].Position - m.Vertices[0].Position; // (4, -1, 0) after scaling
        Assert.Equal(0, Vector3.Dot(m.Vertices[0].Normal, Vector3.Normalize(edge)), 4);
        Assert.True(Vector3.Dot(FaceNormal(m), m.Vertices[0].Normal) > 0);
    }

    [Fact]
    public void Mirrored_static_nodes_keep_their_faces_pointing_out()
    {
        var g = new Gltf();
        var mesh = Triangle(g);
        var path = Write("mirrored.gltf", g.Json($$"""
            "meshes": [{{mesh}}],
            "nodes": [{ "mesh": 0, "scale": [-1, 1, 1] }]
            """));
        var m = new SkinnedModel(path);
        var normal = m.Vertices[0].Normal;
        Assert.Equal(-1 / MathF.Sqrt(2), normal.X, 4); // the mirror image of (1, 1, 0)/√2
        Assert.True(Vector3.Dot(FaceNormal(m), normal) > 0, "winding was not reversed for the mirrored node");
    }

    /// <summary>A rig of one joint ("Root", at y = 1) and a mesh node "BodyLOD0" skinned to it.</summary>
    static string Rig(Gltf g, string animations, bool inverseBind)
    {
        var mesh = Triangle(g);
        var ibm = inverseBind ? g.Floats("MAT4", 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -1, 0, 1) : -1;
        return g.Json($$"""
            "meshes": [{{mesh}}],
            "skins": [{ "joints": [0]{{(inverseBind ? $", \"inverseBindMatrices\": {ibm}" : "")}} }],
            "nodes": [{ "name": "Root", "translation": [0, 1, 0] }, { "name": "BodyLOD0", "mesh": 0, "skin": 0 }]
            {{animations}}
            """);
    }

    [Fact]
    public void A_rig_without_animations_gets_a_still_rest_clip()
    {
        var g = new Gltf();
        var m = new SkinnedModel(Write("bindpose.gltf", Rig(g, "", inverseBind: true)));
        Assert.True(m.IsRigged);
        var clip = Assert.Single(m.Clips);
        Assert.Equal("rest", clip.Name);
        Assert.Equal(clip.FrameCount * m.JointCount * 3, m.BoneRows.Length);
        // At rest, joint global times inverse bind is the identity: vertices stay where they were modelled
        Assert.Equal(new Vector4(1, 0, 0, 0), m.BoneRows[0]);
        Assert.Equal(new Vector4(0, 1, 0, 0), m.BoneRows[1]);
        Assert.Equal(new Vector4(0, 0, 1, 0), m.BoneRows[2]);
    }

    [Fact]
    public void Cubic_spline_tracks_follow_their_tangents()
    {
        var g = new Gltf();
        var times = g.Floats("SCALAR", 0, 1);
        // Keys of (in-tangent, value, out-tangent): from x = 0 leaving at 3 units/s, to x = 1 arriving flat
        var values = g.Floats("VEC3", 0, 0, 0, 0, 0, 0, 3, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0);
        var animations = $$"""
            , "animations": [{ "name": "slide",
                "samplers": [{ "input": {{times}}, "output": {{values}}, "interpolation": "CUBICSPLINE" }],
                "channels": [{ "sampler": 0, "target": { "node": 0, "path": "translation" } }] }]
            """;
        var m = new SkinnedModel(Write("cubic.gltf", Rig(g, animations, inverseBind: false)));
        var clip = m.Clip("slide");
        // Frame 15 is t = 0.5: Hermite gives 0.125 * 3 (start tangent) + 0.5 * 1 = 0.875; linear would give 0.5
        var frame = clip.FirstFrame + SkinnedModel.BakeFps / 2;
        var row0 = m.BoneRows[frame * m.JointCount * 3];
        Assert.Equal(0.875f, row0.W, 4);
        // The ends land exactly on the keys
        Assert.Equal(0f, m.BoneRows[clip.FirstFrame * 3].W, 4);
        Assert.Equal(1f, m.BoneRows[(clip.FirstFrame + clip.FrameCount - 1) * 3].W, 4);
    }

    [Fact]
    public void A_glb_chunk_longer_than_the_file_is_an_error_not_a_hang()
    {
        var json = Encoding.UTF8.GetBytes("""{ "asset": { "version": "2.0" } }  """); // padded to 4 bytes
        var file = new byte[12 + 8 + json.Length + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)json.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 0x4E4F534A);
        json.CopyTo(file, 20);
        var bad = 20 + json.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(bad), 0xFFFFFFF8); // -8 as int: the loop never moved
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(bad + 4), 0x12345678);
        var path = Path.Combine(_dir, "bad.glb");
        File.WriteAllBytes(path, file);

        var load = Task.Run(() => new SkinnedModel(path));
        Assert.True(Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(5))).Result == load, "the loader hung");
        var error = Assert.IsType<InvalidDataException>(load.Exception?.InnerException);
        Assert.Contains("chunk", error.Message);
    }

    [Fact]
    public void A_glb_whose_header_claims_more_than_the_file_is_an_error()
    {
        var file = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), 1000);
        var path = Path.Combine(_dir, "short.glb");
        File.WriteAllBytes(path, file);
        Assert.Throws<InvalidDataException>(() => new SkinnedModel(path));
    }
}
