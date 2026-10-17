using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Trinity.Rendering;

namespace Trinity.Scenes;

/// <summary>
/// A game that is one scene and its scripts, as made in Trinity Editor:
/// <c>Engine.Run(new SceneGame("Scenes/main.scene.json"), options)</c>. The right mouse button orbits the
/// camera and the wheel zooms, unless a script moves <see cref="Scene.Camera"/> itself.
/// </summary>
public class SceneGame(string scenePath) : Game
{
    public Scene Scene { get; private set; } = null!;
    public SceneRenderer Renderer { get; private set; } = null!;
    RenderView _view = null!;

    /// <summary>Show a debug visualisation instead of the lit scene.</summary>
    public DebugView DebugView;

    public override void Load()
    {
        Scene = Scene.Load(Engine, scenePath);
        Renderer = new SceneRenderer(Gpu, Shaders, Scene.Root);
        _view = Renderer.Renderer.CreateView(Gpu.Extent.Width, Gpu.Extent.Height);
        Renderer.LoadAll(Scene.Nodes);
        Scene.Start();
    }

    public override void FixedUpdate(in FrameTime time) => Scene.FixedUpdate(time);

    public override void Update(in FrameTime time)
    {
        ref var cam = ref Scene.Camera;
        if (Input.Down(GlfwMouseButton.Right))
        {
            cam.Yaw -= (float)Input.MouseDelta.X * 0.008f;
            cam.Pitch = Math.Clamp(cam.Pitch + (float)Input.MouseDelta.Y * 0.006f, -1.4f, 1.5f);
        }
        if (Input.Scroll.Y != 0)
            cam.Distance = Math.Clamp(cam.Distance * MathF.Pow(0.88f, (float)Input.Scroll.Y), 0.3f, 1500f);
        if (Input.Pressed(GlfwKey.Escape))
            Window.ShouldClose = true;
        Scene.Update(time);
    }

    public override void RenderFrame(CommandBuffer cmd, in FrameTime time)
    {
        _view.Resize(Gpu.Extent.Width, Gpu.Extent.Height);
        _view.DebugView = DebugView;
        Scene.Environment.ApplyTo(Renderer.Renderer.Settings);
        var camera = Scene.Camera.ToCamera() with { Near = 0.05f, Far = 2000f };
        Renderer.Render(cmd, _view, Scene.Nodes, camera, RenderOutput.Swapchain(Gpu), time.Total);
    }

    public override string Status =>
        $"{Renderer.Renderer.Stats.Visible:N0}/{Renderer.Renderer.Stats.Instances:N0} visible, {Renderer.Renderer.Stats.Triangles:N0} triangles, {Renderer.Renderer.Stats.ShadowTriangles:N0} in shadows | GPU {Renderer.Renderer.Timer.TotalMs:F2} ms: "
        + string.Join(", ", Renderer.Renderer.Timer.Last.Where(s => s.Ms >= 0.005).Select(s => $"{s.Name} {s.Ms:F2}"));

    public override void Dispose()
    {
        _view?.Dispose();
        Renderer?.Dispose();
    }
}
