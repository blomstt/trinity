using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Trinity.Animation;
using Trinity.Graphics;

namespace Trinity.Scenes;

[StructLayout(LayoutKind.Sequential)]
struct ViewConstants
{
    public Matrix4x4 ViewProj;
    public Vector4 CameraPos;
    public Vector4 SunDir;
    public Vector4 Params; // time, grid half size, grid centre x, z
}

[StructLayout(LayoutKind.Sequential)]
struct NodeInstance
{
    public Vector4 Row0, Row1, Row2; // transform, 3x4
    public Vector4 Anim; // clip index, seconds, loop, highlighted
}

/// <summary>A model's GPU data, loaded once however many nodes use it.</summary>
public sealed class LoadedModel : IDisposable
{
    public readonly SkinnedModel Model;
    public readonly GpuBuffer Vertices, Indices;
    public readonly GpuBoneTable Bones;
    public readonly string[] ClipNames;

    public unsafe LoadedModel(GraphicsDevice gpu, string path)
    {
        Model = new SkinnedModel(path);
        Vertices = new GpuBuffer(gpu, Model.Vertices.Length * sizeof(SkinnedVertex), VkBufferUsageFlags.VertexBufferBit);
        Vertices.Write<SkinnedVertex>(Model.Vertices);
        Indices = new GpuBuffer(gpu, Model.Indices.Length * sizeof(uint), VkBufferUsageFlags.IndexBufferBit);
        Indices.Write<uint>(Model.Indices);
        ClipNames = Model.Clips.Select(c => c.Name).ToArray();
        Bones = new GpuBoneTable(gpu, Model, ClipNames);
    }

    public int Triangles => Model.Lod0.IndexCount / 3;

    public void Dispose()
    {
        Bones.Dispose();
        Vertices.Dispose();
        Indices.Dispose();
    }
}

/// <summary>
/// Draws scene nodes (their models, skinned and animated on the GPU) and an optional ground grid, into
/// whatever pass is open: the game's frame or the editor's viewport.
/// </summary>
public sealed unsafe class SceneRenderer : IDisposable
{
    readonly GraphicsDevice _gpu;
    readonly string _root;
    readonly Dictionary<string, LoadedModel?> _models = [];
    readonly VkPipelineLayout _layout;
    readonly VkPipeline _modelPipeline, _gridPipeline;
    readonly GpuBuffer[] _instances = new GpuBuffer[GraphicsDevice.FramesInFlight];
    int _capacity;

    public bool ShowGrid = true;
    public int TrianglesDrawn { get; private set; }

    /// <param name="root">The folder model paths are relative to.</param>
    public SceneRenderer(GraphicsDevice gpu, ShaderLibrary shaders, string root)
    {
        _gpu = gpu;
        _root = root;

        // Every model's bone table has the same set layout; make one to describe it
        var bindingLayout = MakeBoneSetLayout();
        var push = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            Size = (ByteSize32)sizeof(ViewConstants),
        };
        _layout = gpu.Device.CreatePipelineLayout([bindingLayout], [push], null);
        gpu.Device.DestroyDescriptorSetLayout(bindingLayout, null);

        _modelPipeline = Pipelines.Create(gpu, shaders, new GraphicsPipelineDesc
        {
            VertexShader = "scene_model.vert",
            FragmentShader = "scene_model.frag",
            Layout = _layout,
            Cull = Cull.Back,
            Bindings =
            [
                new VertexBinding(sizeof(SkinnedVertex), PerInstance: false,
                    new(0, VkFormat.R32G32B32SFloat, 0), new(1, VkFormat.R32G32B32SFloat, 12),
                    new(2, VkFormat.R16G16B16A16UInt, 24), new(3, VkFormat.R32G32B32A32SFloat, 32),
                    new(4, VkFormat.R8G8B8A8UNorm, 48)),
                new VertexBinding(sizeof(NodeInstance), PerInstance: true,
                    new(5, VkFormat.R32G32B32A32SFloat, 0), new(6, VkFormat.R32G32B32A32SFloat, 16),
                    new(7, VkFormat.R32G32B32A32SFloat, 32), new(8, VkFormat.R32G32B32A32SFloat, 48)),
            ],
        });
        _gridPipeline = Pipelines.Create(gpu, shaders, new GraphicsPipelineDesc
        {
            VertexShader = "scene_grid.vert",
            FragmentShader = "scene_grid.frag",
            Layout = _layout,
            Cull = Cull.None,
            DepthWrite = false,
            Blend = Blend.Alpha,
        });
        EnsureCapacity(64);
    }

    VkDescriptorSetLayout MakeBoneSetLayout()
    {
        var binding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.VertexBit,
        };
        return _gpu.Device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding }, null);
    }

    void EnsureCapacity(int nodes)
    {
        if (nodes <= _capacity)
            return;
        _gpu.WaitIdle();
        _capacity = Math.Max(nodes, _capacity * 2);
        for (var i = 0; i < _instances.Length; i++)
        {
            _instances[i]?.Dispose();
            _instances[i] = new GpuBuffer(_gpu, (long)_capacity * sizeof(NodeInstance), VkBufferUsageFlags.VertexBufferBit);
        }
    }

    /// <summary>The model at a root-relative path, loaded on first use; null if it can't be loaded.</summary>
    public LoadedModel? Model(string relativePath)
    {
        if (_models.TryGetValue(relativePath, out var loaded))
            return loaded;
        try
        {
            loaded = string.IsNullOrEmpty(relativePath) ? null : new LoadedModel(_gpu, Path.GetFullPath(Path.Combine(_root, relativePath)));
            if (loaded != null)
                Log.Info("scene", $"loaded {relativePath}: {loaded.Triangles:N0} triangles, {(loaded.Model.IsRigged ? $"{loaded.Model.JointCount} bones, clips {string.Join(", ", loaded.ClipNames)}" : "static")}");
        }
        catch (Exception e)
        {
            Log.Error("scene", $"couldn't load {relativePath}: {e.Message}");
            loaded = null;
        }
        _models[relativePath] = loaded;
        return loaded;
    }

    /// <summary>Records the nodes (and grid) into the open pass. <paramref name="highlighted"/> gets a rim.</summary>
    public void Render(CommandBuffer cmd, IReadOnlyList<Node> nodes, in Camera camera, float aspect, Vector3 gridCentre, double time, Node? highlighted = null)
    {
        EnsureCapacity(nodes.Count);

        var instances = _instances[_gpu.FrameIndex].AsSpan<NodeInstance>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var o = nodes[i];
            var model = Model(o.Model);
            var clip = model == null ? 0 : Math.Max(0, Array.IndexOf(model.ClipNames, o.Clip));
            var duration = model == null || model.Model.Clips.Count == 0 ? 0 : model.Model.Clips[clip].Duration;
            var loop = o.Clip != "death";
            var t = duration <= 0 ? 0 : loop ? o.Time % duration : Math.Min(o.Time, duration);
            var m = o.Transform;
            instances[i] = new NodeInstance
            {
                Row0 = new Vector4(m.M11, m.M21, m.M31, m.M41),
                Row1 = new Vector4(m.M12, m.M22, m.M32, m.M42),
                Row2 = new Vector4(m.M13, m.M23, m.M33, m.M43),
                Anim = new Vector4(clip, t, loop ? 1 : 0, o == highlighted ? 1 : 0),
            };
        }

        var constants = new ViewConstants
        {
            ViewProj = camera.ViewProjection(aspect),
            CameraPos = new Vector4(camera.Position, 1),
            SunDir = new Vector4(Vector3.Normalize(new Vector3(-0.4f, -0.8f, -0.45f)), 0),
            Params = new Vector4((float)time, MathF.Max(60f, Vector3.Distance(camera.Position, camera.Target) * 6f), MathF.Round(gridCentre.X), MathF.Round(gridCentre.Z)),
        };
        cmd.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<ViewConstants>(in constants)));

        TrianglesDrawn = 0;
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _modelPipeline);
        for (var i = 0; i < nodes.Count; i++)
        {
            if (Model(nodes[i].Model) is not { } model)
                continue;
            cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [model.Bones.Set], []);
            cmd.BindVertexBuffers(0, [model.Vertices.Buffer, _instances[_gpu.FrameIndex].Buffer], [(ByteSize64)0, (ByteSize64)0]);
            cmd.BindIndexBuffer(model.Indices.Buffer, (ByteSize64)0, VkIndexType.UInt32);
            cmd.DrawIndexed(model.Model.Lod0.IndexCount, 1, model.Model.Lod0.FirstIndex, 0, i);
            TrianglesDrawn += model.Triangles;
        }

        // The grid last, blended over the ground, so models stand on it
        if (ShowGrid)
        {
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _gridPipeline);
            cmd.Draw(6, 1, 0, 0);
        }
    }

    /// <summary>The centre and size of a node's model in the world, for framing it with a camera.</summary>
    public (Vector3 Centre, float Size) Bounds(Node o)
    {
        if (Model(o.Model) is not { } m || m.Model.Vertices.Length == 0)
            return (o.Position, 2f * o.Scale);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in m.Model.Vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }
        return (Vector3.Transform((min + max) / 2, o.Transform), (max - min).Length() * o.Scale);
    }

    public void Dispose()
    {
        _gpu.WaitIdle();
        foreach (var m in _models.Values)
            m?.Dispose();
        foreach (var b in _instances)
            b?.Dispose();
        _gpu.Device.DestroyPipeline(_modelPipeline, null);
        _gpu.Device.DestroyPipeline(_gridPipeline, null);
        _gpu.Device.DestroyPipelineLayout(_layout, null);
    }
}
