using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;

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

    public override float4 ClearColor => new(0.035f, 0.04f, 0.05f, 1f);

    public override void Load()
    {
        Scene = Scene.Load(Engine, scenePath);
        Renderer = new SceneRenderer(Gpu, Shaders, Scene.Root);
        foreach (var node in Scene.Nodes)
            Renderer.Model(node.Model);
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

    public override void Render(CommandBuffer cmd, in FrameTime time)
    {
        var camera = Scene.Camera.ToCamera() with { Near = 0.05f, Far = 2000f };
        Renderer.Render(cmd, Scene.Nodes, camera, Gpu.Extent.Width / (float)Gpu.Extent.Height, Scene.Camera.Target, time.Total);
    }

    public override string Status => $"{Scene.Nodes.Count} nodes, {Renderer.TrianglesDrawn:N0} triangles";

    public override void Dispose() => Renderer?.Dispose();
}
