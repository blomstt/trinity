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


    readonly HashSet<EntityId> _noHighlight = [];

    /// <summary>
    /// Renders the world's mesh renderers into <paramref name="view"/> and tone-maps into <paramref name="output"/>:
    /// each at its cached world transform, posed by its <see cref="Animator"/> if it has one, skipping hidden
    /// nodes. <paramref name="highlighted"/> (the editor's selection) get a rim.
    /// </summary>
    public void Render(CommandBuffer cmd, RenderView view, World world, in Camera camera, RenderOutput output, double time,
        IReadOnlySet<EntityId>? highlighted = null, Vector3 gridCentre = default)
    {
        _draws.Clear();
        highlighted ??= _noHighlight;
        Collect(world, _collected);
        foreach (var d in _collected)
        {
            if (Model(d.Mesh) is not { } mesh)
                continue;
            var pose = default(Pose);
            if (d.Clip != null && mesh.Model.Clips.Count > 0)
            {
                var clip = mesh.Clip(d.Clip);
                var duration = mesh.Model.Clips[clip].Duration;
                var loop = !d.Once;
                var t = duration <= 0 ? 0 : loop ? d.Time % duration : Math.Min(d.Time, duration);
                pose = new Pose(clip, t, loop);
            }
            _draws.Add(mesh, d.World, pose, highlighted.Contains(d.Entity), d.NoShadows);
        }
        Renderer.Render(cmd, view, camera, _draws, output, time, gridCentre);
    }

    readonly List<SceneDraw> _collected = [];

    /// <summary>One visible mesh renderer, as the renderer will get it.</summary>
    public readonly record struct SceneDraw(EntityId Entity, string Mesh, Matrix4x4 World, string? Clip, float Time, bool Once, bool NoShadows);

    /// <summary>
    /// The world's visible mesh renderers with their cached world matrices and animation state. No GPU
    /// involved, so this is the step tests check; the renderer then resolves meshes by path, so every
    /// renderer naming the same model shares one GPU mesh.
    /// </summary>
    public static void Collect(World world, List<SceneDraw> into)
    {
        into.Clear();
        var renderers = world.Store<MeshRenderer>();
        var animators = world.Store<Animator>();
        for (var i = 0; i < renderers.Count; i++)
        {
            var e = renderers.Entities[i];
            ref var mr = ref renderers.Components[i];
            if (string.IsNullOrEmpty(mr.Mesh) || world.HiddenInHierarchy(e))
                continue;
            ref var animator = ref animators.TryGet(e, out var animated);
            into.Add(new SceneDraw(e, mr.Mesh, world.WorldMatrix(e), animated ? animator.Clip ?? "" : null, animated ? animator.Time : 0, animated && animator.Once, mr.NoShadows));
        }
    }

    /// <summary>Loads every model the world's mesh renderers use, and their textures, before returning (a load screen's worth of work).</summary>
    public void LoadAll(World world)
    {
        var paths = new HashSet<string>();
        foreach (ref var mr in world.Store<MeshRenderer>().Components)
            if (!string.IsNullOrEmpty(mr.Mesh))
                paths.Add(mr.Mesh);
        foreach (var p in paths)
            Model(p);
        foreach (var task in _loading.Values.ToList())
            task.ContinueWith(_ => { }).Wait();
        foreach (var p in paths)
            Model(p);
        Renderer.Materials.WaitForTextures();
    }

    /// <summary>The centre and size of a node's model in the world, for framing it with a camera.</summary>
    public (Vector3 Centre, float Size) Bounds(World world, EntityId e)
    {
        var m = world.WorldMatrix(e);
        var scale = MathF.Max(new Vector3(m.M11, m.M12, m.M13).Length(), MathF.Max(new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length()));
        if (!world.Has<MeshRenderer>(e) || Model(world.Get<MeshRenderer>(e).Mesh) is not { } mesh || mesh.Model.Vertices.Length == 0)
            return (m.Translation, 2f * scale);
        return (Vector3.Transform((mesh.BoundsMin + mesh.BoundsMax) / 2, m), (mesh.BoundsMax - mesh.BoundsMin).Length() * scale);
    }

    public void Dispose()
    {
        _gpu.WaitIdle();
        foreach (var m in _meshes.Values)
            m?.Dispose();
        Renderer.Dispose();
    }
}
