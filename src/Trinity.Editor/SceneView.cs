using Brutal.VulkanApi;
using Trinity.Graphics;
using Trinity.Scenes;

namespace Trinity.Editor;

/// <summary>The editor's viewport: the scene drawn by Trinity's <see cref="SceneRenderer"/> into a render target the size of the panel.</summary>
sealed class SceneView : IDisposable
{
    readonly GraphicsDevice _gpu;
    readonly SceneRenderer _renderer;

    public RenderTarget? Target { get; private set; }
    public OrbitCamera Camera = new() { Yaw = 0.8f, Pitch = 0.35f, Distance = 8f, Target = new(0, 1, 0) };
    public int TrianglesDrawn => _renderer.TrianglesDrawn;

    public SceneView(GraphicsDevice gpu, ShaderLibrary shaders, EditorProject project)
    {
        _gpu = gpu;
        _renderer = new SceneRenderer(gpu, shaders, project.Root);
    }

    public LoadedModel? Model(string relativePath) => _renderer.Model(relativePath);

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

    /// <summary>Advances the animation previews and records the scene into <see cref="Target"/>.</summary>
    public void Render(CommandBuffer cmd, EditorScene scene, Node? selected, float dt, double time)
    {
        if (Target == null)
            return;
        foreach (var o in scene.Objects)
            if (o.Playing)
                o.Time += dt * o.Speed;
        var camera = Camera.ToCamera() with { Near = 0.05f, Far = 2000f };
        Target.Begin(cmd, new VkClearColorValue { Float32 = new Brutal.Numerics.float4(0.035f, 0.04f, 0.05f, 1f) });
        _renderer.Render(cmd, scene.Objects, camera, Target.Width / (float)Target.Height, Camera.Target, time, selected);
        Target.End(cmd);
    }

    /// <summary>Points the camera at a node, at a distance that fits it.</summary>
    public void Frame(Node o)
    {
        var (centre, size) = _renderer.Bounds(o);
        Camera.Target = centre;
        Camera.Distance = MathF.Max(1.5f, size * 1.6f);
    }

    public void Dispose()
    {
        _gpu.WaitIdle();
        _renderer.Dispose();
        Target?.Dispose();
    }
}
