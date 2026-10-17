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

/// <summary>A specialization constant: <c>layout(constant_id = Id)</c>, as 32 bits (int, uint, bool or float bits).</summary>
public readonly record struct SpecConstant(int Id, uint Value)
{
    public static SpecConstant Int(int id, int value) => new(id, (uint)value);
    public static SpecConstant Bool(int id, bool value) => new(id, value ? 1u : 0u);
    public static SpecConstant Float(int id, float value) => new(id, BitConverter.SingleToUInt32Bits(value));
}

/// <summary>
/// Everything needed to make a graphics pipeline. By default it draws into the device's colour format
/// and depth format with reversed depth (greater-or-equal passes).
/// </summary>
public sealed record GraphicsPipelineDesc
{
    public required string VertexShader { get; init; }
    /// <summary>None for depth-only passes (shadow maps).</summary>
    public string? FragmentShader { get; init; }
    public required VkPipelineLayout Layout { get; init; }
    public VertexBinding[] Bindings { get; init; } = [];
    public VkPrimitiveTopology Topology { get; init; } = VkPrimitiveTopology.TriangleList;
    public Cull Cull { get; init; } = Cull.Back;
    public bool DepthTest { get; init; } = true;
    public bool DepthWrite { get; init; } = true;
    public VkCompareOp DepthCompare { get; init; } = VkCompareOp.GreaterOrEqual;
    /// <summary>Depth bias set per draw with <c>SetDepthBias</c> (shadow maps).</summary>
    public bool DepthBias { get; init; }
    /// <summary>Clamp depth instead of clipping it, so shadow casters behind the light's near plane still cast.</summary>
    public bool DepthClamp { get; init; }
    public Blend Blend { get; init; } = Blend.Opaque;
    /// <summary>Colour attachment formats; null means the device's colour format, empty means depth only.</summary>
    public VkFormat[]? ColorFormats { get; init; }
    /// <summary><see cref="VkFormat.Undefined"/> for no depth attachment.</summary>
    public VkFormat DepthFormat { get; init; } = GraphicsDevice.DepthFormat;
    public SpecConstant[] Constants { get; init; } = [];
}

public static class Pipelines
{
    /// <summary>Specialization info for <paramref name="constants"/>, pointing into <paramref name="entries"/> and <paramref name="data"/>.</summary>
    static unsafe VkSpecializationInfo Specialization(SpecConstant[] constants, VkSpecializationMapEntry* entries, uint* data)
    {
        for (var i = 0; i < constants.Length; i++)
        {
            entries[i] = new VkSpecializationMapEntry { ConstantID = constants[i].Id, Offset = (ByteSize32)(i * 4), Size = (ByteSize64)4 };
            data[i] = constants[i].Value;
        }
        return new VkSpecializationInfo { MapEntryCount = constants.Length, MapEntries = entries, DataSize = (ByteSize64)(constants.Length * 4), Data = data };
    }

    /// <summary>
    /// Creates a pipeline for dynamic rendering into the formats in <paramref name="desc"/>, with
    /// viewport and scissor (and depth bias, if asked for) set per frame. Front faces wind
    /// counter-clockwise in world space, as glTF and Blender export them.
    /// </summary>
    public static unsafe VkPipeline Create(GraphicsDevice gpu, ShaderLibrary shaders, GraphicsPipelineDesc desc)
    {
        var device = gpu.Device;
        var vert = shaders.Load(desc.VertexShader);
        var frag = desc.FragmentShader == null ? default : shaders.Load(desc.FragmentShader);
        var specEntries = stackalloc VkSpecializationMapEntry[Math.Max(1, desc.Constants.Length)];
        var specData = stackalloc uint[Math.Max(1, desc.Constants.Length)];
        var spec = Specialization(desc.Constants, specEntries, specData);
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
                    new() { Stage = VkShaderStageFlags.VertexBit, Module = vert, Name = name, SpecializationInfo = &spec },
                    new() { Stage = VkShaderStageFlags.FragmentBit, Module = frag, Name = name, SpecializationInfo = &spec },
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
                    DepthClampEnable = desc.DepthClamp && gpu.Features.DepthClamp,
                    DepthBiasEnable = desc.DepthBias,
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
                    DepthCompareOp = desc.DepthCompare,
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
                var colorFormats = desc.ColorFormats ?? [gpu.ColorFormat];
                var attachments = stackalloc VkPipelineColorBlendAttachmentState[Math.Max(1, colorFormats.Length)];
                for (var i = 0; i < colorFormats.Length; i++)
                    attachments[i] = attachment;
                var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = colorFormats.Length, Attachments = attachments };
                Span<VkDynamicState> dynamics = desc.DepthBias
                    ? [VkDynamicState.Viewport, VkDynamicState.Scissor, VkDynamicState.DepthBias]
                    : [VkDynamicState.Viewport, VkDynamicState.Scissor];
                fixed (VkFormat* formats = colorFormats)
                fixed (VkPipelineShaderStageCreateInfo* s = stages)
                fixed (VkDynamicState* d = dynamics)
                {
                    var rendering = new VkPipelineRenderingCreateInfo
                    {
                        ColorAttachmentCount = colorFormats.Length,
                        ColorAttachmentFormats = formats,
                        DepthAttachmentFormat = desc.DepthFormat,
                    };
                    var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = dynamics.Length, DynamicStates = d };
                    return device.CreateGraphicsPipeline(
                        gpu.PipelineCache,
                        new VkGraphicsPipelineCreateInfo
                        {
                            Next = &rendering,
                            StageCount = frag.IsNull() ? 1 : 2,
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
            if (!frag.IsNull())
                device.DestroyShaderModule(frag, null);
        }
    }

    /// <summary>Creates a compute pipeline from <c>Shaders/&lt;shader&gt;.spv</c>.</summary>
    public static unsafe VkPipeline CreateCompute(GraphicsDevice gpu, ShaderLibrary shaders, string shader, VkPipelineLayout layout, params SpecConstant[] constants)
    {
        var module = shaders.Load(shader);
        var specEntries = stackalloc VkSpecializationMapEntry[Math.Max(1, constants.Length)];
        var specData = stackalloc uint[Math.Max(1, constants.Length)];
        var spec = Specialization(constants, specEntries, specData);
        try
        {
            fixed (byte* name = "main"u8)
                return gpu.Device.CreateComputePipeline(
                    gpu.PipelineCache,
                    new VkComputePipelineCreateInfo
                    {
                        Stage = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.ComputeBit, Module = module, Name = name, SpecializationInfo = &spec },
                        Layout = layout,
                    },
                    null
                );
        }
        finally
        {
            gpu.Device.DestroyShaderModule(module, null);
        }
    }
}
