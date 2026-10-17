using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Trinity.Animation;
using Trinity.Graphics;

namespace BattleSample;

/// <summary>One soldier as the GPU sees it. The simulation writes these straight into mapped memory.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SoldierInstance
{
    public Vector4 Place; // x, z, facing, team
    public Vector4 Anim; // walk phase, attack phase (<0 none), death progress, speed
}

[StructLayout(LayoutKind.Sequential)]
public struct FrameConstants
{
    public Matrix4x4 ViewProj;
    public Vector4 CameraPos;
    public Vector4 SunDir;
    public Vector4 Params; // time, fog density
}

/// <summary>The ground and every soldier: two instanced draws (detailed and distant) from one skinned model.</summary>
public sealed unsafe class CrowdRenderer : IDisposable
{
    readonly GraphicsDevice _gpu;
    readonly GpuBoneTable _bones;
    readonly VkPipelineLayout _layout;
    readonly VkPipeline _crowd, _ground;
    readonly GpuBuffer _vertices, _indices;
    readonly GpuBuffer[] _instances = new GpuBuffer[GraphicsDevice.FramesInFlight];
    readonly (int First, int Count) _lod0, _lod1;

    public int Capacity { get; private set; }
    public int TrianglesNear => _lod0.Count / 3;
    public int TrianglesFar => _lod1.Count / 3;

    public CrowdRenderer(GraphicsDevice gpu, ShaderLibrary shaders, SkinnedModel model, int capacity)
    {
        _gpu = gpu;
        _bones = new GpuBoneTable(gpu, model, "idle", "walk", "attack", "death");
        _vertices = new GpuBuffer(gpu, model.Vertices.Length * sizeof(SkinnedVertex), VkBufferUsageFlags.VertexBufferBit);
        _vertices.Write<SkinnedVertex>(model.Vertices);
        _indices = new GpuBuffer(gpu, model.Indices.Length * sizeof(uint), VkBufferUsageFlags.IndexBufferBit);
        _indices.Write<uint>(model.Indices);
        _lod0 = model.Lod0;
        _lod1 = model.Lod1;
        EnsureCapacity(capacity);

        var push = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            Size = (ByteSize32)sizeof(FrameConstants),
        };
        _layout = gpu.Device.CreatePipelineLayout([_bones.SetLayout], [push], null);

        _crowd = Pipelines.Create(gpu, shaders, new GraphicsPipelineDesc
        {
            VertexShader = "crowd.vert",
            FragmentShader = "lit.frag",
            Layout = _layout,
            Cull = Cull.Back,
            Bindings =
            [
                new VertexBinding(sizeof(SkinnedVertex), PerInstance: false,
                    new(0, VkFormat.R32G32B32SFloat, 0), new(1, VkFormat.R32G32B32SFloat, 12),
                    new(2, VkFormat.R16G16B16A16UInt, 24), new(3, VkFormat.R32G32B32A32SFloat, 32),
                    new(4, VkFormat.R8G8B8A8UNorm, 48)),
                new VertexBinding(sizeof(SoldierInstance), PerInstance: true,
                    new(5, VkFormat.R32G32B32A32SFloat, 0), new(6, VkFormat.R32G32B32A32SFloat, 16)),
            ],
        });
        _ground = Pipelines.Create(gpu, shaders, new GraphicsPipelineDesc
        {
            VertexShader = "ground.vert",
            FragmentShader = "ground.frag",
            Layout = _layout,
            Cull = Cull.None,
        });
    }

    public void EnsureCapacity(int soldiers)
    {
        if (soldiers <= Capacity)
            return;
        _gpu.WaitIdle();
        for (var i = 0; i < _instances.Length; i++)
        {
            _instances[i]?.Dispose();
            _instances[i] = new GpuBuffer(_gpu, (long)soldiers * sizeof(SoldierInstance), VkBufferUsageFlags.VertexBufferBit);
        }
        Capacity = soldiers;
    }

    /// <summary>This frame's instance memory; the simulation fills it.</summary>
    public Span<SoldierInstance> Instances(int frameIndex) => _instances[frameIndex].AsSpan<SoldierInstance>();

    public void Draw(CommandBuffer cmd, int frameIndex, int near, int far, in FrameConstants constants)
    {
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<FrameConstants>(in constants));
        cmd.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, 0, bytes);

        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _ground);
        cmd.Draw(6, 1, 0, 0);

        if (near + far == 0)
            return;
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _crowd);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_bones.Set], []);
        cmd.BindVertexBuffers(0, [_vertices.Buffer, _instances[frameIndex].Buffer], [(ByteSize64)0, (ByteSize64)0]);
        cmd.BindIndexBuffer(_indices.Buffer, (ByteSize64)0, VkIndexType.UInt32);
        if (near > 0)
            cmd.DrawIndexed(_lod0.Count, near, _lod0.First, 0, 0);
        if (far > 0)
            cmd.DrawIndexed(_lod1.Count, far, _lod1.First, 0, near);
    }

    public void Dispose()
    {
        var device = _gpu.Device;
        device.DestroyPipeline(_crowd, null);
        device.DestroyPipeline(_ground, null);
        device.DestroyPipelineLayout(_layout, null);
        _bones.Dispose();
        _vertices.Dispose();
        _indices.Dispose();
        foreach (var b in _instances)
            b?.Dispose();
    }
}
