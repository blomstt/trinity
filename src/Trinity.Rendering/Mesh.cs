using System.Numerics;
using Brutal.VulkanApi;
using Trinity.Animation;
using Trinity.Graphics;

namespace Trinity.Rendering;

/// <summary>A run of indices drawn with one material, with what the renderer needs to batch it.</summary>
public readonly record struct MeshPart(int FirstIndex, int IndexCount, int Material, AlphaMode Alpha, bool DoubleSided, int Lod);

/// <summary>
/// A model's GPU data: vertices, indices, baked bone table, and its materials and textures in the
/// <see cref="MaterialLibrary"/>, shared by every instance drawn with it. Textures stream in after the
/// mesh is created; until they arrive the mesh draws with placeholders.
/// </summary>
public sealed class Mesh : IDisposable
{
    readonly GraphicsDevice _gpu;
    readonly MaterialLibrary _library;
    readonly List<int> _materials = [], _textures = [];

    public readonly SkinnedModel Model;
    public readonly GpuBuffer Vertices, Indices;
    public readonly GpuBoneTable Bones;
    public readonly string[] ClipNames;
    public readonly IReadOnlyList<MeshPart> Parts;

    /// <summary>Corners of the model's box in its own space (rest pose).</summary>
    public readonly Vector3 BoundsMin, BoundsMax;

    public unsafe Mesh(GraphicsDevice gpu, MaterialLibrary library, SkinnedModel model)
    {
        _gpu = gpu;
        _library = library;
        Model = model;
        Vertices = new GpuBuffer(gpu, Math.Max(1, model.Vertices.Length) * sizeof(SkinnedVertex), VkBufferUsageFlags.VertexBufferBit);
        Vertices.Write<SkinnedVertex>(model.Vertices);
        Indices = new GpuBuffer(gpu, Math.Max(1, model.Indices.Length) * sizeof(uint), VkBufferUsageFlags.IndexBufferBit);
        Indices.Write<uint>(model.Indices);
        ClipNames = model.Clips.Select(c => c.Name).ToArray();
        Bones = new GpuBoneTable(gpu, model, ClipNames);

        BoundsMin = new Vector3(float.MaxValue);
        BoundsMax = new Vector3(float.MinValue);
        foreach (var v in model.Vertices)
        {
            BoundsMin = Vector3.Min(BoundsMin, v.Position);
            BoundsMax = Vector3.Max(BoundsMax, v.Position);
        }
        if (model.Vertices.Length == 0)
            BoundsMin = BoundsMax = Vector3.Zero;

        // Materials, with their textures loading in the background
        var textures = new Dictionary<(int, bool), int>();
        int Texture(int index, bool srgb, int fallback)
        {
            if (index < 0 || index >= model.Textures.Count || model.Textures[index].Image is var image && (image < 0 || model.Images[image] == null))
                return fallback;
            if (textures.TryGetValue((index, srgb), out var handle))
                return handle;
            var t = model.Textures[index];
            handle = library.LoadTexture(model.Images[t.Image]!, srgb, new SamplerKey(t.WrapU, t.WrapV, t.Nearest), fallback);
            _textures.Add(handle);
            return textures[(index, srgb)] = handle;
        }
        var handles = model.Materials.Select(m =>
        {
            var handle = library.AddMaterial(new MaterialDesc
            {
                BaseColor = m.BaseColor,
                Metallic = m.Metallic,
                Roughness = m.Roughness,
                Emissive = m.Emissive,
                NormalScale = m.NormalScale,
                OcclusionStrength = m.OcclusionStrength,
                AlphaMode = m.AlphaMode,
                AlphaCutoff = m.AlphaCutoff,
                DoubleSided = m.DoubleSided,
                BaseColorTexture = Texture(m.BaseColorTexture, srgb: true, MaterialLibrary.White),
                MetallicRoughnessTexture = Texture(m.MetallicRoughnessTexture, srgb: false, MaterialLibrary.White),
                NormalTexture = Texture(m.NormalTexture, srgb: false, MaterialLibrary.FlatNormal),
                OcclusionTexture = Texture(m.OcclusionTexture, srgb: false, MaterialLibrary.White),
                EmissiveTexture = Texture(m.EmissiveTexture, srgb: true, MaterialLibrary.White),
            });
            _materials.Add(handle);
            return (Handle: handle, Desc: m);
        }).ToList();
        // The encoded images now live in the loader's tasks
        for (var i = 0; i < model.Images.Count; i++)
            model.Images[i] = null;

        Parts = model.Submeshes.Select(s =>
        {
            var (handle, desc) = s.Material >= 0 && s.Material < handles.Count ? handles[s.Material] : (0, new ModelMaterial());
            return new MeshPart(s.FirstIndex, s.IndexCount, handle, desc.AlphaMode, desc.DoubleSided, s.Lod);
        }).ToList();
    }

    public int Triangles => Parts.Where(p => p.Lod == 0).Sum(p => p.IndexCount) / 3;

    /// <summary>Index of a clip by name, ignoring case; the first clip (with a warning, once per name) if there's no such clip.</summary>
    public int Clip(string name)
    {
        for (var i = 0; i < ClipNames.Length; i++)
            if (string.Equals(ClipNames[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        if (name.Length > 0 && ClipNames.Length > 0 && _unknownClips.Add(name))
            Log.Warn("render", $"no clip '{name}' in this model (clips: {string.Join(", ", ClipNames)}); playing {ClipNames[0]}");
        return 0;
    }

    readonly HashSet<string> _unknownClips = [];

    public void Dispose()
    {
        foreach (var m in _materials)
            _library.ReleaseMaterial(m);
        foreach (var t in _textures)
            _library.ReleaseTexture(t);
        // Frames in flight may still draw it
        _gpu.Release(() =>
        {
            Bones.Dispose();
            Vertices.Dispose();
            Indices.Dispose();
        });
    }
}
