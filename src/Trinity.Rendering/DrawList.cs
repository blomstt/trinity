using System.Numerics;

namespace Trinity.Rendering;

/// <summary>Where an instance is in its animation.</summary>
/// <param name="Clip">Clip index in the mesh's bone table.</param>
/// <param name="Seconds">Time into the clip.</param>
/// <param name="Loop">Wrap at the end (otherwise hold the last frame).</param>
public readonly record struct Pose(int Clip = 0, float Seconds = 0, bool Loop = true);

/// <summary>One mesh to draw this frame.</summary>
public readonly record struct DrawItem(Mesh Mesh, Matrix4x4 World, Pose Pose, bool Highlight, bool NoShadows = false);

/// <summary>
/// What to draw this frame. Fill it, hand it to <see cref="Renderer.Render"/>, clear it next frame.
/// The renderer groups items by mesh and draws each group as one instanced draw.
/// </summary>
public sealed class DrawList
{
    readonly List<DrawItem> _items = [];

    public IReadOnlyList<DrawItem> Items => _items;
    public int Count => _items.Count;

    public void Add(Mesh mesh, in Matrix4x4 world, Pose pose = default, bool highlight = false, bool noShadows = false) =>
        _items.Add(new DrawItem(mesh, world, pose, highlight, noShadows));

    public void Clear() => _items.Clear();
}
