using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Trinity.Animation;
using Trinity.Graphics;

namespace Trinity.Rendering;

[StructLayout(LayoutKind.Sequential)]
struct FrameConstants
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 View;
    public Matrix4x4 InvViewProj;
    public Matrix4x4 Shadow0, Shadow1, Shadow2, Shadow3;
    public Vector4 CameraPos;
    public Vector4 SunDir;
    public Vector4 SunColor;
    public Vector4 CascadeSplits;
    public Vector4 ShadowTexel;
    public Vector4 ShadowParams; // on, blend band, normal bias (texels), resolution
    public Vector4 Fog;
    public Vector4 Params; // time, exposure, debug view, tone mapper
    public Vector4 Grid; // half size, centre x, centre z
}

[StructLayout(LayoutKind.Sequential)]
struct DrawConstants
{
    public uint Material, Pass, Pad0, Pad1;
}

[StructLayout(LayoutKind.Sequential)]
struct InstanceConstants
{
    public Vector4 Row0, Row1, Row2;
    public Vector4 Anim; // clip, seconds, loop, flags
    public uint Material, Pad0, Pad1, Pad2;
}

/// <summary>Where a frame ends up: the swapchain image (already in colour-attachment layout) or an image the caller samples later.</summary>
/// <param name="Image">An image the renderer leaves in ShaderReadOnly layout when done; null for the swapchain.</param>
public readonly record struct RenderOutput(VkImageView View, VkFormat Format, GpuImage? Image = null)
{
    public static RenderOutput Swapchain(GraphicsDevice gpu) => new(gpu.CurrentView, gpu.ColorFormat);

    public static RenderOutput To(GpuImage image) => new(image.View, image.Format, image);
}

/// <summary>The curve that turns HDR light into display colours.</summary>
public enum ToneMapper
{
    /// <summary>Natural, desaturates very bright colours towards white (as film does).</summary>
    AgX,
    /// <summary>The film-like ACES fit: more contrast and saturation.</summary>
    Aces,
}

/// <summary>Light and look for a frame: sun, sky, shadows, fog and the camera's exposure.</summary>
public sealed class RenderSettings
{
    /// <summary>Direction sunlight travels (from the sun towards the ground). See <see cref="SunAt"/>.</summary>
    public Vector3 SunDirection = SunAt(azimuth: 220, elevation: 50);
    /// <summary>Sunlight arriving at the top of the atmosphere; the sky and the lit ground scale with it.</summary>
    public float SunIlluminance = 6f;

    /// <summary>Aerosols (Mie scattering) relative to a clear day: higher is hazier, with a whiter sky and a softer sun.</summary>
    public float Haze = 1f;
    public float MieAnisotropy = 0.8f;
    /// <summary>Approximate light scattered more than once in the sky; brightens the sky towards the horizon and at dusk.</summary>
    public float MultipleScattering = 0.5f;
    public Vector3 GroundAlbedo = new(0.3f, 0.28f, 0.25f);

    public bool Shadows = true;
    /// <summary>How far from the camera shadows reach, in metres; four cascades share it.</summary>
    public float ShadowDistance = 80f;
    /// <summary>Between even (0) and logarithmic (1) cascade splits; higher keeps more detail close to the camera.</summary>
    public float CascadeSplit = 0.75f;
    /// <summary>Fraction of each cascade blended into the next, hiding the seam.</summary>
    public float CascadeBlend = 0.1f;
    /// <summary>Shadow lookups move this many texels off the surface along its normal, against acne.</summary>
    public float ShadowNormalBias = 1.5f;
    public float ShadowDepthBias = 1.5f, ShadowSlopeBias = 2.5f;

    /// <summary>Meshes with a far version (LOD1) switch to it beyond this many times their size from the camera.</summary>
    public float LodDistance = 25f;

    /// <summary>Exponential distance fog density (per metre), thinning with height by <see cref="FogHeightFalloff"/>; 0 for none.</summary>
    public float FogDensity = 0.002f;
    public float FogHeightFalloff = 0.05f;
    /// <summary>Height (metres) at which fog has its full density.</summary>
    public float FogHeight;

    /// <summary>Exposure compensation in photographic stops: +1 is twice as bright.</summary>
    public float ExposureStops;
    public ToneMapper ToneMapper = ToneMapper.AgX;

    /// <summary>The direction sunlight travels for a sun at <paramref name="azimuth"/> (degrees clockwise from north, -Z) and <paramref name="elevation"/> above the horizon.</summary>
    public static Vector3 SunAt(float azimuth, float elevation)
    {
        var a = float.DegreesToRadians(azimuth);
        var e = float.DegreesToRadians(elevation);
        var toSun = new Vector3(MathF.Sin(a) * MathF.Cos(e), MathF.Sin(e), -MathF.Cos(a) * MathF.Cos(e));
        return -toSun;
    }
}

/// <summary>
/// Trinity's renderer: draws meshes in HDR with a forward pipeline, then tone-maps into the output
/// with any overlays (the editor's grid) on top. One renderer serves any number of <see cref="RenderView"/>s.
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    internal const int ViewSamplers = 2;
    public const int ShadowResolution = 2048;

    readonly GraphicsDevice _gpu;
    readonly ShaderLibrary _shaders;
    internal readonly VkDescriptorSetLayout ViewSetLayout;
    readonly VkDescriptorSetLayout _boneSetLayout;
    readonly VkPipelineLayout _layout;
    readonly Dictionary<(AlphaMode, bool DoubleSided), VkPipeline> _meshPipelines = [];
    readonly Dictionary<(AlphaMode, bool DoubleSided), VkPipeline> _shadowPipelines = [];
    readonly Dictionary<bool, VkPipeline> _blendPipelines = [];
    readonly List<(float Distance, Mesh Mesh, MeshPart Part, int Instance)> _transparent = [];
    readonly VkPipeline _skyPipeline;
    internal readonly VkSampler ShadowSampler;

    /// <summary>The sky and its light, shared by all views.</summary>
    public readonly Sky Sky;

    /// <summary>GPU time per pass, a couple of frames behind.</summary>
    public readonly GpuTimer Timer;
    long _materialsUpdated = -1;

    /// <summary>Every material and texture, shared by all meshes and views.</summary>
    public readonly MaterialLibrary Materials;
    readonly Dictionary<VkFormat, (VkPipeline Tonemap, VkPipeline Grid, VkPipeline Lines, VkPipeline LinesOnTop)> _outputPipelines = [];
    Debug.Vertex[] _debugTested = [], _debugOnTop = [];
    internal readonly VkSampler ClampSampler;

    readonly Dictionary<Mesh, List<int>> _batches = [];
    readonly List<Mesh> _batchOrder = [];

    // Culling: each instance's bounding sphere and LOD, and per pass (0 the camera, 1-4 the cascades)
    // a run of visible instance indices per mesh and LOD
    const int Passes = 5;
    Vector4[] _spheres = [];
    byte[] _lods = [];
    bool[] _noShadows = [];
    readonly List<uint> _visible = [];
    readonly Dictionary<(Mesh Mesh, int Pass, int Lod), (int Offset, int Count)> _ranges = [];

    /// <summary>Last frame's counts: instances submitted, drawn by the camera after culling, triangles drawn by the camera and into shadows.</summary>
    public (int Instances, int Visible, long Triangles, long ShadowTriangles) Stats { get; private set; }

    public readonly RenderSettings Settings = new();

    public Renderer(GraphicsDevice gpu, ShaderLibrary shaders)
    {
        _gpu = gpu;
        _shaders = shaders;
        var device = gpu.Device;

        ShadowSampler = device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                AddressModeU = VkSamplerAddressMode.ClampToEdge,
                AddressModeV = VkSamplerAddressMode.ClampToEdge,
                AddressModeW = VkSamplerAddressMode.ClampToEdge,
                CompareEnable = true,
                CompareOp = VkCompareOp.GreaterOrEqual, // reversed depth: lit when at least as close to the light as the caster
            },
            null
        );
        // The shadow map's comparison sampler is fixed in the layout: Metal (MoltenVK) can't take one through a descriptor
        var shadowSampler = ShadowSampler;

        var all = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;
        Span<VkDescriptorSetLayoutBinding> view =
        [
            new() { Binding = 0, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = all },
            new() { Binding = 1, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = all },
            new() { Binding = 2, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
            new()
            {
                Binding = 3,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
                ImmutableSamplers = &shadowSampler,
            },
            new() { Binding = 4, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = all },
        ];
        fixed (VkDescriptorSetLayoutBinding* b = view)
            ViewSetLayout = device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = view.Length, Bindings = b }, null);
        Materials = new MaterialLibrary(gpu);
        Sky = new Sky(gpu, shaders);
        Timer = new GpuTimer(gpu);
        var bones = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.VertexBit,
        };
        _boneSetLayout = device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &bones }, null);
        var push = new VkPushConstantRange { StageFlags = all, Size = (ByteSize32)16 };
        _layout = device.CreatePipelineLayout([ViewSetLayout, Materials.SetLayout, _boneSetLayout, Sky.SetLayout], [push], null);

        ClampSampler = device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                AddressModeU = VkSamplerAddressMode.ClampToEdge,
                AddressModeV = VkSamplerAddressMode.ClampToEdge,
                AddressModeW = VkSamplerAddressMode.ClampToEdge,
            },
            null
        );

        foreach (var mode in new[] { AlphaMode.Opaque, AlphaMode.Mask })
            foreach (var doubleSided in new[] { false, true })
            {
                _meshPipelines[(mode, doubleSided)] = MeshPipeline(mode, doubleSided);
                _shadowPipelines[(mode, doubleSided)] = ShadowPipeline(mode, doubleSided);
            }
        foreach (var doubleSided in new[] { false, true })
            _blendPipelines[doubleSided] = Pipelines.Create(gpu, shaders, new GraphicsPipelineDesc
            {
                VertexShader = "mesh.vert",
                FragmentShader = gpu.Features.Bindless ? "mesh.frag" : "mesh_fallback.frag",
                Layout = _layout,
                Cull = doubleSided ? Cull.None : Cull.Back,
                DepthWrite = false,
                Blend = Blend.Alpha,
                ColorFormats = [RenderView.HdrFormat],
                Bindings = [MeshVertex],
                Constants = [SpecConstant.Bool(2, true), SpecConstant.Int(1, Materials.TextureCapacity)],
            });
        _skyPipeline = Pipelines.Create(gpu, shaders, new GraphicsPipelineDesc
        {
            VertexShader = "fullscreen.vert",
            FragmentShader = "sky.frag",
            Layout = _layout,
            Cull = Cull.None,
            DepthWrite = false, // drawn where nothing else is: the cleared far depth passes greater-or-equal
            ColorFormats = [RenderView.HdrFormat],
        });
        Log.Info("render", $"{(gpu.Features.Bindless ? "bindless" : "fixed")} texture array of {Materials.TextureCapacity}, {gpu.Features.MaxAnisotropy}x anisotropy");
    }

    static readonly VertexBinding MeshVertex = new(
        72, // sizeof(SkinnedVertex)
        PerInstance: false,
        new(0, VkFormat.R32G32B32SFloat, 0),
        new(1, VkFormat.R32G32B32SFloat, 12),
        new(2, VkFormat.R32G32B32A32SFloat, 24),
        new(3, VkFormat.R32G32SFloat, 40),
        new(4, VkFormat.R16G16B16A16UInt, 48),
        new(5, VkFormat.R32G32B32A32SFloat, 56)
    );

    VkPipeline MeshPipeline(AlphaMode mode, bool doubleSided) =>
        Pipelines.Create(_gpu, _shaders, new GraphicsPipelineDesc
        {
            VertexShader = "mesh.vert",
            FragmentShader = _gpu.Features.Bindless ? "mesh.frag" : "mesh_fallback.frag",
            Layout = _layout,
            Cull = doubleSided ? Cull.None : Cull.Back,
            ColorFormats = [RenderView.HdrFormat],
            Bindings = [MeshVertex],
            Constants = [SpecConstant.Bool(0, mode == AlphaMode.Mask), SpecConstant.Int(1, Materials.TextureCapacity)],
        });

    VkPipeline ShadowPipeline(AlphaMode mode, bool doubleSided) =>
        Pipelines.Create(_gpu, _shaders, new GraphicsPipelineDesc
        {
            VertexShader = "shadow.vert",
            // Opaque casters only write depth; masked ones cut out their holes
            FragmentShader = mode == AlphaMode.Mask ? (_gpu.Features.Bindless ? "shadow_mask.frag" : "shadow_mask_fallback.frag") : null,
            Layout = _layout,
            Cull = doubleSided ? Cull.None : Cull.Back,
            ColorFormats = [],
            DepthBias = true,
            DepthClamp = true,
            Bindings = [MeshVertex],
            Constants = [SpecConstant.Int(1, Materials.TextureCapacity)],
        });

    /// <summary>Makes a model's GPU mesh, with its materials registered and textures loading.</summary>
    public Mesh CreateMesh(SkinnedModel model) => new(_gpu, Materials, model);

    public RenderView CreateView(int width, int height) => new(_gpu, this, width, height);

    (VkPipeline Tonemap, VkPipeline Grid, VkPipeline Lines, VkPipeline LinesOnTop) OutputPipelines(VkFormat format)
    {
        if (_outputPipelines.TryGetValue(format, out var p))
            return p;
        p.Tonemap = Pipelines.Create(_gpu, _shaders, new GraphicsPipelineDesc
        {
            VertexShader = "fullscreen.vert",
            FragmentShader = "tonemap.frag",
            Layout = _layout,
            Cull = Cull.None,
            DepthTest = false,
            DepthWrite = false,
            ColorFormats = [format],
        });
        p.Grid = Pipelines.Create(_gpu, _shaders, new GraphicsPipelineDesc
        {
            VertexShader = "grid.vert",
            FragmentShader = "grid.frag",
            Layout = _layout,
            Cull = Cull.None,
            DepthWrite = false,
            Blend = Blend.Alpha,
            ColorFormats = [format],
        });
        var lines = new GraphicsPipelineDesc
        {
            VertexShader = "debug.vert",
            FragmentShader = "debug.frag",
            Layout = _layout,
            Topology = VkPrimitiveTopology.LineList,
            Cull = Cull.None,
            DepthWrite = false,
            Blend = Blend.Alpha,
            ColorFormats = [format],
            Bindings = [new VertexBinding(16, PerInstance: false, new(0, VkFormat.R32G32B32SFloat, 0), new(1, VkFormat.R8G8B8A8UNorm, 12))],
        };
        p.Lines = Pipelines.Create(_gpu, _shaders, lines);
        p.LinesOnTop = Pipelines.Create(_gpu, _shaders, lines with { DepthTest = false });
        return _outputPipelines[format] = p;
    }

    /// <summary>
    /// Renders <paramref name="draws"/> as seen by <paramref name="camera"/> into <paramref name="view"/>'s
    /// targets, then tone-maps into <paramref name="output"/>, which must be the view's size.
    /// </summary>
    public void Render(CommandBuffer cmd, RenderView view, in Camera camera, DrawList draws, RenderOutput output, double time = 0, Vector3 gridCentre = default)
    {
        var f = _gpu.FrameIndex;
        if (_materialsUpdated != _gpu.FrameNumber)
        {
            Timer.Begin(cmd);
            Materials.Update(cmd);
            Timer.Mark(cmd, "uploads");
            Sky.Update(cmd, Settings);
            Timer.Mark(cmd, "sky update");
            (_debugTested, _debugOnTop) = Debug.TakeFrame(time);
            _materialsUpdated = _gpu.FrameNumber;
        }
        WriteFrame(view, f, camera, draws, time, gridCentre);

        // Shadow pass: each cascade's depth from the sun
        view.Shadow.Transition(cmd, VkImageLayout.DepthAttachmentOptimal, discard: true);
        for (var c = 0; c < 4; c++)
        {
            BeginShadow(cmd, view.Shadow.ViewOf(0, c));
            if (Settings.Shadows)
            {
                cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [view.Sets[f], Materials.Set], []);
                cmd.SetDepthBias(-Settings.ShadowDepthBias, 0, -Settings.ShadowSlopeBias); // reversed depth: away from the light is smaller
                foreach (var ((mode, doubleSided), pipeline) in _shadowPipelines)
                {
                    cmd.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
                    DrawParts(cmd, part => part.Alpha == mode && part.DoubleSided == doubleSided, pass: c + 1);
                }
            }
            cmd.EndRendering();
        }
        view.Shadow.Transition(cmd, VkImageLayout.ShaderReadOnlyOptimal);
        Timer.Mark(cmd, "shadows");

        // Opaque pass, into HDR, then the sky behind it
        view.Hdr.Transition(cmd, VkImageLayout.ColorAttachmentOptimal, discard: true);
        view.Depth.Transition(cmd, VkImageLayout.DepthAttachmentOptimal, discard: true);
        BeginRendering(cmd, view, view.Hdr.View, VkAttachmentLoadOp.DontCare, default, VkAttachmentLoadOp.Clear);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [view.Sets[f], Materials.Set], []);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 3, [Sky.Set], []);
        foreach (var ((mode, doubleSided), pipeline) in _meshPipelines)
        {
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
            DrawParts(cmd, part => part.Alpha == mode && part.DoubleSided == doubleSided);
        }
        Timer.Mark(cmd, "opaque");
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _skyPipeline);
        cmd.Draw(3, 1, 0, 0);
        Timer.Mark(cmd, "sky");
        DrawTransparent(cmd);
        cmd.EndRendering();
        Timer.Mark(cmd, "transparent");

        // Tone mapping into the output, then overlays tested against the scene's depth
        view.Hdr.Transition(cmd, VkImageLayout.ShaderReadOnlyOptimal);
        output.Image?.Transition(cmd, VkImageLayout.ColorAttachmentOptimal, discard: true);
        BeginRendering(cmd, view, output.View, VkAttachmentLoadOp.DontCare, default, VkAttachmentLoadOp.Load);
        var (tonemap, grid, lines, linesOnTop) = OutputPipelines(output.Format);
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, tonemap);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [view.Sets[f]], []);
        cmd.Draw(3, 1, 0, 0);
        if (view.GridHalfSize > 0)
        {
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, grid);
            cmd.Draw(6, 1, 0, 0);
        }
        DrawDebugLines(cmd, view, f, lines, linesOnTop);
        cmd.EndRendering();
        output.Image?.Transition(cmd, VkImageLayout.ShaderReadOnlyOptimal);
        Timer.Mark(cmd, "tone map + overlays");
    }

    /// <summary>Every mesh's level-0 parts that <paramref name="include"/> picks, each as one draw over the mesh's instances.</summary>
    /// <summary>
    /// Blended parts of visible instances, each on its own, farthest first so nearer surfaces blend over
    /// farther ones. Sorted by the distance to each instance's centre, which is right for separate
    /// objects. Each gets its own entry in the visible list.
    /// </summary>
    void CollectTransparent(Vector3 eye, Frustum frustum)
    {
        _transparent.Clear();
        var first = 0;
        foreach (var mesh in _batchOrder)
        {
            var count = _batches[mesh].Count;
            foreach (var part in mesh.Parts)
            {
                if (part.Lod != 0 || part.Alpha != AlphaMode.Blend)
                    continue;
                for (var i = first; i < first + count; i++)
                {
                    var s = _spheres[i];
                    var centre = new Vector3(s.X, s.Y, s.Z);
                    if (frustum.Contains(centre, s.W))
                        _transparent.Add((Vector3.DistanceSquared(centre, eye), mesh, part, i));
                }
            }
            first += count;
        }
        _transparent.Sort((a, b) => b.Distance.CompareTo(a.Distance));
        for (var t = 0; t < _transparent.Count; t++)
        {
            var (d, mesh, part, instance) = _transparent[t];
            _transparent[t] = (d, mesh, part, _visible.Count);
            _visible.Add((uint)instance);
        }
    }

    void DrawTransparent(CommandBuffer cmd)
    {
        if (_transparent.Count == 0)
            return;
        Mesh? bound = null;
        bool? sides = null;
        foreach (var (_, mesh, part, instance) in _transparent)
        {
            if (sides != part.DoubleSided)
            {
                cmd.BindPipeline(VkPipelineBindPoint.Graphics, _blendPipelines[part.DoubleSided]);
                sides = part.DoubleSided;
            }
            if (bound != mesh)
            {
                cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 2, [mesh.Bones.Set], []);
                cmd.BindVertexBuffers(0, [mesh.Vertices.Buffer], [(ByteSize64)0]);
                cmd.BindIndexBuffer(mesh.Indices.Buffer, (ByteSize64)0, VkIndexType.UInt32);
                bound = mesh;
            }
            var push = new DrawConstants { Material = (uint)part.Material };
            cmd.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<DrawConstants>(in push)));
            cmd.DrawIndexed(part.IndexCount, 1, part.FirstIndex, 0, instance);
        }
    }

    void DrawDebugLines(CommandBuffer cmd, RenderView view, int f, VkPipeline tested, VkPipeline onTop)
    {
        var count = _debugTested.Length + _debugOnTop.Length;
        if (count == 0)
            return;
        var bytes = (long)count * sizeof(Debug.Vertex);
        if (view.DebugLines[f] is not { } buffer || buffer.Size < bytes)
        {
            view.DebugLines[f]?.Dispose(); // safe: this frame slot's previous use is finished
            buffer = view.DebugLines[f] = new GpuBuffer(_gpu, Math.Max(bytes, 64 * 1024), VkBufferUsageFlags.VertexBufferBit);
        }
        var span = buffer.AsSpan<Debug.Vertex>();
        _debugTested.CopyTo(span);
        _debugOnTop.CopyTo(span[_debugTested.Length..]);
        cmd.BindVertexBuffers(0, [buffer.Buffer], [(ByteSize64)0]);
        if (_debugTested.Length > 0)
        {
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, tested);
            cmd.Draw(_debugTested.Length, 1, 0, 0);
        }
        if (_debugOnTop.Length > 0)
        {
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, onTop);
            cmd.Draw(_debugOnTop.Length, 1, _debugTested.Length, 0);
        }
    }

    void BeginShadow(CommandBuffer cmd, VkImageView layer)
    {
        var depth = new VkRenderingAttachmentInfo
        {
            ImageView = layer,
            ImageLayout = VkImageLayout.DepthAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 0f } },
        };
        var extent = new VkExtent2D { Width = ShadowResolution, Height = ShadowResolution };
        cmd.BeginRendering(new VkRenderingInfo { RenderArea = new VkRect2D { Extent = extent }, LayerCount = 1, DepthAttachment = &depth });
        cmd.SetViewport(0, [new VkViewport { Width = ShadowResolution, Height = ShadowResolution, MaxDepth = 1f }]);
        cmd.SetScissor(0, [new VkRect2D { Extent = extent }]);
    }

    /// <summary>Every mesh's parts that <paramref name="include"/> picks, each LOD as one draw over that pass's visible instances.</summary>
    void DrawParts(CommandBuffer cmd, Func<MeshPart, bool> include, int pass = 0)
    {
        foreach (var mesh in _batchOrder)
        {
            var bound = false;
            var hasLod1 = HasLod1(mesh);
            for (var lod = 0; lod < 2; lod++)
            {
                if (!_ranges.TryGetValue((mesh, pass, lod), out var range) || range.Count == 0)
                    continue;
                var partLod = hasLod1 ? lod : 0;
                foreach (var part in mesh.Parts)
                {
                    if (part.Lod != partLod || !include(part))
                        continue;
                    if (!bound)
                    {
                        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 2, [mesh.Bones.Set], []);
                        cmd.BindVertexBuffers(0, [mesh.Vertices.Buffer], [(ByteSize64)0]);
                        cmd.BindIndexBuffer(mesh.Indices.Buffer, (ByteSize64)0, VkIndexType.UInt32);
                        bound = true;
                    }
                    var push = new DrawConstants { Material = (uint)part.Material, Pass = (uint)Math.Max(0, pass - 1) };
                    cmd.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<DrawConstants>(in push)));
                    cmd.DrawIndexed(part.IndexCount, range.Count, part.FirstIndex, 0, range.Offset);
                }
            }
        }
    }

    static bool HasLod1(Mesh mesh) => mesh.Parts.Any(p => p.Lod == 1);

    /// <summary>
    /// For each pass (the camera, then each cascade), the instances whose bounding spheres touch its
    /// view, split by level of detail (the far version past <see cref="RenderSettings.LodDistance"/>
    /// times the instance's size). The result is index lists the vertex shaders read through.
    /// </summary>
    void CullInstances(Vector3 eye, Matrix4x4 viewProj, ReadOnlySpan<Matrix4x4> shadow)
    {
        _visible.Clear();
        _ranges.Clear();
        Span<Frustum> frusta = stackalloc Frustum[Passes];
        frusta[0] = new Frustum(viewProj);
        // Shadow matrices map to 0..1; frusta want clip space (-1..1)
        var toClip = new Matrix4x4(2, 0, 0, 0, 0, 2, 0, 0, 0, 0, 1, 0, -1, -1, 0, 1);
        for (var c = 0; c < 4; c++)
            frusta[c + 1] = new Frustum(shadow[c] * toClip);

        var instances = _batchOrder.Sum(m => _batches[m].Count);
        long triangles = 0, shadowTriangles = 0;
        var visibleCount = 0;
        for (var pass = 0; pass < (Settings.Shadows ? Passes : 1); pass++)
        {
            var first = 0;
            foreach (var mesh in _batchOrder)
            {
                var count = _batches[mesh].Count;
                var lods = HasLod1(mesh) ? 2 : 1;
                for (var lod = 0; lod < lods; lod++)
                {
                    var start = _visible.Count;
                    for (var i = first; i < first + count; i++)
                    {
                        if ((_lods[i] != lod && lods == 2) || (pass > 0 && _noShadows[i]))
                            continue;
                        var s = _spheres[i];
                        if (frusta[pass].Contains(new Vector3(s.X, s.Y, s.Z), s.W))
                            _visible.Add((uint)i);
                    }
                    var n = _visible.Count - start;
                    _ranges[(mesh, pass, lod)] = (start, n);
                    var tris = (long)n * mesh.Parts.Where(p => p.Lod == (lods == 2 ? lod : 0)).Sum(p => p.IndexCount) / 3;
                    if (pass == 0)
                    {
                        triangles += tris;
                        visibleCount += n;
                    }
                    else
                        shadowTriangles += tris;
                }
                first += count;
            }
        }
        CollectTransparent(eye, frusta[0]);
        Stats = (instances, visibleCount, triangles, shadowTriangles);
    }

    void WriteFrame(RenderView view, int f, in Camera camera, DrawList draws, double time, Vector3 gridCentre)
    {
        // Group by mesh, so each mesh is one instanced draw over a contiguous run of instances
        foreach (var list in _batches.Values)
            list.Clear();
        _batchOrder.Clear();
        for (var i = 0; i < draws.Count; i++)
        {
            var mesh = draws.Items[i].Mesh;
            if (!_batches.TryGetValue(mesh, out var list))
                _batches[mesh] = list = [];
            if (list.Count == 0)
                _batchOrder.Add(mesh);
            list.Add(i);
        }
        // Forget meshes not drawn this frame, so disposed ones aren't held
        foreach (var gone in _batches.Where(b => b.Value.Count == 0).Select(b => b.Key).ToList())
            _batches.Remove(gone);

        var instances = view.EnsureInstances(f, Math.Max(1, draws.Count)).AsSpan<InstanceConstants>();
        if (_spheres.Length < draws.Count)
        {
            _spheres = new Vector4[draws.Count * 2];
            _lods = new byte[draws.Count * 2];
            _noShadows = new bool[draws.Count * 2];
        }
        var n = 0;
        foreach (var mesh in _batchOrder)
        {
            var localCentre = (mesh.BoundsMin + mesh.BoundsMax) / 2;
            // Animation can reach past the rest pose's box: give skinned meshes some room
            var localRadius = (mesh.BoundsMax - mesh.BoundsMin).Length() / 2 * (mesh.Model.IsRigged ? 1.3f : 1f);
            foreach (var i in _batches[mesh])
            {
                var item = draws.Items[i];
                var m = item.World;
                var centre = Vector3.Transform(localCentre, m);
                var scale = MathF.Sqrt(MathF.Max(new Vector3(m.M11, m.M12, m.M13).LengthSquared(), MathF.Max(new Vector3(m.M21, m.M22, m.M23).LengthSquared(), new Vector3(m.M31, m.M32, m.M33).LengthSquared())));
                var radius = localRadius * scale;
                _spheres[n] = new Vector4(centre, radius);
                _noShadows[n] = item.NoShadows;
                _lods[n] = (byte)(Vector3.Distance(centre, camera.Position) > radius * Settings.LodDistance ? 1 : 0);
                instances[n++] = new InstanceConstants
                {
                    Row0 = new Vector4(m.M11, m.M21, m.M31, m.M41),
                    Row1 = new Vector4(m.M12, m.M22, m.M32, m.M42),
                    Row2 = new Vector4(m.M13, m.M23, m.M33, m.M43),
                    Anim = new Vector4(item.Pose.Clip, item.Pose.Seconds, item.Pose.Loop ? 1 : 0, item.Highlight ? 1 : 0),
                    Material = uint.MaxValue, // the part's own material
                };
            }
        }

        var s = Settings;
        var aspect = view.Width / (float)view.Height;
        var viewProj = camera.ViewProjection(aspect);
        Matrix4x4.Invert(viewProj, out var invViewProj);
        Span<Matrix4x4> shadow = stackalloc Matrix4x4[4];
        Span<float> splits = stackalloc float[4];
        Span<float> texel = stackalloc float[4];
        Cascades.Fit(camera, aspect, Vector3.Normalize(s.SunDirection), MathF.Min(s.ShadowDistance, camera.Far), s.CascadeSplit, ShadowResolution, shadow, splits, texel);
        CullInstances(camera.Position, viewProj, shadow);
        var visible = view.EnsureVisible(f, Math.Max(1, _visible.Count)).AsSpan<uint>();
        for (var i = 0; i < _visible.Count; i++)
            visible[i] = _visible[i];
        view.FrameData[f].AsSpan<FrameConstants>()[0] = new FrameConstants
        {
            ViewProj = viewProj,
            View = camera.View,
            InvViewProj = invViewProj,
            Shadow0 = shadow[0],
            Shadow1 = shadow[1],
            Shadow2 = shadow[2],
            Shadow3 = shadow[3],
            CameraPos = new Vector4(camera.Position, 1),
            SunDir = new Vector4(Vector3.Normalize(s.SunDirection), 0),
            SunColor = new Vector4(Sky.SunColor, 0),
            CascadeSplits = new Vector4(splits[0], splits[1], splits[2], splits[3]),
            ShadowTexel = new Vector4(texel[0], texel[1], texel[2], texel[3]),
            ShadowParams = new Vector4(s.Shadows ? 1 : 0, s.CascadeBlend, s.ShadowNormalBias, ShadowResolution),
            Fog = new Vector4(s.FogDensity, s.FogHeightFalloff, s.FogHeight, 0),
            Params = new Vector4((float)time, MathF.Pow(2, s.ExposureStops), (float)view.DebugView, (float)s.ToneMapper),
            Grid = new Vector4(view.GridHalfSize, MathF.Round(gridCentre.X), MathF.Round(gridCentre.Z), 0),
        };
    }

    void BeginRendering(CommandBuffer cmd, RenderView view, VkImageView color, VkAttachmentLoadOp colorLoad, Vector4 clear, VkAttachmentLoadOp depthLoad)
    {
        var colorAttachment = new VkRenderingAttachmentInfo
        {
            ImageView = color,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = colorLoad,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearValue { Color = new VkClearColorValue { Float32 = new Brutal.Numerics.float4(clear.X, clear.Y, clear.Z, clear.W) } },
        };
        var depthAttachment = new VkRenderingAttachmentInfo
        {
            ImageView = view.Depth.View,
            ImageLayout = VkImageLayout.DepthAttachmentOptimal,
            LoadOp = depthLoad,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 0f } }, // reversed depth
        };
        var extent = new VkExtent2D { Width = view.Width, Height = view.Height };
        cmd.BeginRendering(
            new VkRenderingInfo
            {
                RenderArea = new VkRect2D { Extent = extent },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment,
                DepthAttachment = &depthAttachment,
            }
        );
        cmd.SetViewport(0, [new VkViewport { Width = view.Width, Height = view.Height, MaxDepth = 1f }]);
        cmd.SetScissor(0, [new VkRect2D { Extent = extent }]);
    }

    public void Dispose()
    {
        var device = _gpu.Device;
        _gpu.WaitIdle();
        foreach (var p in _meshPipelines.Values.Concat(_shadowPipelines.Values).Concat(_blendPipelines.Values))
            device.DestroyPipeline(p, null);
        device.DestroyPipeline(_skyPipeline, null);
        device.DestroySampler(ShadowSampler, null);
        Timer.Dispose();
        Sky.Dispose();
        Materials.Dispose();
        foreach (var (tonemap, grid, lines, linesOnTop) in _outputPipelines.Values)
        {
            device.DestroyPipeline(tonemap, null);
            device.DestroyPipeline(grid, null);
            device.DestroyPipeline(lines, null);
            device.DestroyPipeline(linesOnTop, null);
        }
        device.DestroySampler(ClampSampler, null);
        device.DestroyPipelineLayout(_layout, null);
        device.DestroyDescriptorSetLayout(ViewSetLayout, null);
        device.DestroyDescriptorSetLayout(_boneSetLayout, null);
    }
}
