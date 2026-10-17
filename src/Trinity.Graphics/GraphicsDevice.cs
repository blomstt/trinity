using Brutal;
using Brutal.GlfwApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Trinity.Graphics;

/// <summary>What this GPU supports beyond Vulkan 1.3's baseline, for systems that adapt to it.</summary>
/// <param name="Bindless">Large, partially bound texture arrays indexed per pixel (descriptor indexing).</param>
/// <param name="MaxTextures">Textures one array can hold: thousands when bindless, a small fixed number when not.</param>
/// <param name="TimestampPeriod">Nanoseconds per GPU timestamp tick; 0 if timestamps aren't supported.</param>
public sealed record GpuFeatures(bool Bindless, int MaxTextures, float MaxAnisotropy, bool DepthClamp, float TimestampPeriod);

/// <summary>
/// Everything needed to put pictures on the screen: instance, device, swapchain, a shared depth
/// buffer, and the per-frame command buffers and sync objects. Uses Vulkan 1.3 dynamic rendering,
/// so there are no render passes or framebuffers. Depth is reversed: 1 at the near plane, 0 at the far
/// one, cleared to 0 and tested with greater-or-equal, which keeps precision over long distances.
/// </summary>
public sealed unsafe class GraphicsDevice : IDisposable
{
    public const int FramesInFlight = 2;
    public const VkFormat DepthFormat = VkFormat.D32SFloat;

    public readonly Instance Instance;
    public readonly PhysicalDevice PhysicalDevice;
    public readonly DeviceEx Device;
    public readonly Queue Queue;
    public readonly int QueueFamily;
    public readonly string GpuName;
    public readonly GpuFeatures Features;

    /// <summary>Compiled pipelines, kept on disk between runs so start-up doesn't recompile them.</summary>
    public readonly VkPipelineCache PipelineCache;
    readonly string _pipelineCachePath;

    public VkFormat ColorFormat { get; private set; }
    public VkExtent2D Extent { get; private set; }
    public bool VSync { get; set; }
    public int FrameIndex { get; private set; }

    /// <summary>Counts frames begun, for doing once-per-frame work only once.</summary>
    public long FrameNumber { get; private set; }

    /// <summary>The swapchain images' views (one in off-screen mode), for tools that render their own passes.</summary>
    public IReadOnlyList<VkImageView> ImageViews => _views;

    /// <summary>Which of <see cref="ImageViews"/> this frame draws into (valid after <see cref="BeginFrame"/>).</summary>
    public int ImageIndex => _imageIndex;

    /// <summary>This frame's image, in ColorAttachmentOptimal after <see cref="BeginFrame"/>.</summary>
    public VkImage CurrentImage => _images[_imageIndex];

    public VkImageView CurrentView => _views[_imageIndex];

    public int MinImageCount { get; private set; } = 2;

    /// <summary>Raised after the swapchain is rebuilt (resize, vsync change); framebuffers made from <see cref="ImageViews"/> must be remade.</summary>
    public event Action? SwapchainRecreated;

    readonly Window _window;
    readonly VkSurfaceKHR _surface;
    readonly VkCommandPool _commandPool;
    readonly CommandBuffer[] _commandBuffers = new CommandBuffer[FramesInFlight];
    readonly VkSemaphore[] _imageAvailable = new VkSemaphore[FramesInFlight];
    readonly VkFence[] _inFlight = new VkFence[FramesInFlight];

    VkSwapchainKHR _swapchain;
    VkImage[] _images = [];
    VkImageView[] _views = [];
    VkSemaphore[] _renderFinished = []; // one per swapchain image, as the present engine holds it
    VkImage _depthImage;
    VkDeviceMemory _depthMemory;
    VkImageView _depthView;
    int _imageIndex;
    bool _rendering;
    readonly List<Action>[] _releases = [[], []];
    bool _swapchainDirty;
    bool _presentModeVSync;

    // Off-screen mode (video recording): render into our own image at a fixed size, never present
    readonly bool _offscreen;
    VkDeviceMemory _offscreenMemory;

    public GraphicsDevice(Window window, bool vsync, VkExtent2D? offscreen = null)
    {
        _window = window;
        VSync = vsync;
        _offscreen = offscreen != null;

        // Instance, with whatever GLFW needs for a surface on this platform (Wayland, X11 or Metal)
        var extensions = new HashSet<string>();
        var glfwExtensions = Glfw.GetRequiredInstanceExtensions(out var count);
        if (count == 0)
            throw new Exception("GLFW found no Vulkan loader (libvulkan) or no surface support for this platform");
        for (var i = 0; i < count; i++)
            extensions.Add(new string((sbyte*)glfwExtensions[i]));
        // On macOS, Vulkan runs on Metal through a portability driver (MoltenVK), which the loader only lists when asked
        VulkanHelpers.AddPlatformRequiredExtensions(extensions);
        var layers = new HashSet<string>();
        if (Environment.GetEnvironmentVariable("TRINITY_VALIDATION") == "1" && VulkanHelpers.Validation.IsSupported)
            VulkanHelpers.AddValidationLayers(layers);

        Instance = Vulkan.CreateInstance(
            new Instance.CreateInfo
            {
                AppInfo = new VkApplicationInfo { ApiVersion = (1 << 22) | (3 << 12) }, // Vulkan 1.3
                Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
                EnabledExtensions = extensions,
                EnabledLayers = layers,
            }
        );

        if (Glfw.CreateWindowSurface(Instance.VkHandle, window.Native, out var surface) != 0)
            throw new Exception("Couldn't create a Vulkan surface for the window");
        _surface = new VkSurfaceKHR((nint)surface);

        // Prefer a discrete GPU that can present to this window
        (PhysicalDevice, QueueFamily) = PickGpu();
        var props = PhysicalDevice.GetProperties();
        GpuName = new string((sbyte*)props.DeviceName);

        // Optional features: bindless texture arrays, anisotropic filtering, depth clamp (for shadows)
        var supported13 = new VkPhysicalDeviceVulkan13Features();
        var supported12 = new VkPhysicalDeviceVulkan12Features { Next = &supported13 };
        var supported = new VkPhysicalDeviceFeatures2 { Next = &supported12 };
        PhysicalDevice.GetFeatures2(ref supported);
        // Bindless arrays are update-after-bind, so they count against its limits, which can be far higher
        // (MoltenVK: 16 samplers per stage without, 1024 with)
        var bindless = supported12.DescriptorIndexing && supported12.RuntimeDescriptorArray && supported12.DescriptorBindingPartiallyBound
            && supported12.ShaderSampledImageArrayNonUniformIndexing && supported12.DescriptorBindingVariableDescriptorCount
            && supported12.DescriptorBindingSampledImageUpdateAfterBind
            && Environment.GetEnvironmentVariable("TRINITY_NO_BINDLESS") != "1";
        var limits = props.Limits;
        var limits12 = new VkPhysicalDeviceVulkan12Properties();
        var props2 = new VkPhysicalDeviceProperties2 { Next = &limits12 };
        PhysicalDevice.GetProperties2(ref props2);
        // A combined image sampler counts as a sampler, a sampled image and a resource at once, and the
        // frame's own few samplers (shadow map, HDR, sky tables) share the same per-stage limits
        const int otherSamplers = 8;
        var maxTextures = bindless
            ? Math.Min(16384, Math.Min(limits12.MaxPerStageDescriptorUpdateAfterBindSampledImages,
                Math.Min(limits12.MaxPerStageDescriptorUpdateAfterBindSamplers, limits12.MaxPerStageUpdateAfterBindResources)) - otherSamplers)
            : Math.Min(64, Math.Min(limits.MaxPerStageDescriptorSampledImages,
                Math.Min(limits.MaxPerStageDescriptorSamplers, limits.MaxPerStageResources)) - otherSamplers);
        Features = new GpuFeatures(
            bindless,
            maxTextures,
            supported.Features.SamplerAnisotropy ? limits.MaxSamplerAnisotropy : 1,
            supported.Features.DepthClamp,
            limits.TimestampComputeAndGraphics ? limits.TimestampPeriod : 0
        );

        var priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo
        {
            QueueFamilyIndex = QueueFamily,
            QueueCount = 1,
            QueuePriorities = &priority,
        };
        var vulkan13 = new VkPhysicalDeviceVulkan13Features
        {
            DynamicRendering = true,
            Synchronization2 = true,
            // Shaders compiled for Vulkan 1.3 turn `discard` into demote-to-helper
            ShaderDemoteToHelperInvocation = supported13.ShaderDemoteToHelperInvocation,
        };
        var vulkan12 = new VkPhysicalDeviceVulkan12Features
        {
            Next = &vulkan13,
            DescriptorIndexing = bindless,
            RuntimeDescriptorArray = bindless,
            DescriptorBindingPartiallyBound = bindless,
            ShaderSampledImageArrayNonUniformIndexing = bindless,
            DescriptorBindingVariableDescriptorCount = bindless,
            DescriptorBindingSampledImageUpdateAfterBind = bindless,
            HostQueryReset = supported12.HostQueryReset,
        };
        var core = new VkPhysicalDeviceFeatures
        {
            SamplerAnisotropy = supported.Features.SamplerAnisotropy,
            DepthClamp = supported.Features.DepthClamp,
        };
        // A portability driver must have VK_KHR_portability_subset enabled; elsewhere this adds nothing
        var deviceExtensions = new HashSet<string> { "VK_KHR_swapchain" };
        VulkanHelpers.AddMandatoryIfSupportedDeviceExtensions(deviceExtensions, PhysicalDevice);
        Device = PhysicalDevice.CreateDevice(
            new DeviceEx.CreateInfo
            {
                Extensions = [.. deviceExtensions],
                QueueCreateInfos = new ReadOnlySpan<VkDeviceQueueCreateInfo>(&queueInfo, 1),
                Features = &core,
                Next = &vulkan12,
            }
        );
        (PipelineCache, _pipelineCachePath) = LoadPipelineCache(props);
        Queue = Device.GetQueue(QueueFamily, 0);

        _commandPool = Device.CreateCommandPool(
            new VkCommandPoolCreateInfo
            {
                QueueFamilyIndex = QueueFamily,
                Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit,
            },
            null
        );
        for (var i = 0; i < FramesInFlight; i++)
        {
            _commandBuffers[i] = Device.AllocateCommandBuffer(
                new VkCommandBufferAllocateInfo { CommandPool = _commandPool, Level = VkCommandBufferLevel.Primary }
            );
            _imageAvailable[i] = Device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
            _inFlight[i] = Device.CreateFence(VkFenceCreateFlags.SignaledBit, null);
        }

        if (offscreen is { } size)
            CreateOffscreenTarget(size);
        else
            CreateSwapchain();
    }

    /// <summary>The pipeline cache from the last run on this GPU and driver, if any.</summary>
    (VkPipelineCache, string) LoadPipelineCache(in VkPhysicalDeviceProperties props)
    {
        var cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        var id = props.VendorID.ToString("x") + "-" + props.DeviceID.ToString("x") + "-" + props.DriverVersion.ToString("x");
        var path = Path.Combine(cacheHome, "trinity", $"pipelines-{id}.bin");
        // The driver checks the header (GPU and driver UUID) and ignores data that doesn't match
        var data = File.Exists(path) ? File.ReadAllBytes(path) : [];
        fixed (byte* d = data)
            return (Device.CreatePipelineCache(new VkPipelineCacheCreateInfo { InitialDataSize = (nuint)data.Length, InitialData = d }, null), path);
    }

    void SavePipelineCache()
    {
        try
        {
            var size = Device.GetPipelineCacheData_Count(PipelineCache);
            var data = new byte[(int)size];
            Device.GetPipelineCacheData_Write(PipelineCache, data);
            Directory.CreateDirectory(Path.GetDirectoryName(_pipelineCachePath)!);
            File.WriteAllBytes(_pipelineCachePath, data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache is only a speed-up
        }
    }

    /// <summary>
    /// Runs <paramref name="destroy"/> once the GPU has finished every frame that might use the
    /// resource it frees: <see cref="FramesInFlight"/> frames from now, or at shutdown.
    /// </summary>
    public void Release(Action destroy) => _releases[FrameIndex].Add(destroy);

    void RunReleases(int slot)
    {
        var list = _releases[slot];
        if (list.Count == 0)
            return;
        var actions = list.ToArray();
        list.Clear();
        foreach (var a in actions)
            a();
    }

    void CreateOffscreenTarget(VkExtent2D size)
    {
        Extent = size;
        ColorFormat = VkFormat.B8G8R8A8SRGB;
        var image = Device.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = ColorFormat,
                Extent = new VkExtent3D { Width = size.Width, Height = size.Height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            null
        );
        _offscreenMemory = Device.AllocateMemory(PhysicalDevice, image, VkMemoryPropertyFlags.DeviceLocalBit, null);
        Device.BindImageMemory(image, _offscreenMemory, (ByteSize64)0);
        _images = [image];
        _views = [CreateView(image, ColorFormat, VkImageAspectFlags.ColorBit)];
        _renderFinished = [];
        CreateDepth();
    }

    (PhysicalDevice, int) PickGpu()
    {
        PhysicalDevice? best = null;
        var bestFamily = -1;
        foreach (var gpu in Instance.PhysicalDevices)
        {
            var familyCount = gpu.GetQueueFamilyProperties_Count();
            var families = new VkQueueFamilyProperties[familyCount]; // once per GPU at start-up
            gpu.GetQueueFamilyProperties_Write(families);
            for (var f = 0; f < familyCount; f++)
            {
                if ((families[f].QueueFlags & VkQueueFlags.GraphicsBit) == 0 || !gpu.GetSurfaceSupportKHR(f, _surface))
                    continue;
                var discrete = gpu.GetProperties().DeviceType == VkPhysicalDeviceType.DiscreteGpu;
                if (best == null || discrete)
                {
                    best = gpu;
                    bestFamily = f;
                }
                break;
            }
            if (best == gpu && gpu.GetProperties().DeviceType == VkPhysicalDeviceType.DiscreteGpu)
                break;
        }
        return best == null ? throw new Exception("No GPU can draw to this window") : (best, bestFamily);
    }

    void CreateSwapchain()
    {
        var caps = new VkSurfaceCapabilitiesKHR();
        PhysicalDevice.GetSurfaceCapabilitiesKHR(_surface, ref caps);

        var formatCount = PhysicalDevice.GetSurfaceFormatsKHR_Count(_surface);
        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[formatCount];
        PhysicalDevice.GetSurfaceFormatsKHR_Write(_surface, formats);
        var chosen = formats[0];
        foreach (var f in formats)
            if (f.Format is VkFormat.B8G8R8A8SRGB or VkFormat.R8G8B8A8SRGB && f.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR)
            {
                chosen = f;
                break;
            }
        ColorFormat = chosen.Format;

        // Mailbox shows the real frame rate without tearing; FIFO caps it to the monitor
        var modeCount = PhysicalDevice.GetSurfacePresentModesKHR_Count(_surface);
        Span<VkPresentModeKHR> modes = stackalloc VkPresentModeKHR[modeCount];
        PhysicalDevice.GetSurfacePresentModesKHR_Write(_surface, modes);
        var presentMode = VkPresentModeKHR.FifoKHR;
        if (!VSync)
            foreach (var m in modes)
                if (m == VkPresentModeKHR.MailboxKHR || (m == VkPresentModeKHR.ImmediateKHR && presentMode != VkPresentModeKHR.MailboxKHR))
                    presentMode = m;
        _presentModeVSync = VSync;

        var fb = _window.FramebufferSize;
        Extent = caps.CurrentExtent.Width != -1
            ? caps.CurrentExtent
            : new VkExtent2D
            {
                Width = Math.Clamp(fb.X, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Height = Math.Clamp(fb.Y, caps.MinImageExtent.Height, caps.MaxImageExtent.Height),
            };

        MinImageCount = Math.Max(2, caps.MinImageCount);
        var imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0)
            imageCount = Math.Min(imageCount, caps.MaxImageCount);

        var old = _swapchain;
        _swapchain = Device.CreateSwapchainKHR(
            new VkSwapchainCreateInfoKHR
            {
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = chosen.Format,
                ImageColorSpace = chosen.ColorSpace,
                ImageExtent = Extent,
                ImageArrayLayers = 1,
                ImageUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit, // TransferSrc: video capture
                ImageSharingMode = VkSharingMode.Exclusive,
                PreTransform = caps.CurrentTransform,
                CompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
                PresentMode = presentMode,
                Clipped = true,
                OldSwapchain = old,
            },
            null
        );
        if (!old.IsNull())
            Device.DestroySwapchainKHR(old, null);

        var count = Device.GetSwapchainImagesKHR_Count(_swapchain);
        _images = new VkImage[count];
        Device.GetSwapchainImagesKHR_Write(_swapchain, _images);
        _views = new VkImageView[count];
        _renderFinished = new VkSemaphore[count];
        for (var i = 0; i < count; i++)
        {
            _views[i] = CreateView(_images[i], ColorFormat, VkImageAspectFlags.ColorBit);
            _renderFinished[i] = Device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        }

        CreateDepth();
    }

    void CreateDepth()
    {
        // One depth buffer shared by both frames in flight; the queue runs them in order
        _depthImage = Device.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = DepthFormat,
                Extent = new VkExtent3D { Width = Extent.Width, Height = Extent.Height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.DepthStencilAttachmentBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            null
        );
        _depthMemory = Device.AllocateMemory(PhysicalDevice, _depthImage, VkMemoryPropertyFlags.DeviceLocalBit, null);
        Device.BindImageMemory(_depthImage, _depthMemory, (ByteSize64)0);
        _depthView = CreateView(_depthImage, DepthFormat, VkImageAspectFlags.DepthBit);
    }

    VkImageView CreateView(VkImage image, VkFormat format, VkImageAspectFlags aspect) =>
        Device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2D,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = aspect, LevelCount = 1, LayerCount = 1 },
            },
            null
        );

    void DestroySwapchainResources(bool destroySwapchain)
    {
        if (_offscreen)
        {
            Device.DestroyImageView(_views[0], null);
            Device.DestroyImage(_images[0], null);
            Device.FreeMemory(_offscreenMemory, null);
            Device.DestroyImageView(_depthView, null);
            Device.DestroyImage(_depthImage, null);
            Device.FreeMemory(_depthMemory, null);
            return;
        }
        foreach (var v in _views)
            Device.DestroyImageView(v, null);
        foreach (var s in _renderFinished)
            Device.DestroySemaphore(s, null);
        Device.DestroyImageView(_depthView, null);
        Device.DestroyImage(_depthImage, null);
        Device.FreeMemory(_depthMemory, null);
        if (destroySwapchain)
            Device.DestroySwapchainKHR(_swapchain, null);
    }

    void RecreateSwapchain()
    {
        // A minimised window has a zero-size framebuffer; wait until it comes back
        while (_window.FramebufferSize.X == 0 || _window.FramebufferSize.Y == 0)
            Glfw.WaitEvents();
        Device.WaitIdle();
        DestroySwapchainResources(destroySwapchain: false);
        CreateSwapchain();
        _swapchainDirty = false;
        SwapchainRecreated?.Invoke();
    }

    /// <summary>
    /// Waits for this frame slot to be free, acquires a swapchain image and starts recording.
    /// Returns false if the frame should be skipped (the swapchain was just rebuilt).
    /// </summary>
    public bool BeginFrame(out CommandBuffer cmd)
    {
        cmd = _commandBuffers[FrameIndex];
        // Wayland never reports an out-of-date swapchain on resize, so compare sizes ourselves
        var fb = _window.FramebufferSize;
        var resized = fb.X != Extent.Width || fb.Y != Extent.Height;
        if (!_offscreen && (_swapchainDirty || resized || _presentModeVSync != VSync))
            RecreateSwapchain();

        Device.WaitForFence(_inFlight[FrameIndex], nint.MaxValue);
        FrameNumber++;
        RunReleases(FrameIndex); // this slot's last frame is done with them

        if (_offscreen)
        {
            _imageIndex = 0;
        }
        else
        {
            var result = Device.AcquireNextImageKHR(_swapchain, nint.MaxValue, _imageAvailable[FrameIndex], default, out _imageIndex);
            if (result == VkResult.ErrorOutOfDateKHR)
            {
                RecreateSwapchain();
                return false;
            }
            _swapchainDirty |= result == VkResult.SuboptimalKHR;
        }

        Device.ResetFences([_inFlight[FrameIndex]]);
        cmd.Reset(0);
        cmd.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });

        // Colour and depth into attachment layouts; previous contents are discarded
        Span<VkImageMemoryBarrier2> barriers =
        [
            new()
            {
                SrcStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
                DstStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
                DstAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,
                OldLayout = VkImageLayout.Undefined,
                NewLayout = VkImageLayout.ColorAttachmentOptimal,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = _images[_imageIndex],
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
                Image = _depthImage,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.DepthBit, LevelCount = 1, LayerCount = 1 },
            },
        ];
        fixed (VkImageMemoryBarrier2* b = barriers)
            cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 2, ImageMemoryBarriers = b });
        return true;
    }

    /// <summary>Starts dynamic rendering into the current swapchain image and the depth buffer.</summary>
    public void BeginRendering(CommandBuffer cmd, VkClearColorValue clear)
    {
        var color = new VkRenderingAttachmentInfo
        {
            ImageView = _views[_imageIndex],
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearValue { Color = clear },
        };
        var depth = new VkRenderingAttachmentInfo
        {
            ImageView = _depthView,
            ImageLayout = VkImageLayout.DepthAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.DontCare,
            ClearValue = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 0f } }, // reversed depth: 0 is far
        };
        _rendering = true;
        cmd.BeginRendering(
            new VkRenderingInfo
            {
                RenderArea = new VkRect2D { Extent = Extent },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                ColorAttachments = &color,
                DepthAttachment = &depth,
            }
        );
        cmd.SetViewport(0, [new VkViewport { Width = Extent.Width, Height = Extent.Height, MaxDepth = 1f }]);
        cmd.SetScissor(0, [new VkRect2D { Extent = Extent }]);
    }

    /// <summary>
    /// Ends rendering, moves the image to present layout, submits and presents. With
    /// <paramref name="capture"/>, the finished image is also copied there (tightly packed pixels in
    /// <see cref="ColorFormat"/>); it's ready once <see cref="WaitIdle"/> returns.
    /// </summary>
    /// <param name="endRendering">
    /// False if the caller already ended its own rendering, leaving the image in
    /// ColorAttachmentOptimal (for example a classic render pass used by a tool's UI).
    /// </param>
    public void EndFrame(CommandBuffer cmd, GpuBuffer? capture = null, bool endRendering = true)
    {
        if (endRendering && _rendering)
            cmd.EndRendering();
        _rendering = false;
        var oldLayout = VkImageLayout.ColorAttachmentOptimal;
        if (capture != null)
        {
            var toCopy = new VkImageMemoryBarrier2
            {
                SrcStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
                SrcAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,
                DstStageMask = VkPipelineStageFlags2.CopyBit,
                DstAccessMask = VkAccessFlags2.TransferReadBit,
                OldLayout = VkImageLayout.ColorAttachmentOptimal,
                NewLayout = VkImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = _images[_imageIndex],
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            };
            cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toCopy });
            cmd.CopyImageToBuffer(
                _images[_imageIndex],
                VkImageLayout.TransferSrcOptimal,
                capture.Buffer,
                [
                    new VkBufferImageCopy
                    {
                        ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                        ImageExtent = new VkExtent3D { Width = Extent.Width, Height = Extent.Height, Depth = 1 },
                    },
                ]
            );
            oldLayout = VkImageLayout.TransferSrcOptimal;
        }
        var toPresent = new VkImageMemoryBarrier2
        {
            SrcStageMask = capture != null ? VkPipelineStageFlags2.CopyBit : VkPipelineStageFlags2.ColorAttachmentOutputBit,
            SrcAccessMask = capture != null ? VkAccessFlags2.None : VkAccessFlags2.ColorAttachmentWriteBit,
            DstStageMask = VkPipelineStageFlags2.BottomOfPipeBit,
            OldLayout = oldLayout,
            NewLayout = VkImageLayout.PresentSrcKHR,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = _images[_imageIndex],
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        };
        cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toPresent });
        cmd.End();

        var handle = cmd.Handle;
        if (_offscreen)
        {
            Queue.Submit([new VkSubmitInfo { CommandBufferCount = 1, CommandBuffers = &handle }], _inFlight[FrameIndex]);
            FrameIndex = (FrameIndex + 1) % FramesInFlight;
            return;
        }

        var wait = _imageAvailable[FrameIndex];
        var signal = _renderFinished[_imageIndex];
        var waitStage = VkPipelineStageFlags.ColorAttachmentOutputBit;
        Queue.Submit(
            [
                new VkSubmitInfo
                {
                    WaitSemaphoreCount = 1,
                    WaitSemaphores = &wait,
                    WaitDstStageMask = &waitStage,
                    CommandBufferCount = 1,
                    CommandBuffers = &handle,
                    SignalSemaphoreCount = 1,
                    SignalSemaphores = &signal,
                },
            ],
            _inFlight[FrameIndex]
        );

        var swapchain = _swapchain;
        var index = _imageIndex;
        var result = Queue.PresentKHR(
            new VkPresentInfoKHR
            {
                WaitSemaphoreCount = 1,
                WaitSemaphores = &signal,
                SwapchainCount = 1,
                Swapchains = &swapchain,
                ImageIndices = &index,
            }
        );
        if (result is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
            _swapchainDirty = true;

        FrameIndex = (FrameIndex + 1) % FramesInFlight;
    }

    /// <summary>
    /// Records commands with <paramref name="record"/>, runs them on the GPU and waits. For one-time
    /// work such as uploading textures and meshes at load time, not for per-frame rendering.
    /// </summary>
    public void Submit(Action<CommandBuffer> record)
    {
        var cmd = Device.AllocateCommandBuffer(
            new VkCommandBufferAllocateInfo { CommandPool = _commandPool, Level = VkCommandBufferLevel.Primary }
        );
        cmd.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });
        record(cmd);
        cmd.End();
        var fence = Device.CreateFence(default, null);
        var handle = cmd.Handle;
        Queue.Submit([new VkSubmitInfo { CommandBufferCount = 1, CommandBuffers = &handle }], fence);
        Device.WaitForFence(fence, nint.MaxValue);
        Device.DestroyFence(fence, null);
        Device.FreeCommandBuffers(_commandPool, [cmd]);
    }

    /// <summary>Ends rendering started by <see cref="BeginRendering"/>, for a frame that draws more passes after it.</summary>
    public void EndRendering(CommandBuffer cmd)
    {
        if (_rendering)
            cmd.EndRendering();
        _rendering = false;
    }

    /// <summary>Blocks until the GPU has finished everything submitted so far.</summary>
    public void WaitIdle() => Device.WaitIdle();

    public void Dispose()
    {
        Device.WaitIdle();
        // Releasing one thing can queue the release of another (a texture frees its image): drain until empty
        while (_releases[0].Count > 0 || _releases[1].Count > 0)
        {
            RunReleases(0);
            RunReleases(1);
        }
        SavePipelineCache();
        Device.DestroyPipelineCache(PipelineCache, null);
        DestroySwapchainResources(destroySwapchain: true);
        for (var i = 0; i < FramesInFlight; i++)
        {
            Device.DestroySemaphore(_imageAvailable[i], null);
            Device.DestroyFence(_inFlight[i], null);
        }
        Device.DestroyCommandPool(_commandPool, null);
        Device.Dispose();
        Instance.DestroySurfaceKHR(_surface, null);
        Instance.Dispose();
    }
}
