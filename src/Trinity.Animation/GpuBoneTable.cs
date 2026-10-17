using System.Numerics;
using Brutal;
using Brutal.VulkanApi;
using Trinity.Graphics;

namespace Trinity.Animation;

/// <summary>
/// A <see cref="SkinnedModel"/>'s baked animation on the GPU, as a read-only storage buffer at
/// set 0, binding 0, laid out for <c>skinning.glsl</c>:
/// a header (joint count, bake fps), one record per clip (first frame, frame count, duration),
/// then three float4 rows per joint per frame.
/// </summary>
public sealed unsafe class GpuBoneTable : IDisposable
{
    readonly GraphicsDevice _gpu;
    readonly GpuBuffer _buffer;
    readonly VkDescriptorPool _pool;

    public readonly VkDescriptorSetLayout SetLayout;
    public readonly VkDescriptorSet Set;

    /// <param name="clipOrder">Clip names in the order shaders index them, e.g. idle, walk, attack, death.</param>
    public GpuBoneTable(GraphicsDevice gpu, SkinnedModel model, params string[] clipOrder)
    {
        _gpu = gpu;
        var device = gpu.Device;

        var table = new List<Vector4> { new(model.JointCount, SkinnedModel.BakeFps, clipOrder.Length, 0) };
        foreach (var name in clipOrder)
        {
            var c = model.Clip(name);
            table.Add(new Vector4(c.FirstFrame, c.FrameCount, c.Duration, 0));
        }
        table.AddRange(model.BoneRows);
        _buffer = new GpuBuffer(gpu, table.Count * sizeof(Vector4), VkBufferUsageFlags.StorageBufferBit);
        _buffer.Write<Vector4>(table.ToArray());

        var binding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.VertexBit,
        };
        SetLayout = device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding }, null);
        var poolSize = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 1 };
        _pool = device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &poolSize }, null);
        var layout = SetLayout;
        VkDescriptorSet set = default;
        device.AllocateDescriptorSets(
            new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout },
            new ReadOnlySpan<VkDescriptorSet>(&set, 1)
        );
        Set = set;
        var info = new VkDescriptorBufferInfo { Buffer = _buffer.Buffer, Range = (ByteSize64)(ulong)_buffer.Size };
        device.UpdateDescriptorSets(
            [new VkWriteDescriptorSet { DstSet = Set, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &info }],
            []
        );
    }

    public void Dispose()
    {
        _gpu.Device.DestroyDescriptorPool(_pool, null);
        _gpu.Device.DestroyDescriptorSetLayout(SetLayout, null);
        _buffer.Dispose();
    }
}
