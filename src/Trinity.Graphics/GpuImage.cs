using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Trinity.Graphics;

/// <summary>What a <see cref="GpuImage"/> is made as.</summary>
public sealed record GpuImageDesc
{
    public required VkFormat Format { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int MipLevels { get; init; } = 1;
    /// <summary>Array layers; 6 per cube for <see cref="Cube"/> images.</summary>
    public int Layers { get; init; } = 1;
    public bool Cube { get; init; }
    public required VkImageUsageFlags Usage { get; init; }
}

/// <summary>
/// A GPU image with its memory and a view of the whole thing, which remembers its layout so
/// <see cref="Transition"/> only needs to be told where it's going. Every image the renderer uses is
/// one: render targets, depth buffers, shadow maps, lookup tables, cubemaps and textures.
/// Commands are recorded in submission order on one queue, which is what the tracking assumes.
/// </summary>
public sealed unsafe class GpuImage : IDisposable
{
    readonly GraphicsDevice _gpu;
    readonly VkDeviceMemory _memory;
    readonly Dictionary<(int Mip, int Layer, int Layers, VkImageViewType Type), VkImageView> _views = [];
    public readonly GpuImageDesc Desc;
    public readonly VkImage Image;
    public readonly VkImageAspectFlags Aspect;

    /// <summary>All mips and layers, as a 2D, 2D array or cube view.</summary>
    public readonly VkImageView View;

    public VkImageLayout Layout { get; private set; } = VkImageLayout.Undefined;
    public int Width => Desc.Width;
    public int Height => Desc.Height;
    public VkFormat Format => Desc.Format;

    public GpuImage(GraphicsDevice gpu, GpuImageDesc desc)
    {
        _gpu = gpu;
        Desc = desc;
        Aspect = IsDepth(desc.Format) ? VkImageAspectFlags.DepthBit : VkImageAspectFlags.ColorBit;
        var device = gpu.Device;
        Image = device.CreateImage(
            new VkImageCreateInfo
            {
                Flags = desc.Cube ? VkImageCreateFlags.CubeCompatibleBit : 0,
                ImageType = VkImageType._2D,
                Format = desc.Format,
                Extent = new VkExtent3D { Width = Math.Max(1, desc.Width), Height = Math.Max(1, desc.Height), Depth = 1 },
                MipLevels = desc.MipLevels,
                ArrayLayers = desc.Layers,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = desc.Usage,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            null
        );
        _memory = device.AllocateMemory(gpu.PhysicalDevice, Image, VkMemoryPropertyFlags.DeviceLocalBit, null);
        device.BindImageMemory(Image, _memory, (ByteSize64)0);
        var type = desc.Cube ? VkImageViewType.Cube : desc.Layers > 1 ? VkImageViewType._2DArray : VkImageViewType._2D;
        View = CreateView(0, desc.MipLevels, 0, desc.Layers, type);
    }

    public static bool IsDepth(VkFormat f) => f is VkFormat.D32SFloat or VkFormat.D16UNorm or VkFormat.D24UNormS8UInt or VkFormat.D32SFloatS8UInt;

    VkImageView CreateView(int mip, int mips, int layer, int layers, VkImageViewType type) =>
        _gpu.Device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = Image,
                ViewType = type,
                Format = Desc.Format,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = Aspect,
                    BaseMipLevel = mip,
                    LevelCount = mips,
                    BaseArrayLayer = layer,
                    LayerCount = layers,
                },
            },
            null
        );

    /// <summary>A view of one mip of one layer (or of <paramref name="layers"/> layers as an array), made once and kept.</summary>
    public VkImageView ViewOf(int mip = 0, int layer = 0, int layers = 1)
    {
        var type = layers > 1 ? VkImageViewType._2DArray : VkImageViewType._2D;
        if (!_views.TryGetValue((mip, layer, layers, type), out var view))
            _views[(mip, layer, layers, type)] = view = CreateView(mip, 1, layer, layers, type);
        return view;
    }

    /// <summary>Where an image in a layout is used, for barriers.</summary>
    static (VkPipelineStageFlags2, VkAccessFlags2) Usage(VkImageLayout layout) =>
        layout switch
        {
            VkImageLayout.ColorAttachmentOptimal => (VkPipelineStageFlags2.ColorAttachmentOutputBit, VkAccessFlags2.ColorAttachmentWriteBit | VkAccessFlags2.ColorAttachmentReadBit),
            VkImageLayout.DepthAttachmentOptimal => (VkPipelineStageFlags2.EarlyFragmentTestsBit | VkPipelineStageFlags2.LateFragmentTestsBit, VkAccessFlags2.DepthStencilAttachmentWriteBit | VkAccessFlags2.DepthStencilAttachmentReadBit),
            VkImageLayout.DepthReadOnlyOptimal => (VkPipelineStageFlags2.EarlyFragmentTestsBit | VkPipelineStageFlags2.LateFragmentTestsBit | VkPipelineStageFlags2.FragmentShaderBit, VkAccessFlags2.DepthStencilAttachmentReadBit | VkAccessFlags2.ShaderSampledReadBit),
            VkImageLayout.ShaderReadOnlyOptimal => (VkPipelineStageFlags2.FragmentShaderBit | VkPipelineStageFlags2.ComputeShaderBit | VkPipelineStageFlags2.VertexShaderBit, VkAccessFlags2.ShaderSampledReadBit),
            VkImageLayout.General => (VkPipelineStageFlags2.ComputeShaderBit | VkPipelineStageFlags2.FragmentShaderBit, VkAccessFlags2.ShaderStorageReadBit | VkAccessFlags2.ShaderStorageWriteBit | VkAccessFlags2.ShaderSampledReadBit),
            VkImageLayout.TransferDstOptimal => (VkPipelineStageFlags2.CopyBit | VkPipelineStageFlags2.BlitBit, VkAccessFlags2.TransferWriteBit),
            VkImageLayout.TransferSrcOptimal => (VkPipelineStageFlags2.CopyBit | VkPipelineStageFlags2.BlitBit, VkAccessFlags2.TransferReadBit),
            _ => (VkPipelineStageFlags2.TopOfPipeBit, VkAccessFlags2.None),
        };

    /// <summary>
    /// Moves the whole image to <paramref name="layout"/>, waiting for its last use. With
    /// <paramref name="discard"/>, the old contents don't matter (cheaper, for render targets about to be cleared).
    /// A barrier is recorded even when the layout doesn't change, so writes become visible to the next reader.
    /// </summary>
    public void Transition(CommandBuffer cmd, VkImageLayout layout, bool discard = false)
    {
        var (srcStage, srcAccess) = Usage(Layout);
        var (dstStage, dstAccess) = Usage(layout);
        var barrier = new VkImageMemoryBarrier2
        {
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess & WriteAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = discard ? VkImageLayout.Undefined : Layout,
            NewLayout = layout,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = Image,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = Aspect, LevelCount = Desc.MipLevels, LayerCount = Desc.Layers },
        };
        cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &barrier });
        Layout = layout;
    }

    const VkAccessFlags2 WriteAccess =
        VkAccessFlags2.ColorAttachmentWriteBit | VkAccessFlags2.DepthStencilAttachmentWriteBit | VkAccessFlags2.ShaderStorageWriteBit | VkAccessFlags2.TransferWriteBit;

    /// <summary>Records a barrier for part of the image (one mip), for building mip chains; doesn't change <see cref="Layout"/>.</summary>
    public void TransitionMip(CommandBuffer cmd, int mip, VkImageLayout from, VkImageLayout to, int layer = 0, int layers = -1)
    {
        var (srcStage, srcAccess) = Usage(from);
        var (dstStage, dstAccess) = Usage(to);
        var barrier = new VkImageMemoryBarrier2
        {
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess & WriteAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = Image,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = Aspect,
                BaseMipLevel = mip,
                LevelCount = 1,
                BaseArrayLayer = layer,
                LayerCount = layers < 0 ? Desc.Layers : layers,
            },
        };
        cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &barrier });
    }

    /// <summary>Tells the tracker the image is now in <paramref name="layout"/> (after per-mip work done by hand).</summary>
    public void SetLayout(VkImageLayout layout) => Layout = layout;

    /// <summary>Destroys the image once the GPU can no longer be using it.</summary>
    public void Dispose()
    {
        var device = _gpu.Device;
        var views = _views.Values.Append(View).ToArray();
        var image = Image;
        var memory = _memory;
        _gpu.Release(() =>
        {
            foreach (var v in views)
                device.DestroyImageView(v, null);
            device.DestroyImage(image, null);
            device.FreeMemory(memory, null);
        });
    }
}
