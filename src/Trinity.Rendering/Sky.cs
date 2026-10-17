using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Trinity.Graphics;

namespace Trinity.Rendering;

[StructLayout(LayoutKind.Sequential)]
struct AtmosphereConstants
{
    public Vector4 SunDir; // towards the sun, illuminance
    public Vector4 Params; // haze, mie g, viewer altitude (km), multiple scattering
    public Vector4 Ground; // albedo
}

/// <summary>
/// The procedural sky and the light it gives: an atmosphere (single scattering, after Hillaire 2020)
/// computed on the GPU into a sky-view table, from which come the sky's spherical harmonics (diffuse
/// light) and a cubemap prefiltered by roughness (reflections). All of it is recomputed only when the
/// sun or sky settings change, and costs a fraction of a millisecond when they do. Bound as set 3.
/// </summary>
public sealed unsafe class Sky : IDisposable
{
    const int EnvSize = 128, EnvMips = 6, RadianceMips = 8;

    readonly GraphicsDevice _gpu;
    readonly GpuBuffer _constants, _sh;
    readonly GpuImage _transmittance, _skyView, _radiance, _environment;
    readonly VkSampler _linear;
    readonly VkDescriptorSetLayout _computeLayout;
    readonly VkPipelineLayout _computePipelineLayout;
    readonly VkPipeline _transmittancePass, _skyViewPass, _shPass, _cubePass, _prefilterPass;
    readonly VkDescriptorPool _pool;
    readonly VkDescriptorSet _tSet, _svSet, _shSet, _cubeSet;
    readonly VkDescriptorSet[] _prefilterSets = new VkDescriptorSet[EnvMips];

    public readonly VkDescriptorSetLayout SetLayout;
    public readonly VkDescriptorSet Set;

    AtmosphereConstants _last;
    bool _computed;

    /// <summary>Sunlight reaching the ground after the atmosphere, for the last settings computed (linear, HDR).</summary>
    public Vector3 SunColor { get; private set; }

    public Sky(GraphicsDevice gpu, ShaderLibrary shaders)
    {
        _gpu = gpu;
        var device = gpu.Device;
        _constants = new GpuBuffer(gpu, sizeof(AtmosphereConstants), VkBufferUsageFlags.UniformBufferBit);
        _sh = new GpuBuffer(gpu, 9 * 16, VkBufferUsageFlags.StorageBufferBit);
        var storage = VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit;
        _transmittance = new GpuImage(gpu, new GpuImageDesc { Format = VkFormat.R16G16B16A16SFloat, Width = 256, Height = 64, Usage = storage });
        _skyView = new GpuImage(gpu, new GpuImageDesc { Format = VkFormat.R16G16B16A16SFloat, Width = 256, Height = 128, Usage = storage });
        _radiance = new GpuImage(gpu, new GpuImageDesc
        {
            Format = VkFormat.R16G16B16A16SFloat, Width = EnvSize, Height = EnvSize, Layers = 6, Cube = true, MipLevels = RadianceMips,
            Usage = storage | VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit,
        });
        _environment = new GpuImage(gpu, new GpuImageDesc
        {
            Format = VkFormat.R16G16B16A16SFloat, Width = EnvSize, Height = EnvSize, Layers = 6, Cube = true, MipLevels = EnvMips, Usage = storage,
        });
        _linear = device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                MipmapMode = VkSamplerMipmapMode.Linear,
                AddressModeU = VkSamplerAddressMode.ClampToEdge,
                AddressModeV = VkSamplerAddressMode.ClampToEdge,
                AddressModeW = VkSamplerAddressMode.ClampToEdge,
                MaxLod = 1000,
            },
            null
        );

        // Compute: one layout for every pass; each pass's set fills the bindings it uses
        var c = VkShaderStageFlags.ComputeBit;
        Span<VkDescriptorSetLayoutBinding> computeBindings =
        [
            new() { Binding = 0, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = c },
            new() { Binding = 1, DescriptorType = VkDescriptorType.StorageImage, DescriptorCount = 1, StageFlags = c },
            new() { Binding = 2, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = c },
            new() { Binding = 3, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = c },
            new() { Binding = 4, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = c },
        ];
        fixed (VkDescriptorSetLayoutBinding* b = computeBindings)
            _computeLayout = device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = computeBindings.Length, Bindings = b }, null);
        var push = new VkPushConstantRange { StageFlags = c, Size = (ByteSize32)8 };
        _computePipelineLayout = device.CreatePipelineLayout([_computeLayout], [push], null);
        _transmittancePass = Pipelines.CreateCompute(gpu, shaders, "transmittance.comp", _computePipelineLayout);
        _skyViewPass = Pipelines.CreateCompute(gpu, shaders, "skyview.comp", _computePipelineLayout);
        _shPass = Pipelines.CreateCompute(gpu, shaders, "sky_sh.comp", _computePipelineLayout);
        _cubePass = Pipelines.CreateCompute(gpu, shaders, "env_cube.comp", _computePipelineLayout);
        _prefilterPass = Pipelines.CreateCompute(gpu, shaders, "env_prefilter.comp", _computePipelineLayout);

        // Rendering: the environment as shaders see it
        var all = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;
        Span<VkDescriptorSetLayoutBinding> renderBindings =
        [
            new() { Binding = 0, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = all },
            new() { Binding = 1, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = all },
            new() { Binding = 2, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = all },
        ];
        fixed (VkDescriptorSetLayoutBinding* b = renderBindings)
            SetLayout = device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = renderBindings.Length, Bindings = b }, null);

        var sets = 5 + EnvMips;
        Span<VkDescriptorPoolSize> sizes =
        [
            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = sets },
            new() { Type = VkDescriptorType.StorageImage, DescriptorCount = sets },
            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = sets * 3 },
            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = sets },
        ];
        fixed (VkDescriptorPoolSize* s = sizes)
            _pool = device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = sets, PoolSizeCount = sizes.Length, PoolSizes = s }, null);

        _tSet = Allocate(_computeLayout);
        _svSet = Allocate(_computeLayout);
        _shSet = Allocate(_computeLayout);
        _cubeSet = Allocate(_computeLayout);
        for (var m = 0; m < EnvMips; m++)
            _prefilterSets[m] = Allocate(_computeLayout);
        Set = Allocate(SetLayout);

        WriteBuffer(_tSet, 0, VkDescriptorType.UniformBuffer, _constants);
        WriteImage(_tSet, 1, VkDescriptorType.StorageImage, _transmittance.View, VkImageLayout.General);
        WriteBuffer(_svSet, 0, VkDescriptorType.UniformBuffer, _constants);
        WriteImage(_svSet, 1, VkDescriptorType.StorageImage, _skyView.View, VkImageLayout.General);
        WriteImage(_svSet, 2, VkDescriptorType.CombinedImageSampler, _transmittance.View, VkImageLayout.ShaderReadOnlyOptimal);
        WriteBuffer(_shSet, 0, VkDescriptorType.UniformBuffer, _constants);
        WriteImage(_shSet, 2, VkDescriptorType.CombinedImageSampler, _skyView.View, VkImageLayout.ShaderReadOnlyOptimal);
        WriteBuffer(_shSet, 3, VkDescriptorType.StorageBuffer, _sh);
        WriteBuffer(_cubeSet, 0, VkDescriptorType.UniformBuffer, _constants);
        WriteImage(_cubeSet, 1, VkDescriptorType.StorageImage, _radiance.ViewOf(0, 0, 6), VkImageLayout.General);
        WriteImage(_cubeSet, 2, VkDescriptorType.CombinedImageSampler, _skyView.View, VkImageLayout.ShaderReadOnlyOptimal);
        for (var m = 0; m < EnvMips; m++)
        {
            WriteImage(_prefilterSets[m], 1, VkDescriptorType.StorageImage, _environment.ViewOf(m, 0, 6), VkImageLayout.General);
            WriteImage(_prefilterSets[m], 4, VkDescriptorType.CombinedImageSampler, _radiance.View, VkImageLayout.ShaderReadOnlyOptimal);
        }
        WriteImage(Set, 0, VkDescriptorType.CombinedImageSampler, _environment.View, VkImageLayout.ShaderReadOnlyOptimal);
        WriteBuffer(Set, 1, VkDescriptorType.StorageBuffer, _sh);
        WriteImage(Set, 2, VkDescriptorType.CombinedImageSampler, _skyView.View, VkImageLayout.ShaderReadOnlyOptimal);
    }

    VkDescriptorSet Allocate(VkDescriptorSetLayout layout)
    {
        VkDescriptorSet set = default;
        _gpu.Device.AllocateDescriptorSets(
            new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout },
            new ReadOnlySpan<VkDescriptorSet>(&set, 1)
        );
        return set;
    }

    void WriteBuffer(VkDescriptorSet set, int binding, VkDescriptorType type, GpuBuffer buffer)
    {
        var info = new VkDescriptorBufferInfo { Buffer = buffer.Buffer, Range = (ByteSize64)(ulong)buffer.Size };
        _gpu.Device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = set, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, BufferInfo = &info }], []);
    }

    void WriteImage(VkDescriptorSet set, int binding, VkDescriptorType type, VkImageView view, VkImageLayout layout)
    {
        var info = new VkDescriptorImageInfo { Sampler = _linear, ImageView = view, ImageLayout = layout };
        _gpu.Device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = set, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, ImageInfo = &info }], []);
    }

    static void MemoryBarrier(CommandBuffer cmd, VkPipelineStageFlags2 src, VkAccessFlags2 srcAccess, VkPipelineStageFlags2 dst, VkAccessFlags2 dstAccess)
    {
        var barrier = new VkMemoryBarrier2 { SrcStageMask = src, SrcAccessMask = srcAccess, DstStageMask = dst, DstAccessMask = dstAccess };
        cmd.PipelineBarrier2(new VkDependencyInfo { MemoryBarrierCount = 1, MemoryBarriers = &barrier });
    }

    /// <summary>Recomputes the sky and its light if <paramref name="s"/> changed since last time. Record before any pass that reads them.</summary>
    public void Update(CommandBuffer cmd, RenderSettings s)
    {
        var toSun = -Vector3.Normalize(s.SunDirection);
        var constants = new AtmosphereConstants
        {
            SunDir = new Vector4(toSun, s.SunIlluminance),
            Params = new Vector4(s.Haze, s.MieAnisotropy, 0.2f, s.MultipleScattering),
            Ground = new Vector4(s.GroundAlbedo, 0),
        };
        if (_computed && constants.Equals(_last))
            return;
        _last = constants;
        _computed = true;
        _constants.AsSpan<AtmosphereConstants>()[0] = constants;
        SunColor = SunTransmittance(toSun, s.Haze, 0.2f) * s.SunIlluminance;

        // Readers of the previous results (earlier frames' shading) finish before these are rewritten
        MemoryBarrier(cmd, VkPipelineStageFlags2.FragmentShaderBit | VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.None,
            VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderStorageWriteBit);

        Dispatch(cmd, _transmittancePass, _tSet, _transmittance, 256, 64, 1);
        Dispatch(cmd, _skyViewPass, _svSet, _skyView, 256, 128, 1);

        cmd.BindPipeline(VkPipelineBindPoint.Compute, _shPass);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _computePipelineLayout, 0, [_shSet], []);
        cmd.Dispatch(1, 1, 1);

        // The radiance cubemap, then its mips by downsampling, for the prefilter to read
        Dispatch(cmd, _cubePass, _cubeSet, _radiance, EnvSize, EnvSize, 6, toShaderRead: false);
        _radiance.Transition(cmd, VkImageLayout.TransferDstOptimal);
        var size = EnvSize;
        for (var m = 1; m < RadianceMips; m++)
        {
            _radiance.TransitionMip(cmd, m - 1, VkImageLayout.TransferDstOptimal, VkImageLayout.TransferSrcOptimal);
            var blit = new VkImageBlit
            {
                SrcSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = m - 1, LayerCount = 6 },
                DstSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = m, LayerCount = 6 },
            };
            blit.SrcOffsets[1] = new VkOffset3D { X = size, Y = size, Z = 1 };
            blit.DstOffsets[1] = new VkOffset3D { X = size / 2, Y = size / 2, Z = 1 };
            cmd.BlitImage(_radiance.Image, VkImageLayout.TransferSrcOptimal, _radiance.Image, VkImageLayout.TransferDstOptimal, [blit], VkFilter.Linear);
            _radiance.TransitionMip(cmd, m - 1, VkImageLayout.TransferSrcOptimal, VkImageLayout.ShaderReadOnlyOptimal);
            size /= 2;
        }
        _radiance.TransitionMip(cmd, RadianceMips - 1, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);
        _radiance.SetLayout(VkImageLayout.ShaderReadOnlyOptimal);

        _environment.Transition(cmd, VkImageLayout.General, discard: true);
        cmd.BindPipeline(VkPipelineBindPoint.Compute, _prefilterPass);
        size = EnvSize;
        for (var m = 0; m < EnvMips; m++)
        {
            cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _computePipelineLayout, 0, [_prefilterSets[m]], []);
            Span<float> push = [m / (float)(EnvMips - 1), EnvSize];
            cmd.PushConstants(_computePipelineLayout, VkShaderStageFlags.ComputeBit, 0, MemoryMarshal.AsBytes(push));
            cmd.Dispatch((size + 7) / 8, (size + 7) / 8, 6);
            size /= 2;
        }
        _environment.Transition(cmd, VkImageLayout.ShaderReadOnlyOptimal);
        MemoryBarrier(cmd, VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderStorageWriteBit,
            VkPipelineStageFlags2.FragmentShaderBit | VkPipelineStageFlags2.VertexShaderBit, VkAccessFlags2.ShaderStorageReadBit);
    }

    void Dispatch(CommandBuffer cmd, VkPipeline pipeline, VkDescriptorSet set, GpuImage target, int w, int h, int layers, bool toShaderRead = true)
    {
        target.Transition(cmd, VkImageLayout.General, discard: true);
        cmd.BindPipeline(VkPipelineBindPoint.Compute, pipeline);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _computePipelineLayout, 0, [set], []);
        cmd.Dispatch((w + 7) / 8, (h + 7) / 8, layers);
        if (toShaderRead)
            target.Transition(cmd, VkImageLayout.ShaderReadOnlyOptimal);
    }

    /// <summary>The fraction of sunlight that crosses the atmosphere to the viewer, the same model as the shaders (CPU, one ray).</summary>
    public static Vector3 SunTransmittance(Vector3 toSun, float haze, float altitude)
    {
        const float bottom = 6360, top = 6460;
        var o = new Vector3(0, bottom + altitude, 0);
        if (RaySphere(o, toSun, bottom) > 0)
            return Vector3.Zero; // below the horizon
        var t = RaySphere(o, toSun, top);
        const int steps = 40;
        var depth = Vector3.Zero;
        var rayleigh = new Vector3(5.802f, 13.558f, 33.1f) * 1e-3f;
        var ozone = new Vector3(0.650f, 1.881f, 0.085f) * 1e-3f;
        for (var i = 0; i < steps; i++)
        {
            var h = (o + toSun * (t * (i + 0.5f) / steps)).Length() - bottom;
            var mie = MathF.Exp(-h / 1.2f) * haze * 4.40e-3f;
            depth += rayleigh * MathF.Exp(-h / 8) + new Vector3(mie) + ozone * MathF.Max(0, 1 - MathF.Abs(h - 25) / 15);
        }
        depth *= t / steps;
        return new Vector3(MathF.Exp(-depth.X), MathF.Exp(-depth.Y), MathF.Exp(-depth.Z));
    }

    static float RaySphere(Vector3 o, Vector3 d, float r)
    {
        var b = Vector3.Dot(o, d);
        var c = Vector3.Dot(o, o) - r * r;
        var disc = b * b - c;
        if (disc < 0)
            return -1;
        var s = MathF.Sqrt(disc);
        return -b - s > 0 ? -b - s : -b + s > 0 ? -b + s : -1;
    }

    public void Dispose()
    {
        var device = _gpu.Device;
        _gpu.WaitIdle();
        foreach (var p in new[] { _transmittancePass, _skyViewPass, _shPass, _cubePass, _prefilterPass })
            device.DestroyPipeline(p, null);
        device.DestroyPipelineLayout(_computePipelineLayout, null);
        device.DestroyDescriptorSetLayout(_computeLayout, null);
        device.DestroyDescriptorSetLayout(SetLayout, null);
        device.DestroyDescriptorPool(_pool, null);
        device.DestroySampler(_linear, null);
        _constants.Dispose();
        _sh.Dispose();
        _transmittance.Dispose();
        _skyView.Dispose();
        _radiance.Dispose();
        _environment.Dispose();
    }
}
