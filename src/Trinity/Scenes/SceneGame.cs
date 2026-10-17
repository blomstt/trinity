using System.Numerics;
using Brutal.GlfwApi;
using Brutal.VulkanApi;
using Trinity.Graphics;
using Trinity.Rendering;

namespace Trinity.Scenes;

/// <summary>
/// A game that is one scene and its scripts, as made in Trinity Editor:
/// <c>Engine.Run(new SceneGame("Scenes/main.scene.json"), options)</c>. It views the scene through the
/// first <see cref="Camera3D"/> marked current; without one, through an orbit camera (right mouse button
/// orbits, the wheel zooms) that scripts can also move (<see cref="Scene.Camera"/>). A
/// <see cref="DirectionalLight"/> sets the sun; otherwise the scene's environment does.
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
        Renderer.LoadAll(Scene.World);
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
        var settings = Renderer.Renderer.Settings;
        Scene.Environment.ApplyTo(settings);
        ApplyLight(Scene.World, settings);
        Renderer.Render(cmd, _view, Scene.World, ViewCamera(Scene), RenderOutput.Swapchain(Gpu), time.Total);
    }

    /// <summary>The camera a scene is seen through: its current <see cref="Camera3D"/>, or its orbit camera.</summary>
    public static Camera ViewCamera(Scene scene)
    {
        var world = scene.World;
        var cameras = world.Store<Camera3D>();
        for (var i = 0; i < cameras.Count; i++)
        {
            ref var c = ref cameras.Components[i];
            if (!c.Current)
                continue;
            var node = new Node(world, cameras.Entities[i]);
            return new Camera
            {
                Position = node.WorldPosition,
                Target = node.WorldPosition + node.Forward,
                FieldOfView = float.DegreesToRadians(c.FieldOfViewOrDefault),
                Near = c.NearOrDefault,
                Far = c.FarOrDefault,
            };
        }
        return scene.Camera.ToCamera() with { Near = 0.05f, Far = 2000f };
    }

    /// <summary>The sun from the world's first directional light, if it has one.</summary>
    public static void ApplyLight(World world, RenderSettings settings)
    {
        var lights = world.Store<DirectionalLight>();
        if (lights.Count == 0)
            return;
        var node = new Node(world, lights.Entities[0]);
        settings.SunDirection = node.Forward;
        if (lights.Components[0].Illuminance > 0)
            settings.SunIlluminance = lights.Components[0].Illuminance;
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
