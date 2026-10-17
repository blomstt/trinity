using Brutal;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Trinity.Graphics;

/// <summary>
/// A 2D texture in GPU memory, ready to sample: image, view and sampler. Loads PNG, JPEG, TGA and
/// the other formats stb_image reads. Colour textures are sRGB, so shaders sample linear light.
/// </summary>
public sealed unsafe class Texture : IDisposable
{
    readonly GraphicsDevice _gpu;
    readonly VkDeviceMemory _memory;

    public readonly VkImage Image;
    public readonly VkImageView View;
    public readonly VkSampler Sampler;
    public readonly int Width, Height;

    /// <param name="srgb">True for colour (albedo, UI, logos); false for data such as normal maps.</param>
    public static Texture Load(GraphicsDevice gpu, string path, bool srgb = true)
    {
        using var image = Stb.Load(path, desiredChannels: 4);
        if (image.IsNull())
            throw new FileNotFoundException($"Couldn't load image {path}");
        return new Texture(gpu, image.Width, image.Height, image.AsSpan(), srgb);
    }

    /// <param name="rgba">Width * Height * 4 bytes, top row first.</param>
    public Texture(GraphicsDevice gpu, int width, int height, ReadOnlySpan<byte> rgba, bool srgb = true, bool smooth = true)
    {
        _gpu = gpu;
        Width = width;
        Height = height;
        var device = gpu.Device;
        var format = srgb ? VkFormat.R8G8B8A8SRGB : VkFormat.R8G8B8A8UNorm;

        Image = device.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            null
        );
        _memory = device.AllocateMemory(gpu.PhysicalDevice, Image, VkMemoryPropertyFlags.DeviceLocalBit, null);
        device.BindImageMemory(Image, _memory, (ByteSize64)0);

        // Upload through a staging buffer, leaving the image ready for shaders to read
        using var staging = new GpuBuffer(gpu, (long)width * height * 4, VkBufferUsageFlags.TransferSrcBit);
        staging.Write(rgba[..(width * height * 4)]);
        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
        var image = Image;
        gpu.Submit(cmd =>
        {
            var toCopy = new VkImageMemoryBarrier2
            {
                DstStageMask = VkPipelineStageFlags2.CopyBit,
                DstAccessMask = VkAccessFlags2.TransferWriteBit,
                OldLayout = VkImageLayout.Undefined,
                NewLayout = VkImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = image,
                SubresourceRange = range,
            };
            cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toCopy });
            cmd.CopyBufferToImage(
                staging.Buffer,
                image,
                VkImageLayout.TransferDstOptimal,
                [
                    new VkBufferImageCopy
                    {
                        ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                        ImageExtent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
                    },
                ]
            );
            var toRead = new VkImageMemoryBarrier2
            {
                SrcStageMask = VkPipelineStageFlags2.CopyBit,
                SrcAccessMask = VkAccessFlags2.TransferWriteBit,
                DstStageMask = VkPipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = VkAccessFlags2.ShaderSampledReadBit,
                OldLayout = VkImageLayout.TransferDstOptimal,
                NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = image,
                SubresourceRange = range,
            };
            cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toRead });
        });

        View = device.CreateImageView(
            new VkImageViewCreateInfo { Image = Image, ViewType = VkImageViewType._2D, Format = format, SubresourceRange = range },
            null
        );
        var filter = smooth ? VkFilter.Linear : VkFilter.Nearest;
        Sampler = device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = filter,
                MinFilter = filter,
                MipmapMode = VkSamplerMipmapMode.Linear,
                AddressModeU = VkSamplerAddressMode.ClampToEdge,
                AddressModeV = VkSamplerAddressMode.ClampToEdge,
                AddressModeW = VkSamplerAddressMode.ClampToEdge,
                MaxLod = 0,
            },
            null
        );
    }

    public void Dispose()
    {
        var device = _gpu.Device;
        device.DestroySampler(Sampler, null);
        device.DestroyImageView(View, null);
        device.DestroyImage(Image, null);
        device.FreeMemory(_memory, null);
    }
}

/// <summary>
/// A descriptor set with one texture at binding 0 for fragment shaders
/// (<c>layout(set = N, binding = 0) uniform sampler2D</c>).
/// </summary>
public sealed unsafe class TextureBinding : IDisposable
{
    readonly GraphicsDevice _gpu;
    readonly VkDescriptorPool _pool;

    public readonly VkDescriptorSetLayout SetLayout;
    public readonly VkDescriptorSet Set;

    public TextureBinding(GraphicsDevice gpu, Texture texture)
    {
        _gpu = gpu;
        var device = gpu.Device;
        var binding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.FragmentBit,
        };
        SetLayout = device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding }, null);
        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        _pool = device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &size }, null);
        var layout = SetLayout;
        VkDescriptorSet set = default;
        device.AllocateDescriptorSets(
            new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout },
            new ReadOnlySpan<VkDescriptorSet>(&set, 1)
        );
        Set = set;
        var info = new VkDescriptorImageInfo { Sampler = texture.Sampler, ImageView = texture.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        device.UpdateDescriptorSets(
            [new VkWriteDescriptorSet { DstSet = Set, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &info }],
            []
        );
    }

    public void Dispose()
    {
        _gpu.Device.DestroyDescriptorPool(_pool, null);
        _gpu.Device.DestroyDescriptorSetLayout(SetLayout, null);
    }
}
