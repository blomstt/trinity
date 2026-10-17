using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Trinity.Graphics;

namespace Trinity.Rendering;

/// <summary>Debug visualisations a view can show instead of the lit image.</summary>
public enum DebugView
{
    None,
    Albedo,
    Normals,
    Cascades,
}

/// <summary>
/// One place the renderer draws to: a game window or an editor viewport. Owns that view's HDR colour
/// and depth targets and its per-frame data, so several views can render in the same frame.
/// </summary>
public sealed unsafe class RenderView : IDisposable
{
    public const VkFormat HdrFormat = VkFormat.R16G16B16A16SFloat;

    readonly GraphicsDevice _gpu;
    readonly Renderer _renderer;
    readonly VkDescriptorPool _pool;
    internal readonly VkDescriptorSet[] Sets = new VkDescriptorSet[GraphicsDevice.FramesInFlight];
    internal readonly GpuBuffer[] FrameData = new GpuBuffer[GraphicsDevice.FramesInFlight];
    internal readonly GpuBuffer?[] InstanceData = new GpuBuffer?[GraphicsDevice.FramesInFlight];
    internal readonly GpuBuffer?[] DebugLines = new GpuBuffer?[GraphicsDevice.FramesInFlight];
    internal readonly GpuBuffer?[] VisibleData = new GpuBuffer?[GraphicsDevice.FramesInFlight];

    public int Width { get; private set; }
    public int Height { get; private set; }
    public GpuImage Hdr { get; private set; } = null!;
    public GpuImage Depth { get; private set; } = null!;

    /// <summary>The sun's shadow maps: four cascades as layers of one depth image.</summary>
    public readonly GpuImage Shadow;

    public DebugView DebugView = DebugView.None;

    /// <summary>The editor's ground grid, drawn over the image; zero half-size for none.</summary>
    public float GridHalfSize;

    internal RenderView(GraphicsDevice gpu, Renderer renderer, int width, int height)
    {
        _gpu = gpu;
        _renderer = renderer;
        Span<VkDescriptorPoolSize> sizes =
        [
            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = GraphicsDevice.FramesInFlight },
            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = GraphicsDevice.FramesInFlight * 2 },
            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = GraphicsDevice.FramesInFlight * Renderer.ViewSamplers },
        ];
        fixed (VkDescriptorPoolSize* s = sizes)
            _pool = gpu.Device.CreateDescriptorPool(
                new VkDescriptorPoolCreateInfo { MaxSets = GraphicsDevice.FramesInFlight, PoolSizeCount = sizes.Length, PoolSizes = s },
                null
            );
        for (var f = 0; f < GraphicsDevice.FramesInFlight; f++)
        {
            var layout = renderer.ViewSetLayout;
            VkDescriptorSet set = default;
            gpu.Device.AllocateDescriptorSets(
                new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout },
                new ReadOnlySpan<VkDescriptorSet>(&set, 1)
            );
            Sets[f] = set;
            FrameData[f] = new GpuBuffer(gpu, sizeof(FrameConstants), VkBufferUsageFlags.UniformBufferBit);
            WriteBuffer(f, 0, VkDescriptorType.UniformBuffer, FrameData[f]);
        }
        Shadow = new GpuImage(gpu, new GpuImageDesc
        {
            Format = GraphicsDevice.DepthFormat,
            Width = Renderer.ShadowResolution,
            Height = Renderer.ShadowResolution,
            Layers = 4,
            Usage = VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit,
        });
        for (var f = 0; f < GraphicsDevice.FramesInFlight; f++)
            WriteImage(f, 3, Shadow.View, renderer.ShadowSampler, VkImageLayout.ShaderReadOnlyOptimal);
        Resize(width, height);
        for (var f = 0; f < GraphicsDevice.FramesInFlight; f++)
        {
            EnsureInstances(f, 256);
            EnsureVisible(f, 1024);
        }
    }

    void WriteBuffer(int frame, int binding, VkDescriptorType type, GpuBuffer buffer)
    {
        var info = new VkDescriptorBufferInfo { Buffer = buffer.Buffer, Range = (ByteSize64)(ulong)buffer.Size };
        _gpu.Device.UpdateDescriptorSets(
            [new VkWriteDescriptorSet { DstSet = Sets[frame], DstBinding = binding, DescriptorCount = 1, DescriptorType = type, BufferInfo = &info }],
            []
        );
    }

    internal void WriteImage(int frame, int binding, VkImageView view, VkSampler sampler, VkImageLayout layout)
    {
        var info = new VkDescriptorImageInfo { Sampler = sampler, ImageView = view, ImageLayout = layout };
        _gpu.Device.UpdateDescriptorSets(
            [new VkWriteDescriptorSet { DstSet = Sets[frame], DstBinding = binding, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &info }],
            []
        );
    }

    /// <summary>Grows this frame's instance buffer to hold <paramref name="count"/> instances. Safe once the frame's fence has been waited on.</summary>
    internal GpuBuffer EnsureInstances(int frame, int count)
    {
        if (InstanceData[frame] is { } existing && existing.Size >= (long)count * sizeof(InstanceConstants))
            return existing;
        InstanceData[frame]?.Dispose();
        var capacity = Math.Max(count, (int)((InstanceData[frame]?.Size ?? 0) / sizeof(InstanceConstants) * 2));
        var buffer = new GpuBuffer(_gpu, (long)capacity * sizeof(InstanceConstants), VkBufferUsageFlags.StorageBufferBit);
        InstanceData[frame] = buffer;
        WriteBuffer(frame, 1, VkDescriptorType.StorageBuffer, buffer);
        return buffer;
    }

    /// <summary>Grows this frame's visible-instance list to hold <paramref name="count"/> indices.</summary>
    internal GpuBuffer EnsureVisible(int frame, int count)
    {
        if (VisibleData[frame] is { } existing && existing.Size >= count * 4L)
            return existing;
        var capacity = Math.Max(count, (int)((VisibleData[frame]?.Size ?? 0) / 4 * 2));
        VisibleData[frame]?.Dispose();
        var buffer = new GpuBuffer(_gpu, capacity * 4L, VkBufferUsageFlags.StorageBufferBit);
        VisibleData[frame] = buffer;
        WriteBuffer(frame, 4, VkDescriptorType.StorageBuffer, buffer);
        return buffer;
    }

    /// <summary>Makes the targets match the output size. Returns true if they were remade.</summary>
    public bool Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (Hdr != null && width == Width && height == Height)
            return false;
        if (Hdr != null)
        {
            // Both frames' descriptor sets point at the old targets
            _gpu.WaitIdle();
            Hdr.Dispose();
            Depth.Dispose();
        }
        Width = width;
        Height = height;
        Hdr = new GpuImage(_gpu, new GpuImageDesc
        {
            Format = HdrFormat,
            Width = width,
            Height = height,
            Usage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit,
        });
        Depth = new GpuImage(_gpu, new GpuImageDesc
        {
            Format = GraphicsDevice.DepthFormat,
            Width = width,
            Height = height,
            Usage = VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit,
        });
        for (var f = 0; f < GraphicsDevice.FramesInFlight; f++)
            WriteImage(f, 2, Hdr.View, _renderer.ClampSampler, VkImageLayout.ShaderReadOnlyOptimal);
        return true;
    }

    public void Dispose()
    {
        _gpu.WaitIdle();
        Hdr.Dispose();
        Depth.Dispose();
        Shadow.Dispose();
        foreach (var b in FrameData)
            b.Dispose();
        foreach (var b in InstanceData)
            b?.Dispose();
        foreach (var b in DebugLines)
            b?.Dispose();
        foreach (var b in VisibleData)
            b?.Dispose();
        _gpu.Device.DestroyDescriptorPool(_pool, null);
    }
}
