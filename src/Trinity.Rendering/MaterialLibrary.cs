using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Trinity.Animation;
using Trinity.Graphics;

namespace Trinity.Rendering;

/// <summary>How a texture is sampled.</summary>
public readonly record struct SamplerKey(TextureWrap WrapU = TextureWrap.Repeat, TextureWrap WrapV = TextureWrap.Repeat, bool Nearest = false);

/// <summary>A material as the renderer stores it. Texture fields are handles from <see cref="MaterialLibrary.LoadTexture"/>.</summary>
public sealed record MaterialDesc
{
    public Vector4 BaseColor { get; init; } = Vector4.One;
    public float Metallic { get; init; }
    public float Roughness { get; init; } = 0.7f;
    public Vector3 Emissive { get; init; }
    public float NormalScale { get; init; } = 1;
    public float OcclusionStrength { get; init; } = 1;
    public AlphaMode AlphaMode { get; init; }
    public float AlphaCutoff { get; init; } = 0.5f;
    public bool DoubleSided { get; init; }
    public int BaseColorTexture { get; init; } = MaterialLibrary.White;
    public int MetallicRoughnessTexture { get; init; } = MaterialLibrary.White;
    public int NormalTexture { get; init; } = MaterialLibrary.FlatNormal;
    public int OcclusionTexture { get; init; } = MaterialLibrary.White;
    public int EmissiveTexture { get; init; } = MaterialLibrary.White;
}

[StructLayout(LayoutKind.Sequential)]
struct GpuMaterial
{
    public Vector4 BaseColor;
    public Vector4 Emissive; // rgb, normal scale
    public Vector4 Params; // metallic, roughness, alpha cutoff, occlusion strength
    public uint BaseColorTexture, MetallicRoughnessTexture, NormalTexture, OcclusionTexture;
    public uint EmissiveTexture, AlphaMode, Flags, Pad;
}

/// <summary>
/// Every material and texture the renderer can draw with, bound once per frame as descriptor set 1:
/// materials in one storage buffer, textures in one array indexed by handle (bindless). On GPUs
/// without descriptor indexing the array is a small fixed size instead, and draws stay grouped per
/// material so indices are uniform. Handles stay valid for the resource's life.
/// <para>
/// Textures load in the background: <see cref="LoadTexture"/> returns at once with the handle showing
/// a placeholder; worker threads decode the image, and <see cref="Update"/> uploads a few each frame
/// (with mipmaps), so loading never stalls rendering.
/// </para>
/// </summary>
public sealed unsafe class MaterialLibrary : IDisposable
{
    public const int White = 0, FlatNormal = 1;
    const long UploadBudget = 64L << 20; // bytes of decoded image per frame

    readonly GraphicsDevice _gpu;
    readonly int _capacity;
    readonly VkDescriptorPool _pool;
    public readonly VkDescriptorSetLayout SetLayout;
    readonly VkDescriptorSet[] _sets = new VkDescriptorSet[GraphicsDevice.FramesInFlight];

    readonly GpuImage?[] _images;
    readonly VkSampler[] _samplerOf;
    readonly Dictionary<SamplerKey, VkSampler> _samplers = [];
    readonly Stack<int> _freeTextures = new();
    int _nextTexture;
    readonly HashSet<int>[] _dirty = [[], []];

    readonly ConcurrentQueue<Decoded> _decoded = new();
    int _loading;

    GpuBuffer _materials;
    int _materialCount;
    readonly Stack<int> _freeMaterials = new();
    readonly bool[] _materialsRebound = new bool[GraphicsDevice.FramesInFlight];
    readonly List<MaterialDesc?> _descs = [];

    /// <summary>Raised on a worker thread when a texture has been decoded and is waiting to upload (wake an idle loop).</summary>
    public event Action? TextureReady;

    public bool Bindless => _gpu.Features.Bindless;
    public int TextureCapacity => _capacity;
    public int TexturesLoading => _loading;

    sealed record Decoded(int Handle, int Width, int Height, byte[] Rgba, bool Srgb, SamplerKey Sampler);

    public MaterialLibrary(GraphicsDevice gpu)
    {
        _gpu = gpu;
        _capacity = gpu.Features.MaxTextures;
        _images = new GpuImage?[_capacity];
        _samplerOf = new VkSampler[_capacity];
        var device = gpu.Device;

        var all = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;
        Span<VkDescriptorSetLayoutBinding> bindings =
        [
            new() { Binding = 0, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = all },
            new() { Binding = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = _capacity, StageFlags = VkShaderStageFlags.FragmentBit },
        ];
        Span<VkDescriptorBindingFlags> flags =
        [
            0,
            Bindless ? VkDescriptorBindingFlags.PartiallyBoundBit | VkDescriptorBindingFlags.VariableDescriptorCountBit : 0,
        ];
        fixed (VkDescriptorSetLayoutBinding* b = bindings)
        fixed (VkDescriptorBindingFlags* f = flags)
        {
            var flagInfo = new VkDescriptorSetLayoutBindingFlagsCreateInfo { BindingCount = 2, BindingFlags = f };
            SetLayout = device.CreateDescriptorSetLayout(
                new VkDescriptorSetLayoutCreateInfo { Next = &flagInfo, BindingCount = 2, Bindings = b },
                null
            );
        }

        Span<VkDescriptorPoolSize> sizes =
        [
            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = GraphicsDevice.FramesInFlight },
            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = _capacity * GraphicsDevice.FramesInFlight },
        ];
        fixed (VkDescriptorPoolSize* s = sizes)
            _pool = device.CreateDescriptorPool(
                new VkDescriptorPoolCreateInfo { MaxSets = GraphicsDevice.FramesInFlight, PoolSizeCount = 2, PoolSizes = s },
                null
            );
        for (var i = 0; i < GraphicsDevice.FramesInFlight; i++)
        {
            var layout = SetLayout;
            var count = _capacity;
            var variable = new VkDescriptorSetVariableDescriptorCountAllocateInfo { DescriptorSetCount = 1, DescriptorCounts = &count };
            VkDescriptorSet set = default;
            device.AllocateDescriptorSets(
                new VkDescriptorSetAllocateInfo
                {
                    Next = Bindless ? &variable : null,
                    DescriptorPool = _pool,
                    DescriptorSetCount = 1,
                    SetLayouts = &layout,
                },
                new ReadOnlySpan<VkDescriptorSet>(&set, 1)
            );
            _sets[i] = set;
        }

        // The two built-in textures every material falls back on
        AddReady(Solid(new byte[] { 255, 255, 255, 255 }, srgb: true));
        AddReady(Solid(new byte[] { 128, 128, 255, 255 }, srgb: false));
        // Without partially bound arrays every slot must hold something
        if (!Bindless)
            for (var i = 2; i < _capacity; i++)
            {
                _samplerOf[i] = _samplerOf[White];
                MarkDirty(i);
            }

        _materials = new GpuBuffer(gpu, 256 * sizeof(GpuMaterial), VkBufferUsageFlags.StorageBufferBit);
        AddMaterial(new MaterialDesc()); // material 0: the default for meshes without one
    }

    GpuImage Solid(byte[] rgba, bool srgb)
    {
        var image = CreateImage(1, 1, srgb, mips: 1);
        using var staging = new GpuBuffer(_gpu, 4, VkBufferUsageFlags.TransferSrcBit);
        staging.Write<byte>(rgba);
        _gpu.Submit(cmd => Upload(cmd, image, staging));
        return image;
    }

    void AddReady(GpuImage image)
    {
        var handle = _nextTexture++;
        _images[handle] = image;
        _samplerOf[handle] = Sampler(new SamplerKey());
        MarkDirty(handle);
    }

    VkSampler Sampler(SamplerKey key)
    {
        if (_samplers.TryGetValue(key, out var s))
            return s;
        static VkSamplerAddressMode Mode(TextureWrap w) => w switch
        {
            TextureWrap.Clamp => VkSamplerAddressMode.ClampToEdge,
            TextureWrap.Mirror => VkSamplerAddressMode.MirroredRepeat,
            _ => VkSamplerAddressMode.Repeat,
        };
        var filter = key.Nearest ? VkFilter.Nearest : VkFilter.Linear;
        var anisotropy = MathF.Min(16, _gpu.Features.MaxAnisotropy);
        return _samplers[key] = _gpu.Device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = filter,
                MinFilter = filter,
                MipmapMode = key.Nearest ? VkSamplerMipmapMode.Nearest : VkSamplerMipmapMode.Linear,
                AddressModeU = Mode(key.WrapU),
                AddressModeV = Mode(key.WrapV),
                AddressModeW = VkSamplerAddressMode.Repeat,
                AnisotropyEnable = anisotropy > 1 && !key.Nearest,
                MaxAnisotropy = anisotropy,
                MaxLod = 1000,
            },
            null
        );
    }

    GpuImage CreateImage(int width, int height, bool srgb, int mips) =>
        new(_gpu, new GpuImageDesc
        {
            Format = srgb ? VkFormat.R8G8B8A8SRGB : VkFormat.R8G8B8A8UNorm,
            Width = width,
            Height = height,
            MipLevels = mips,
            Usage = VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit,
        });

    /// <summary>Copies <paramref name="staging"/> into mip 0, builds the other mips by downsampling, and leaves the image ready to sample.</summary>
    static void Upload(CommandBuffer cmd, GpuImage image, GpuBuffer staging)
    {
        image.Transition(cmd, VkImageLayout.TransferDstOptimal, discard: true);
        cmd.CopyBufferToImage(
            staging.Buffer,
            image.Image,
            VkImageLayout.TransferDstOptimal,
            [
                new VkBufferImageCopy
                {
                    ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                    ImageExtent = new VkExtent3D { Width = image.Width, Height = image.Height, Depth = 1 },
                },
            ]
        );
        int w = image.Width, h = image.Height;
        var mips = image.Desc.MipLevels;
        for (var m = 1; m < mips; m++)
        {
            image.TransitionMip(cmd, m - 1, VkImageLayout.TransferDstOptimal, VkImageLayout.TransferSrcOptimal);
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var blit = new VkImageBlit
            {
                SrcSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = m - 1, LayerCount = 1 },
                DstSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = m, LayerCount = 1 },
            };
            blit.SrcOffsets[1] = new VkOffset3D { X = w, Y = h, Z = 1 };
            blit.DstOffsets[1] = new VkOffset3D { X = nw, Y = nh, Z = 1 };
            cmd.BlitImage(image.Image, VkImageLayout.TransferSrcOptimal, image.Image, VkImageLayout.TransferDstOptimal, [blit], VkFilter.Linear);
            image.TransitionMip(cmd, m - 1, VkImageLayout.TransferSrcOptimal, VkImageLayout.ShaderReadOnlyOptimal);
            (w, h) = (nw, nh);
        }
        image.TransitionMip(cmd, mips - 1, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);
        image.SetLayout(VkImageLayout.ShaderReadOnlyOptimal);
    }

    void MarkDirty(int handle)
    {
        foreach (var d in _dirty)
            d.Add(handle);
    }

    /// <summary>
    /// Starts loading an encoded image (PNG, JPEG, ...) and returns its handle, which shows
    /// <paramref name="placeholder"/> until the real image arrives. Colour images are sRGB; data
    /// (normals, roughness) is not.
    /// </summary>
    public int LoadTexture(byte[] encoded, bool srgb, SamplerKey sampler = default, int placeholder = White)
    {
        var handle = AllocateTexture();
        if (handle < 0)
        {
            Log.Warn("render", $"texture array full ({_capacity}); using a placeholder");
            return placeholder;
        }
        _images[handle] = null;
        _samplerOf[handle] = _samplerOf[placeholder];
        _placeholderOf[handle] = placeholder;
        MarkDirty(handle);
        Interlocked.Increment(ref _loading);
        Task.Run(() =>
        {
            try
            {
                using var image = Stb.LoadFromMemory(encoded, desiredChannels: 4);
                if (image.IsNull())
                    throw new InvalidDataException("not an image stb can read");
                _decoded.Enqueue(new Decoded(handle, image.Width, image.Height, image.AsSpan().ToArray(), srgb, sampler));
            }
            catch (Exception e)
            {
                Log.Warn("render", $"couldn't decode a texture: {e.Message}");
                Interlocked.Decrement(ref _loading);
            }
            TextureReady?.Invoke();
        });
        return handle;
    }

    readonly Dictionary<int, int> _placeholderOf = [];

    int AllocateTexture() => _freeTextures.Count > 0 ? _freeTextures.Pop() : _nextTexture < _capacity ? _nextTexture++ : -1;

    /// <summary>Frees a texture once no frame in flight can still be drawing with it.</summary>
    public void ReleaseTexture(int handle)
    {
        if (handle <= FlatNormal)
            return;
        var image = _images[handle];
        _images[handle] = null;
        _samplerOf[handle] = _samplerOf[White];
        _placeholderOf.Remove(handle);
        MarkDirty(handle);
        _gpu.Release(() =>
        {
            image?.Dispose();
            _freeTextures.Push(handle);
        });
    }

    /// <summary>Adds a material and returns its index, for instance and draw data.</summary>
    public int AddMaterial(MaterialDesc desc)
    {
        var index = _freeMaterials.Count > 0 ? _freeMaterials.Pop() : _materialCount++;
        if ((index + 1) * sizeof(GpuMaterial) > _materials.Size)
        {
            // Grow: the old buffer stays alive until frames using it are done
            var bigger = new GpuBuffer(_gpu, _materials.Size * 2, VkBufferUsageFlags.StorageBufferBit);
            bigger.Write<byte>(_materials.Bytes);
            var old = _materials;
            _gpu.Release(old.Dispose);
            _materials = bigger;
            Array.Fill(_materialsRebound, false);
        }
        while (_descs.Count <= index)
            _descs.Add(null);
        SetMaterial(index, desc);
        return index;
    }

    /// <summary>Changes a material; frames already recorded may still show the old values.</summary>
    public void SetMaterial(int index, MaterialDesc d)
    {
        _descs[index] = d;
        _materials.AsSpan<GpuMaterial>()[index] = new GpuMaterial
        {
            BaseColor = d.BaseColor,
            Emissive = new Vector4(d.Emissive, d.NormalScale),
            Params = new Vector4(d.Metallic, d.Roughness, d.AlphaCutoff, d.OcclusionStrength),
            BaseColorTexture = (uint)d.BaseColorTexture,
            MetallicRoughnessTexture = (uint)d.MetallicRoughnessTexture,
            NormalTexture = (uint)d.NormalTexture,
            OcclusionTexture = (uint)d.OcclusionTexture,
            EmissiveTexture = (uint)d.EmissiveTexture,
            AlphaMode = (uint)d.AlphaMode,
            Flags = d.DoubleSided ? 1u : 0u,
        };
    }

    public MaterialDesc Material(int index) => _descs[index] ?? throw new ArgumentException($"no material {index}");

    public void ReleaseMaterial(int index)
    {
        if (index == 0)
            return;
        _descs[index] = null;
        _gpu.Release(() => _freeMaterials.Push(index));
    }

    /// <summary>
    /// Call once per frame, after <see cref="GraphicsDevice.BeginFrame"/> and before rendering: uploads
    /// decoded textures (within a time budget) and brings this frame's descriptor set up to date.
    /// </summary>
    public void Update(CommandBuffer cmd)
    {
        long uploaded = 0;
        while (uploaded < UploadBudget && _decoded.TryDequeue(out var d))
        {
            Upload(cmd, d, deferStaging: true);
            uploaded += d.Rgba.Length;
        }

        var f = _gpu.FrameIndex;
        if (!_materialsRebound[f])
        {
            var info = new VkDescriptorBufferInfo { Buffer = _materials.Buffer, Range = (ByteSize64)(ulong)_materials.Size };
            _gpu.Device.UpdateDescriptorSets(
                [new VkWriteDescriptorSet { DstSet = _sets[f], DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &info }],
                []
            );
            _materialsRebound[f] = true;
        }
        WriteTextures(f);
    }

    void Upload(CommandBuffer cmd, Decoded d, bool deferStaging)
    {
        Interlocked.Decrement(ref _loading);
        if (!_placeholderOf.ContainsKey(d.Handle))
            return; // released while it was decoding
        var mips = (int)MathF.Floor(MathF.Log2(Math.Max(d.Width, d.Height))) + 1;
        var image = CreateImage(d.Width, d.Height, d.Srgb, mips);
        var staging = new GpuBuffer(_gpu, d.Rgba.Length, VkBufferUsageFlags.TransferSrcBit);
        staging.Write<byte>(d.Rgba);
        Upload(cmd, image, staging);
        if (deferStaging)
            _gpu.Release(staging.Dispose);
        else
            _pendingStaging.Add(staging);
        _images[d.Handle] = image;
        _samplerOf[d.Handle] = Sampler(d.Sampler);
        _placeholderOf.Remove(d.Handle);
        MarkDirty(d.Handle);
    }

    readonly List<GpuBuffer> _pendingStaging = [];

    /// <summary>Blocks until every texture started so far is decoded and uploaded (for a load screen, or recordings).</summary>
    public void WaitForTextures()
    {
        while (Volatile.Read(ref _loading) > 0)
        {
            if (_decoded.IsEmpty)
            {
                Thread.Sleep(1);
                continue;
            }
            _gpu.Submit(cmd =>
            {
                while (_decoded.TryDequeue(out var d))
                    Upload(cmd, d, deferStaging: false);
            });
            foreach (var s in _pendingStaging)
                s.Dispose();
            _pendingStaging.Clear();
        }
    }

    void WriteTextures(int f)
    {
        var dirty = _dirty[f];
        if (dirty.Count == 0)
            return;
        var infos = new VkDescriptorImageInfo[dirty.Count];
        var writes = new VkWriteDescriptorSet[dirty.Count];
        fixed (VkDescriptorImageInfo* pi = infos)
        {
            var n = 0;
            foreach (var handle in dirty)
            {
                var image = _images[handle] ?? _images[_placeholderOf.GetValueOrDefault(handle, White)] ?? _images[White]!;
                infos[n] = new VkDescriptorImageInfo { Sampler = _samplerOf[handle], ImageView = image.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
                writes[n] = new VkWriteDescriptorSet
                {
                    DstSet = _sets[f],
                    DstBinding = 1,
                    DstArrayElement = handle,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = pi + n,
                };
                n++;
            }
            _gpu.Device.UpdateDescriptorSets(writes, []);
        }
        dirty.Clear();
    }

    /// <summary>This frame's set 1 (after <see cref="Update"/>).</summary>
    public VkDescriptorSet Set => _sets[_gpu.FrameIndex];

    public void Dispose()
    {
        _gpu.WaitIdle();
        foreach (var image in _images)
            image?.Dispose();
        foreach (var s in _samplers.Values)
            _gpu.Device.DestroySampler(s, null);
        _materials.Dispose();
        _gpu.Device.DestroyDescriptorPool(_pool, null);
        _gpu.Device.DestroyDescriptorSetLayout(SetLayout, null);
    }
}
