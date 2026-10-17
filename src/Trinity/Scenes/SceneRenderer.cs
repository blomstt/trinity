using System.Numerics;
using Brutal.VulkanApi;
using Trinity.Animation;
using Trinity.Graphics;
using Trinity.Rendering;

namespace Trinity.Scenes;

/// <summary>
/// Draws scene nodes with Trinity's <see cref="Rendering.Renderer"/>: loads each node's model once,
/// turns nodes into a draw list (transform, animation clip and time) and renders it. Used by
/// <see cref="SceneGame"/> and the editor's viewport alike.
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    readonly GraphicsDevice _gpu;
    readonly string _root;
    readonly Dictionary<string, Mesh?> _meshes = [];
    readonly Dictionary<string, Task<SkinnedModel>> _loading = [];

    /// <summary>Raised on a worker thread when a model or texture is ready to finish loading (wake an idle loop).</summary>
    public event Action? AssetReady;

    /// <summary>Models or textures still loading.</summary>
    public bool Loading => _loading.Count > 0 || Renderer.Materials.TexturesLoading > 0;
    readonly DrawList _draws = new();
    public readonly Renderer Renderer;

    /// <summary>Triangles the camera drew last frame, after culling and level of detail.</summary>
    public long TrianglesDrawn => Renderer.Stats.Triangles;

    /// <param name="root">The folder model paths are relative to.</param>
    public SceneRenderer(GraphicsDevice gpu, ShaderLibrary shaders, string root)
    {
        _gpu = gpu;
        _root = root;
        Renderer = new Renderer(gpu, shaders);
        Renderer.Materials.TextureReady += () => AssetReady?.Invoke();
    }

    /// <summary>
    /// The model at a root-relative path, or null while it loads (on a worker thread, started by the
    /// first call) or if it can't be loaded. Its textures keep streaming in after it appears.
    /// </summary>
    public Mesh? Model(string relativePath)
    {
        if (_meshes.TryGetValue(relativePath, out var mesh))
            return mesh;
        if (string.IsNullOrEmpty(relativePath))
            return _meshes[relativePath] = null;
        if (!_loading.TryGetValue(relativePath, out var task))
        {
            var path = Path.GetFullPath(Path.Combine(_root, relativePath));
            _loading[relativePath] = task = Task.Run(() => new SkinnedModel(path));
            task.ContinueWith(_ => AssetReady?.Invoke());
            return null;
        }
        if (!task.IsCompleted)
            return null;
        _loading.Remove(relativePath);
        try
        {
            mesh = Renderer.CreateMesh(task.Result);
            Log.Info("scene", $"loaded {relativePath}: {mesh.Triangles:N0} triangles, {mesh.Model.Materials.Count} materials, {mesh.Model.Textures.Count} textures, {(mesh.Model.IsRigged ? $"{mesh.Model.JointCount} bones, clips {string.Join(", ", mesh.ClipNames)}" : "static")}");
        }
        catch (Exception e)
        {
            Log.Error("scene", $"couldn't load {relativePath}: {(e as AggregateException)?.InnerException?.Message ?? e.Message}");
            mesh = null;
        }
        _meshes[relativePath] = mesh;
        return mesh;
    }

    /// <summary>Loads every model the nodes use, and their textures, before returning (a load screen's worth of work).</summary>
    public void LoadAll(IEnumerable<Node> nodes)
    {
        var paths = nodes.Select(n => n.Model).Distinct().ToList();
        foreach (var p in paths)
            Model(p);
        foreach (var task in _loading.Values.ToList())
            task.ContinueWith(_ => { }).Wait();
        foreach (var p in paths)
            Model(p);
        Renderer.Materials.WaitForTextures();
    }

    /// <summary>Renders the nodes into <paramref name="view"/> and tone-maps into <paramref name="output"/>. <paramref name="highlighted"/> gets a rim.</summary>
    public void Render(CommandBuffer cmd, RenderView view, IReadOnlyList<Node> nodes, in Camera camera, RenderOutput output, double time, Node? highlighted = null, Vector3 gridCentre = default)
    {
        _draws.Clear();
        foreach (var o in nodes)
        {
            if (Model(o.Model) is not { } mesh)
                continue;
            var clip = mesh.Clip(o.Clip);
            var duration = mesh.Model.Clips.Count == 0 ? 0 : mesh.Model.Clips[clip].Duration;
            var loop = o.Clip != "death";
            var t = duration <= 0 ? 0 : loop ? o.Time % duration : Math.Min(o.Time, duration);
            _draws.Add(mesh, o.Transform, new Pose(clip, t, loop), o == highlighted);
        }
        Renderer.Render(cmd, view, camera, _draws, output, time, gridCentre);
    }

    /// <summary>The centre and size of a node's model in the world, for framing it with a camera.</summary>
    public (Vector3 Centre, float Size) Bounds(Node o)
    {
        if (Model(o.Model) is not { } m || m.Model.Vertices.Length == 0)
            return (o.Position, 2f * o.Scale);
        return (Vector3.Transform((m.BoundsMin + m.BoundsMax) / 2, o.Transform), (m.BoundsMax - m.BoundsMin).Length() * o.Scale);
    }

    public void Dispose()
    {
        _gpu.WaitIdle();
        foreach (var m in _meshes.Values)
            m?.Dispose();
        Renderer.Dispose();
    }
}
