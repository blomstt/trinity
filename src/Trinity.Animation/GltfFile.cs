using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trinity.Animation;

/// <summary>
/// A glTF 2.0 file, <c>.glb</c> or <c>.gltf</c> (with <c>.bin</c> files or base64 <c>data:</c> buffers),
/// reduced to the parts Trinity reads. The JSON is parsed with build-time generated code, so it works
/// under NativeAOT. Data is raw: no axis or unit conversion.
/// </summary>
sealed class GltfFile
{
    public readonly GltfDocument Doc;
    readonly byte[][] _buffers;
    readonly string _folder;

    GltfFile(GltfDocument doc, byte[][] buffers, string folder)
    {
        Doc = doc;
        _buffers = buffers;
        _folder = folder;
    }

    public static GltfFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        ReadOnlySpan<byte> json = bytes;
        byte[]? bin = null;

        // GLB: 12-byte header, then chunks of (length, type, data); JSON first, then an optional BIN
        if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x46546C67)
        {
            json = default;
            // The header's total length bounds the chunks (a file with trailing bytes is fine, a short one isn't)
            var total = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
            if (total > (uint)bytes.Length)
                throw new InvalidDataException($"{path}: the GLB header says {total} bytes, the file has {bytes.Length}");
            var end = (int)total;
            for (var o = 12; o + 8 <= end;)
            {
                var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(o));
                var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(o + 4));
                if (length > (uint)(end - o - 8))
                    throw new InvalidDataException($"{path}: GLB chunk at byte {o} says {length} bytes, but only {end - o - 8} are left");
                if (type == 0x4E4F534A)
                    json = bytes.AsSpan(o + 8, (int)length);
                else if (type == 0x004E4942)
                    bin = bytes.AsSpan(o + 8, (int)length).ToArray();
                o += 8 + (int)length;
            }
            if (json.IsEmpty)
                throw new InvalidDataException($"{path}: GLB has no JSON chunk");
        }

        var doc = JsonSerializer.Deserialize(json, GltfJson.Default.GltfDocument)
            ?? throw new InvalidDataException($"{path} is not a glTF file");
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var buffers = doc.Buffers.Select(b => b.Uri switch
        {
            null => bin ?? throw new InvalidDataException($"{path} has no binary chunk"),
            var uri when uri.StartsWith("data:", StringComparison.Ordinal) => Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]),
            var uri => File.ReadAllBytes(Path.Combine(folder, Uri.UnescapeDataString(uri))),
        }).ToArray();
        return new GltfFile(doc, buffers, folder);
    }

    /// <summary>An image's encoded bytes (PNG, JPEG): from the binary chunk, a data: URI or a file next to the glTF.</summary>
    public byte[] ImageBytes(int index)
    {
        var image = Doc.Images[index];
        if (image.BufferView is { } view)
            return BufferView(view).ToArray();
        var uri = image.Uri ?? throw new InvalidDataException($"image {index} has no data");
        return uri.StartsWith("data:", StringComparison.Ordinal)
            ? Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..])
            : File.ReadAllBytes(Path.Combine(_folder, Uri.UnescapeDataString(uri)));
    }

    public ReadOnlySpan<byte> BufferView(int index)
    {
        var view = Doc.BufferViews[index];
        return _buffers[view.Buffer].AsSpan(view.ByteOffset, view.ByteLength);
    }
}

// The glTF schema, only the fields Trinity uses; anything else in the file is skipped

sealed class GltfDocument
{
    public GltfAccessor[] Accessors { get; set; } = [];
    public GltfBufferView[] BufferViews { get; set; } = [];
    public GltfBuffer[] Buffers { get; set; } = [];
    public GltfNode[] Nodes { get; set; } = [];
    public GltfMesh[] Meshes { get; set; } = [];
    public GltfSkin[] Skins { get; set; } = [];
    public GltfAnimation[] Animations { get; set; } = [];
    public GltfMaterial[] Materials { get; set; } = [];
    public GltfTexture[] Textures { get; set; } = [];
    public GltfImage[] Images { get; set; } = [];
    public GltfSampler2D[] Samplers { get; set; } = [];
}

sealed class GltfAccessor
{
    public int? BufferView { get; set; }
    public int ByteOffset { get; set; }
    public int ComponentType { get; set; } // 5120 byte, 5121 ubyte, 5122 short, 5123 ushort, 5125 uint, 5126 float
    public bool Normalized { get; set; }
    public int Count { get; set; }
    public string Type { get; set; } = "SCALAR";
}

sealed class GltfBufferView
{
    public int Buffer { get; set; }
    public int ByteOffset { get; set; }
    public int ByteLength { get; set; }
    public int? ByteStride { get; set; }
}

sealed class GltfBuffer
{
    public string? Uri { get; set; }
}

sealed class GltfNode
{
    public string? Name { get; set; }
    public int? Mesh { get; set; }
    public int? Skin { get; set; }
    public int[] Children { get; set; } = [];
    public float[]? Matrix { get; set; } // column-major
    public float[] Translation { get; set; } = [0, 0, 0];
    public float[] Rotation { get; set; } = [0, 0, 0, 1];
    public float[] Scale { get; set; } = [1, 1, 1];
}

sealed class GltfMesh
{
    public GltfPrimitive[] Primitives { get; set; } = [];
}

sealed class GltfPrimitive
{
    public Dictionary<string, int> Attributes { get; set; } = [];
    public int? Indices { get; set; }
    public int? Material { get; set; }
}

sealed class GltfSkin
{
    public int[] Joints { get; set; } = [];
    public int? InverseBindMatrices { get; set; }
}

sealed class GltfAnimation
{
    public string? Name { get; set; }
    public GltfChannel[] Channels { get; set; } = [];
    public GltfSampler[] Samplers { get; set; } = [];
}

sealed class GltfChannel
{
    public int Sampler { get; set; }
    public GltfTarget Target { get; set; } = new();
}

sealed class GltfTarget
{
    public int? Node { get; set; }
    public string Path { get; set; } = ""; // translation, rotation, scale or weights
}

sealed class GltfSampler
{
    public int Input { get; set; }
    public int Output { get; set; }
    public string Interpolation { get; set; } = "LINEAR"; // or STEP, CUBICSPLINE
}

sealed class GltfMaterial
{
    public string? Name { get; set; }
    public GltfPbr? PbrMetallicRoughness { get; set; }
    public GltfTextureRef? NormalTexture { get; set; }
    public GltfTextureRef? OcclusionTexture { get; set; }
    public GltfTextureRef? EmissiveTexture { get; set; }
    public float[] EmissiveFactor { get; set; } = [0, 0, 0];
    public string AlphaMode { get; set; } = "OPAQUE"; // or MASK, BLEND
    public float AlphaCutoff { get; set; } = 0.5f;
    public bool DoubleSided { get; set; }
    public GltfMaterialExtensions? Extensions { get; set; }
}

sealed class GltfMaterialExtensions
{
    [JsonPropertyName("KHR_materials_emissive_strength")]
    public GltfEmissiveStrength? EmissiveStrength { get; set; }
}

sealed class GltfEmissiveStrength
{
    public float EmissiveStrength { get; set; } = 1;
}

sealed class GltfPbr
{
    public float[] BaseColorFactor { get; set; } = [1, 1, 1, 1];
    public GltfTextureRef? BaseColorTexture { get; set; }
    public float MetallicFactor { get; set; } = 1;
    public float RoughnessFactor { get; set; } = 1;
    public GltfTextureRef? MetallicRoughnessTexture { get; set; }
}

sealed class GltfTextureRef
{
    public int Index { get; set; }
    public int TexCoord { get; set; }
    public float Scale { get; set; } = 1; // normal maps: strength
    public float Strength { get; set; } = 1; // occlusion: strength
}

sealed class GltfTexture
{
    public int? Sampler { get; set; }
    public int? Source { get; set; }
}

sealed class GltfImage
{
    public string? Uri { get; set; }
    public int? BufferView { get; set; }
    public string? MimeType { get; set; }
}

sealed class GltfSampler2D
{
    public int? MagFilter { get; set; } // 9728 nearest, 9729 linear
    public int? MinFilter { get; set; }
    public int WrapS { get; set; } = 10497; // 33071 clamp, 33648 mirrored repeat, 10497 repeat
    public int WrapT { get; set; } = 10497;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GltfDocument))]
sealed partial class GltfJson : JsonSerializerContext;
