using Brutal;
using Brutal.VulkanApi;
using Trinity.Graphics;

namespace Trinity.Editor;

/// <summary>
/// The classic Vulkan render pass the editor's UI is drawn in, over the swapchain image, because
/// BRUTAL's ImGui Vulkan backend needs a render pass (not dynamic rendering). The image arrives in
/// ColorAttachmentOptimal from <see cref="GraphicsDevice.BeginFrame"/> and is left that way for
/// <see cref="GraphicsDevice.EndFrame"/>.
/// </summary>
sealed unsafe class UiPass : IDisposable
{
    readonly GraphicsDevice _gpu;
    VkFramebuffer[] _framebuffers = [];
    public readonly VkRenderPass RenderPass;

    public UiPass(GraphicsDevice gpu)
    {
        _gpu = gpu;
        var color = new VkAttachmentDescription
        {
            Format = gpu.ColorFormat,
            Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.ColorAttachmentOptimal,
            FinalLayout = VkImageLayout.ColorAttachmentOptimal,
        };
        var reference = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var subpass = new VkSubpassDescription
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            ColorAttachments = &reference,
        };
        RenderPass = gpu.Device.CreateRenderPass(
            new VkRenderPassCreateInfo { AttachmentCount = 1, Attachments = &color, SubpassCount = 1, Subpasses = &subpass },
            null
        );
        CreateFramebuffers();
        gpu.SwapchainRecreated += Recreate;
    }

    void CreateFramebuffers()
    {
        var views = _gpu.ImageViews;
        _framebuffers = new VkFramebuffer[views.Count];
        for (var i = 0; i < views.Count; i++)
        {
            var view = views[i];
            _framebuffers[i] = _gpu.Device.CreateFramebuffer(
                new VkFramebufferCreateInfo
                {
                    RenderPass = RenderPass,
                    AttachmentCount = 1,
                    Attachments = &view,
                    Width = _gpu.Extent.Width,
                    Height = _gpu.Extent.Height,
                    Layers = 1,
                },
                null
            );
        }
    }

    void Recreate()
    {
        foreach (var f in _framebuffers)
            _gpu.Device.DestroyFramebuffer(f, null);
        CreateFramebuffers();
    }

    public void Begin(CommandBuffer cmd, VkClearColorValue clear)
    {
        var clearValue = new VkClearValue { Color = clear };
        cmd.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = RenderPass,
                Framebuffer = _framebuffers[_gpu.ImageIndex],
                RenderArea = new VkRect2D { Extent = _gpu.Extent },
                ClearValueCount = 1,
                ClearValues = &clearValue,
            },
            VkSubpassContents.Inline
        );
    }

    public static void End(CommandBuffer cmd) => cmd.EndRenderPass();

    public void Dispose()
    {
        _gpu.SwapchainRecreated -= Recreate;
        foreach (var f in _framebuffers)
            _gpu.Device.DestroyFramebuffer(f, null);
        _gpu.Device.DestroyRenderPass(RenderPass, null);
    }
}
