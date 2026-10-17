using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Trinity.Graphics;

/// <summary>One vertex buffer binding: stride, per-vertex or per-instance, and its attributes.</summary>
public sealed record VertexBinding(int Stride, bool PerInstance, params VertexAttribute[] Attributes);

/// <summary>A shader input: location, format and byte offset in its binding.</summary>
public readonly record struct VertexAttribute(int Location, VkFormat Format, int Offset);

public enum Cull
{
    None,
    Back,
    Front,
}

public enum Blend
{
    Opaque,
    Alpha,
    Additive,
}

/// <summary>Everything needed to make a graphics pipeline that draws into the device's colour + depth targets.</summary>
public sealed record GraphicsPipelineDesc
{
    public required string VertexShader { get; init; }
    public required string FragmentShader { get; init; }
    public required VkPipelineLayout Layout { get; init; }
    public VertexBinding[] Bindings { get; init; } = [];
    public VkPrimitiveTopology Topology { get; init; } = VkPrimitiveTopology.TriangleList;
    public Cull Cull { get; init; } = Cull.Back;
    public bool DepthTest { get; init; } = true;
    public bool DepthWrite { get; init; } = true;
    public Blend Blend { get; init; } = Blend.Opaque;
}

public static class Pipelines
{
    /// <summary>
    /// Creates a pipeline for dynamic rendering into <see cref="GraphicsDevice.ColorFormat"/> and
    /// <see cref="GraphicsDevice.DepthFormat"/>, with viewport and scissor set per frame.
    /// Front faces wind counter-clockwise in world space, as glTF and Blender export them.
    /// </summary>
    public static unsafe VkPipeline Create(GraphicsDevice gpu, ShaderLibrary shaders, GraphicsPipelineDesc desc)
    {
        var device = gpu.Device;
        var vert = shaders.Load(desc.VertexShader);
        var frag = shaders.Load(desc.FragmentShader);
        try
        {
            var bindings = new VkVertexInputBindingDescription[desc.Bindings.Length];
            var attributes = new List<VkVertexInputAttributeDescription>();
            for (var b = 0; b < desc.Bindings.Length; b++)
            {
                var binding = desc.Bindings[b];
                bindings[b] = new VkVertexInputBindingDescription
                {
                    Binding = b,
                    Stride = (ByteSize32)binding.Stride,
                    InputRate = binding.PerInstance ? VkVertexInputRate.Instance : VkVertexInputRate.Vertex,
                };
                foreach (var a in binding.Attributes)
                    attributes.Add(
                        new VkVertexInputAttributeDescription
                        {
                            Location = a.Location,
                            Binding = b,
                            Format = a.Format,
                            Offset = (ByteSize32)a.Offset,
                        }
                    );
            }
            var attributeArray = attributes.ToArray();

            var entry = "main"u8;
            fixed (byte* name = entry)
            fixed (VkVertexInputBindingDescription* pb = bindings)
            fixed (VkVertexInputAttributeDescription* pa = attributeArray)
            {
                Span<VkPipelineShaderStageCreateInfo> stages =
                [
                    new() { Stage = VkShaderStageFlags.VertexBit, Module = vert, Name = name },
                    new() { Stage = VkShaderStageFlags.FragmentBit, Module = frag, Name = name },
                ];
                var vertexInput = new VkPipelineVertexInputStateCreateInfo
                {
                    VertexBindingDescriptionCount = bindings.Length,
                    VertexBindingDescriptions = pb,
                    VertexAttributeDescriptionCount = attributeArray.Length,
                    VertexAttributeDescriptions = pa,
                };
                var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = desc.Topology };
                var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
                var raster = new VkPipelineRasterizationStateCreateInfo
                {
                    PolygonMode = VkPolygonMode.Fill,
                    CullMode = desc.Cull switch
                    {
                        Cull.Back => VkCullModeFlags.BackBit,
                        Cull.Front => VkCullModeFlags.FrontBit,
                        _ => VkCullModeFlags.None,
                    },
                    // Counter-clockwise in world space stays counter-clockwise in Vulkan's
                    // framebuffer: the projection's Y flip and Vulkan's downward Y cancel out
                    FrontFace = VkFrontFace.CounterClockwise,
                    LineWidth = 1f,
                };
                var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
                var depth = new VkPipelineDepthStencilStateCreateInfo
                {
                    DepthTestEnable = desc.DepthTest,
                    DepthWriteEnable = desc.DepthWrite,
                    DepthCompareOp = VkCompareOp.LessOrEqual,
                };
                var all = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit;
                var attachment = desc.Blend switch
                {
                    Blend.Alpha => new VkPipelineColorBlendAttachmentState
                    {
                        BlendEnable = true,
                        SrcColorBlendFactor = VkBlendFactor.SrcAlpha,
                        DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                        ColorBlendOp = VkBlendOp.Add,
                        SrcAlphaBlendFactor = VkBlendFactor.One,
                        DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                        AlphaBlendOp = VkBlendOp.Add,
                        ColorWriteMask = all,
                    },
                    Blend.Additive => new VkPipelineColorBlendAttachmentState
                    {
                        BlendEnable = true,
                        SrcColorBlendFactor = VkBlendFactor.One,
                        DstColorBlendFactor = VkBlendFactor.One,
                        ColorBlendOp = VkBlendOp.Add,
                        SrcAlphaBlendFactor = VkBlendFactor.One,
                        DstAlphaBlendFactor = VkBlendFactor.One,
                        AlphaBlendOp = VkBlendOp.Add,
                        ColorWriteMask = all,
                    },
                    _ => new VkPipelineColorBlendAttachmentState { ColorWriteMask = all },
                };
                var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &attachment };
                Span<VkDynamicState> dynamics = [VkDynamicState.Viewport, VkDynamicState.Scissor];
                var colorFormat = gpu.ColorFormat;
                var rendering = new VkPipelineRenderingCreateInfo
                {
                    ColorAttachmentCount = 1,
                    ColorAttachmentFormats = &colorFormat,
                    DepthAttachmentFormat = GraphicsDevice.DepthFormat,
                };
                fixed (VkPipelineShaderStageCreateInfo* s = stages)
                fixed (VkDynamicState* d = dynamics)
                {
                    var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = dynamics.Length, DynamicStates = d };
                    return device.CreateGraphicsPipeline(
                        default,
                        new VkGraphicsPipelineCreateInfo
                        {
                            Next = &rendering,
                            StageCount = 2,
                            Stages = s,
                            VertexInputState = &vertexInput,
                            InputAssemblyState = &inputAssembly,
                            ViewportState = &viewport,
                            RasterizationState = &raster,
                            MultisampleState = &multisample,
                            DepthStencilState = &depth,
                            ColorBlendState = &blend,
                            DynamicState = &dynamic,
                            Layout = desc.Layout,
                        },
                        null
                    );
                }
            }
        }
        finally
        {
            device.DestroyShaderModule(vert, null);
            device.DestroyShaderModule(frag, null);
        }
    }
}
