using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Trinity.Graphics;

/// <summary>
/// An off-screen colour + depth image that can be rendered into with the normal pipelines and then
/// sampled, for example an editor viewport shown inside the UI.
/// Render between <see cref="Begin"/> and <see cref="End"/>; after End it's ready to sample.
/// </summary>
public sealed unsafe class RenderTarget : IDisposable
{
    readonly GraphicsDevice _gpu;
    readonly VkDeviceMemory _colorMemory, _depthMemory;

    public readonly int Width, Height;
    public readonly VkImage Color, Depth;
    public readonly VkImageView ColorView, DepthView;
    public readonly VkSampler Sampler;

    public RenderTarget(GraphicsDevice gpu, int width, int height)
    {
        _gpu = gpu;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        (Color, _colorMemory, ColorView) = MakeImage(gpu.ColorFormat, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.ColorBit);
        (Depth, _depthMemory, DepthView) = MakeImage(GraphicsDevice.DepthFormat, VkImageUsageFlags.DepthStencilAttachmentBit, VkImageAspectFlags.DepthBit);
        Sampler = gpu.Device.CreateSampler(
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
    }

    (VkImage, VkDeviceMemory, VkImageView) MakeImage(VkFormat format, VkImageUsageFlags usage, VkImageAspectFlags aspect)
    {
        var device = _gpu.Device;
        var image = device.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = Width, Height = Height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = usage,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            null
        );
        var memory = device.AllocateMemory(_gpu.PhysicalDevice, image, VkMemoryPropertyFlags.DeviceLocalBit, null);
        device.BindImageMemory(image, memory, (ByteSize64)0);
        var view = device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2D,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = aspect, LevelCount = 1, LayerCount = 1 },
            },
            null
        );
        return (image, memory, view);
    }

    /// <summary>Starts rendering into the target, cleared, with viewport and scissor covering it.</summary>
    public void Begin(CommandBuffer cmd, VkClearColorValue clear)
    {
        Span<VkImageMemoryBarrier2> barriers =
        [
            new()
            {
                // The previous frame's UI may still be sampling it
                SrcStageMask = VkPipelineStageFlags2.FragmentShaderBit,
                SrcAccessMask = VkAccessFlags2.ShaderSampledReadBit,
                DstStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
                DstAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,
                OldLayout = VkImageLayout.Undefined,
                NewLayout = VkImageLayout.ColorAttachmentOptimal,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = Color,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            },
            new()
            {
                SrcStageMask = VkPipelineStageFlags2.LateFragmentTestsBit,
                SrcAccessMask = VkAccessFlags2.DepthStencilAttachmentWriteBit,
                DstStageMask = VkPipelineStageFlags2.EarlyFragmentTestsBit | VkPipelineStageFlags2.LateFragmentTestsBit,
                DstAccessMask = VkAccessFlags2.DepthStencilAttachmentWriteBit | VkAccessFlags2.DepthStencilAttachmentReadBit,
                OldLayout = VkImageLayout.Undefined,
                NewLayout = VkImageLayout.DepthAttachmentOptimal,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = Depth,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.DepthBit, LevelCount = 1, LayerCount = 1 },
            },
        ];
        fixed (VkImageMemoryBarrier2* b = barriers)
            cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 2, ImageMemoryBarriers = b });

        var color = new VkRenderingAttachmentInfo
        {
            ImageView = ColorView,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearValue { Color = clear },
        };
        var depth = new VkRenderingAttachmentInfo
        {
            ImageView = DepthView,
            ImageLayout = VkImageLayout.DepthAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.DontCare,
            ClearValue = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 0f } }, // reversed depth: 0 is far
        };
        var extent = new VkExtent2D { Width = Width, Height = Height };
        cmd.BeginRendering(
            new VkRenderingInfo
            {
                RenderArea = new VkRect2D { Extent = extent },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                ColorAttachments = &color,
                DepthAttachment = &depth,
            }
        );
        cmd.SetViewport(0, [new VkViewport { Width = Width, Height = Height, MaxDepth = 1f }]);
        cmd.SetScissor(0, [new VkRect2D { Extent = extent }]);
    }

    /// <summary>Ends rendering and makes the colour image readable by shaders.</summary>
    public void End(CommandBuffer cmd)
    {
        cmd.EndRendering();
        var toRead = new VkImageMemoryBarrier2
        {
            SrcStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
            SrcAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,
            DstStageMask = VkPipelineStageFlags2.FragmentShaderBit,
            DstAccessMask = VkAccessFlags2.ShaderSampledReadBit,
            OldLayout = VkImageLayout.ColorAttachmentOptimal,
            NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = Color,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        };
        cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toRead });
    }

    public void Dispose()
    {
        var device = _gpu.Device;
        device.DestroySampler(Sampler, null);
        device.DestroyImageView(ColorView, null);
        device.DestroyImageView(DepthView, null);
        device.DestroyImage(Color, null);
        device.DestroyImage(Depth, null);
        device.FreeMemory(_colorMemory, null);
        device.FreeMemory(_depthMemory, null);
    }
}
