using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Trinity.Animation;
using Trinity.Graphics;

namespace Trinity.Editor;

[StructLayout(LayoutKind.Sequential)]
struct ViewConstants
{
    public Matrix4x4 ViewProj;
    public Vector4 CameraPos;
    public Vector4 SunDir;
    public Vector4 Params; // time, grid half size, grid centre x, z
}

[StructLayout(LayoutKind.Sequential)]
struct ObjectInstance
{
    public Vector4 Row0, Row1, Row2; // transform, 3x4
    public Vector4 Anim; // clip index, seconds, loop, selected
}

/// <summary>A model's GPU data, loaded once however many objects use it.</summary>
sealed class LoadedModel : IDisposable
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

/// <summary>Draws the editor scene (grid and objects) into a render target the size of the viewport panel.</summary>
sealed unsafe class SceneView : IDisposable
{
    readonly GraphicsDevice _gpu;
    readonly EditorProject _project;
    readonly Dictionary<string, LoadedModel?> _models = [];
    readonly VkPipelineLayout _layout;
    readonly VkPipeline _modelPipeline, _gridPipeline;
    readonly GpuBuffer[] _instances = new GpuBuffer[GraphicsDevice.FramesInFlight];
    int _capacity;

    public RenderTarget? Target { get; private set; }
    public OrbitCamera Camera = new() { Yaw = 0.8f, Pitch = 0.35f, Distance = 8f, Target = new Vector3(0, 1, 0) };
    public int TrianglesDrawn { get; private set; }

    public SceneView(GraphicsDevice gpu, ShaderLibrary shaders, EditorProject project)
    {
        _gpu = gpu;
        _project = project;

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
            VertexShader = "editor_model.vert",
            FragmentShader = "editor_model.frag",
            Layout = _layout,
            Cull = Cull.Back,
            Bindings =
            [
                new VertexBinding(sizeof(SkinnedVertex), PerInstance: false,
                    new(0, VkFormat.R32G32B32SFloat, 0), new(1, VkFormat.R32G32B32SFloat, 12),
                    new(2, VkFormat.R16G16B16A16UInt, 24), new(3, VkFormat.R32G32B32A32SFloat, 32),
                    new(4, VkFormat.R8G8B8A8UNorm, 48)),
                new VertexBinding(sizeof(ObjectInstance), PerInstance: true,
                    new(5, VkFormat.R32G32B32A32SFloat, 0), new(6, VkFormat.R32G32B32A32SFloat, 16),
                    new(7, VkFormat.R32G32B32A32SFloat, 32), new(8, VkFormat.R32G32B32A32SFloat, 48)),
            ],
        });
        _gridPipeline = Pipelines.Create(gpu, shaders, new GraphicsPipelineDesc
        {
            VertexShader = "editor_grid.vert",
            FragmentShader = "editor_grid.frag",
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

    void EnsureCapacity(int objects)
    {
        if (objects <= _capacity)
            return;
        _gpu.WaitIdle();
        _capacity = Math.Max(objects, _capacity * 2);
        for (var i = 0; i < _instances.Length; i++)
        {
            _instances[i]?.Dispose();
            _instances[i] = new GpuBuffer(_gpu, (long)_capacity * sizeof(ObjectInstance), VkBufferUsageFlags.VertexBufferBit);
        }
    }

    /// <summary>The model at a project-relative path, loaded on first use; null if it can't be loaded.</summary>
    public LoadedModel? Model(string relativePath)
    {
        if (_models.TryGetValue(relativePath, out var loaded))
            return loaded;
        try
        {
            loaded = new LoadedModel(_gpu, _project.Absolute(relativePath));
            Log.Info("editor", $"loaded {relativePath}: {loaded.Triangles:N0} triangles, {(loaded.Model.IsRigged ? $"{loaded.Model.JointCount} bones, clips {string.Join(", ", loaded.ClipNames)}" : "static")}");
        }
        catch (Exception e)
        {
            Log.Error("editor", $"couldn't load {relativePath}: {e.Message}");
            loaded = null;
        }
        _models[relativePath] = loaded;
        return loaded;
    }

    /// <summary>Makes the render target match the viewport panel. Call before the UI references it.</summary>
    public bool Resize(int width, int height)
    {
        if (width < 8 || height < 8 || (Target != null && Target.Width == width && Target.Height == height))
            return false;
        _gpu.WaitIdle();
        Target?.Dispose();
        Target = new RenderTarget(_gpu, width, height);
        return true;
    }

    /// <summary>Advances animations and records the scene into <see cref="Target"/>.</summary>
    public void Render(CommandBuffer cmd, EditorScene scene, SceneObject? selected, float dt, double time)
    {
        if (Target == null)
            return;
        EnsureCapacity(scene.Objects.Count);

        // Per-object instance data for this frame
        var instances = _instances[_gpu.FrameIndex].AsSpan<ObjectInstance>();
        for (var i = 0; i < scene.Objects.Count; i++)
        {
            var o = scene.Objects[i];
            var model = Model(o.Model);
            var clip = model == null ? 0 : Math.Max(0, Array.IndexOf(model.ClipNames, o.Clip));
            var duration = model == null || model.Model.Clips.Count == 0 ? 0 : model.Model.Clips[clip].Duration;
            if (o.Playing)
                o.Time += dt * o.Speed;
            var loop = o.Clip != "death";
            var t = duration <= 0 ? 0 : loop ? o.Time % duration : Math.Min(o.Time, duration);
            var m = o.Transform;
            instances[i] = new ObjectInstance
            {
                Row0 = new Vector4(m.M11, m.M21, m.M31, m.M41),
                Row1 = new Vector4(m.M12, m.M22, m.M32, m.M42),
                Row2 = new Vector4(m.M13, m.M23, m.M33, m.M43),
                Anim = new Vector4(clip, t, loop ? 1 : 0, o == selected ? 1 : 0),
            };
        }

        var camera = Camera.ToCamera() with { Near = 0.05f, Far = 2000f };
        var constants = new ViewConstants
        {
            ViewProj = camera.ViewProjection(Target.Width / (float)Target.Height),
            CameraPos = new Vector4(camera.Position, 1),
            SunDir = new Vector4(Vector3.Normalize(new Vector3(-0.4f, -0.8f, -0.45f)), 0),
            Params = new Vector4((float)time, MathF.Max(60f, Camera.Distance * 6f), MathF.Round(Camera.Target.X), MathF.Round(Camera.Target.Z)),
        };

        Target.Begin(cmd, new VkClearColorValue { Float32 = new Brutal.Numerics.float4(0.035f, 0.04f, 0.05f, 1f) });
        cmd.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<ViewConstants>(in constants)));

        TrianglesDrawn = 0;
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _modelPipeline);
        for (var i = 0; i < scene.Objects.Count; i++)
        {
            if (Model(scene.Objects[i].Model) is not { } model)
                continue;
            cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [model.Bones.Set], []);
            cmd.BindVertexBuffers(0, [model.Vertices.Buffer, _instances[_gpu.FrameIndex].Buffer], [(ByteSize64)0, (ByteSize64)0]);
            cmd.BindIndexBuffer(model.Indices.Buffer, (ByteSize64)0, VkIndexType.UInt32);
            cmd.DrawIndexed(model.Model.Lod0.IndexCount, 1, model.Model.Lod0.FirstIndex, 0, i);
            TrianglesDrawn += model.Triangles;
        }

        // The grid last, blended over the ground, so objects stand on it
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _gridPipeline);
        cmd.Draw(6, 1, 0, 0);
        Target.End(cmd);
    }

    /// <summary>Points the camera at an object, at a distance that fits it.</summary>
    public void Frame(SceneObject o)
    {
        var size = 2f * o.Scale;
        if (Model(o.Model) is { } m && m.Model.Vertices.Length > 0)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var v in m.Model.Vertices)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
            size = (max - min).Length() * o.Scale;
            Camera.Target = Vector3.Transform((min + max) / 2, o.Transform);
        }
        else
        {
            Camera.Target = o.Position;
        }
        Camera.Distance = MathF.Max(1.5f, size * 1.6f);
    }

    public void Dispose()
    {
        _gpu.WaitIdle();
        foreach (var m in _models.Values)
            m?.Dispose();
        foreach (var b in _instances)
            b?.Dispose();
        Target?.Dispose();
        _gpu.Device.DestroyPipeline(_modelPipeline, null);
        _gpu.Device.DestroyPipeline(_gridPipeline, null);
        _gpu.Device.DestroyPipelineLayout(_layout, null);
    }
}
