using System.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Trinity.Graphics;
using Trinity.Rendering;
using Trinity.Scenes;

namespace Trinity.Editor;

/// <summary>
/// The editor's viewport: the scene rendered by Trinity's renderer at the panel's size, tone-mapped
/// into an image the UI shows, with the ground grid drawn over it.
/// </summary>
sealed class SceneView : IDisposable
{
    readonly GraphicsDevice _gpu;
    readonly SceneRenderer _scene;
    RenderView? _view;

    /// <summary>The finished viewport image, for the UI to show (sampled with <see cref="Sampler"/>).</summary>
    public GpuImage? Output { get; private set; }
    public readonly VkSampler Sampler;
    public OrbitCamera Camera = new() { Yaw = 0.8f, Pitch = 0.35f, Distance = 8f, Target = new(0, 1, 0) };
    public long TrianglesDrawn => _scene.TrianglesDrawn;
    public Renderer Renderer => _scene.Renderer;
    public DebugView DebugView = DebugView.None;

    public SceneView(GraphicsDevice gpu, ShaderLibrary shaders, EditorProject project)
    {
        _gpu = gpu;
        _scene = new SceneRenderer(gpu, shaders, project.Root);
        Sampler = gpu.Device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                AddressModeU = VkSamplerAddressMode.ClampToEdge,
                AddressModeV = VkSamplerAddressMode.ClampToEdge,
                AddressModeW = VkSamplerAddressMode.ClampToEdge,
            },
            null
        );
    }

    public Mesh? Model(string relativePath) => _scene.Model(relativePath);

    /// <summary>Models or textures still loading (keep drawing until they're in).</summary>
    public bool Loading => _scene.Loading;

    /// <summary>Raised on a worker thread when something finished loading.</summary>
    public event Action? AssetReady
    {
        add => _scene.AssetReady += value;
        remove => _scene.AssetReady -= value;
    }

    /// <summary>Makes the viewport images match the panel. Call before the UI references <see cref="Output"/>.</summary>
    public bool Resize(int width, int height)
    {
        if (width < 8 || height < 8 || (Output != null && Output.Width == width && Output.Height == height))
            return false;
        _gpu.WaitIdle();
        Output?.Dispose();
        Output = new GpuImage(_gpu, new GpuImageDesc
        {
            Format = _gpu.ColorFormat,
            Width = width,
            Height = height,
            Usage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit,
        });
        if (_view == null)
            _view = _scene.Renderer.CreateView(width, height);
        else
            _view.Resize(width, height);
        return true;
    }

    public Camera ViewCamera => Camera.ToCamera() with { Near = 0.05f, Far = 2000f };

    /// <summary>Which gizmo axis (0 X, 1 Y, 2 Z) the mouse is over or dragging; -1 for none.</summary>
    public int GizmoAxis = -1;

    /// <summary>Advances the animation previews and records the scene into <see cref="Output"/>, with the selection's bounds and move gizmo.</summary>
    public void Render(CommandBuffer cmd, EditorScene scene, Node? selected, float dt, double time)
    {
        if (Output == null || _view == null)
            return;
        foreach (var o in scene.Objects)
            if (o.Playing)
                o.Time += dt * o.Speed;
        scene.File.Environment.ApplyTo(_scene.Renderer.Settings);
        _view.DebugView = DebugView;
        _view.GridHalfSize = MathF.Max(60f, Camera.Distance * 6f);
        if (selected != null)
            DrawSelection(selected);
        _scene.Render(cmd, _view, scene.Objects, ViewCamera, RenderOutput.To(Output), time, selected, Camera.Target);
    }

    void DrawSelection(Node o)
    {
        if (Model(o.Model) is { } m)
            Debug.Box(Matrix4x4.CreateScale(m.BoundsMax - m.BoundsMin) * Matrix4x4.CreateTranslation((m.BoundsMin + m.BoundsMax) / 2) * o.Transform, Debug.Bronze with { W = 0.8f });
        var length = GizmoLength(o);
        ReadOnlySpan<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        Span<Vector4> colors = [Debug.Red, Debug.Green, Debug.Blue];
        for (var a = 0; a < 3; a++)
            Debug.Arrow(o.Position, o.Position + axes[a] * length, a == GizmoAxis ? Debug.Yellow : colors[a], onTop: true);
    }

    /// <summary>The move gizmo's arrow length: a steady size on screen.</summary>
    public float GizmoLength(Node o) => Vector3.Distance(ViewCamera.Position, o.Position) * 0.15f;

    /// <summary>A world point's position in the viewport (pixels from its top left), or null behind the camera.</summary>
    public Vector2? ToScreen(Vector3 world)
    {
        if (Output == null)
            return null;
        var p = Vector4.Transform(new Vector4(world, 1), ViewCamera.ViewProjection(Output.Width / (float)Output.Height));
        if (p.W <= 1e-4f)
            return null;
        return new Vector2((p.X / p.W * 0.5f + 0.5f) * Output.Width, (p.Y / p.W * 0.5f + 0.5f) * Output.Height);
    }

    /// <summary>The ray through a viewport pixel, from the camera.</summary>
    public (Vector3 Origin, Vector3 Direction) Ray(Vector2 pixel)
    {
        var aspect = Output == null ? 1 : Output.Width / (float)Output.Height;
        Matrix4x4.Invert(ViewCamera.ViewProjection(aspect), out var inv);
        var ndc = new Vector2(pixel.X / Math.Max(1, Output?.Width ?? 1) * 2 - 1, pixel.Y / Math.Max(1, Output?.Height ?? 1) * 2 - 1);
        var near = Vector4.Transform(new Vector4(ndc, 1, 1), inv); // reversed depth: 1 is near
        var far = Vector4.Transform(new Vector4(ndc, 0.001f, 1), inv);
        var a = new Vector3(near.X, near.Y, near.Z) / near.W;
        var b = new Vector3(far.X, far.Y, far.Z) / far.W;
        return (a, Vector3.Normalize(b - a));
    }

    /// <summary>The nearest node whose model's box the ray hits.</summary>
    public Node? Pick(EditorScene scene, (Vector3 Origin, Vector3 Direction) ray)
    {
        Node? best = null;
        var bestT = float.MaxValue;
        foreach (var o in scene.Objects)
        {
            if (Model(o.Model) is not { } m || !Matrix4x4.Invert(o.Transform, out var inv))
                continue;
            var origin = Vector3.Transform(ray.Origin, inv);
            var dir = Vector3.TransformNormal(ray.Direction, inv);
            if (HitBox(origin, dir, m.BoundsMin, m.BoundsMax) is { } local)
            {
                var t = Vector3.Distance(ray.Origin, Vector3.Transform(origin + dir * local, o.Transform));
                if (t < bestT)
                    (best, bestT) = (o, t);
            }
        }
        return best;
    }

    static float? HitBox(Vector3 o, Vector3 d, Vector3 min, Vector3 max)
    {
        float tmin = 0, tmax = float.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var (oi, di, lo, hi) = i switch { 0 => (o.X, d.X, min.X, max.X), 1 => (o.Y, d.Y, min.Y, max.Y), _ => (o.Z, d.Z, min.Z, max.Z) };
            if (MathF.Abs(di) < 1e-8f)
            {
                if (oi < lo || oi > hi)
                    return null;
                continue;
            }
            var t1 = (lo - oi) / di;
            var t2 = (hi - oi) / di;
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1);
            tmax = MathF.Min(tmax, t2);
            if (tmin > tmax)
                return null;
        }
        return tmin;
    }

    /// <summary>Points the camera at a node, at a distance that fits it.</summary>
    public void Frame(Node o)
    {
        var (centre, size) = _scene.Bounds(o);
        Camera.Target = centre;
        Camera.Distance = MathF.Max(1.5f, size * 1.6f);
    }

    public void Dispose()
    {
        _gpu.WaitIdle();
        _view?.Dispose();
        Output?.Dispose();
        _scene.Dispose();
        _gpu.Device.DestroySampler(Sampler, null);
    }
}
