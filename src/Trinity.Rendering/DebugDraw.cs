using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Trinity;

/// <summary>
/// Lines drawn over the frame for debugging: <c>Debug.Line(a, b, color)</c>, boxes, spheres. Drawn
/// after tone mapping, tested against the scene's depth (or on top), for one frame or for some
/// seconds. Calls compile away unless <c>TRINITY_DEBUG_DRAW</c> is defined, which Trinity defines for
/// every build except Release, so shipping builds carry none of it.
/// </summary>
public static class Debug
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Vertex
    {
        public Vector3 Position;
        public uint Color; // RGBA8, sRGB
    }

    record struct Timed(Vertex A, Vertex B, bool OnTop, double Until);

    static readonly Lock Gate = new();
    static readonly List<Vertex> Tested = [], OnTop = [];
    static readonly List<Timed> Lasting = [];
    static double _now;

    public static readonly Vector4 Red = new(0.9f, 0.25f, 0.2f, 1), Green = new(0.35f, 0.85f, 0.3f, 1), Blue = new(0.25f, 0.45f, 0.95f, 1);
    public static readonly Vector4 Yellow = new(0.95f, 0.85f, 0.25f, 1), White = Vector4.One, Bronze = new(0.88f, 0.66f, 0.36f, 1);

    static uint Pack(Vector4 c)
    {
        static uint B(float v) => (uint)Math.Clamp(MathF.Round(v * 255), 0, 255);
        return B(c.X) | B(c.Y) << 8 | B(c.Z) << 16 | B(c.W) << 24;
    }

    /// <param name="onTop">Draw over everything instead of hiding behind the scene.</param>
    /// <param name="seconds">Keep it this long; 0 for this frame only.</param>
    [Conditional("TRINITY_DEBUG_DRAW")]
    public static void Line(Vector3 a, Vector3 b, Vector4 color, bool onTop = false, float seconds = 0) => Add(a, b, color, onTop, seconds);

    // The shapes add their lines through this, not Line: Conditional applies at every call site,
    // including inside this class, which is compiled without the symbol in Release
    static void Add(Vector3 a, Vector3 b, Vector4 color, bool onTop, float seconds)
    {
        var va = new Vertex { Position = a, Color = Pack(color) };
        var vb = new Vertex { Position = b, Color = va.Color };
        lock (Gate)
        {
            if (seconds > 0)
                Lasting.Add(new Timed(va, vb, onTop, _now + seconds));
            var list = onTop ? OnTop : Tested;
            list.Add(va);
            list.Add(vb);
        }
    }

    /// <summary>An axis-aligned box.</summary>
    [Conditional("TRINITY_DEBUG_DRAW")]
    public static void Box(Vector3 min, Vector3 max, Vector4 color, bool onTop = false, float seconds = 0) =>
        AddBox(Matrix4x4.CreateScale(max - min) * Matrix4x4.CreateTranslation((min + max) / 2), color, onTop, seconds);

    /// <summary>A unit cube (-0.5..0.5) placed by <paramref name="transform"/>: any box, rotated and scaled.</summary>
    [Conditional("TRINITY_DEBUG_DRAW")]
    public static void Box(Matrix4x4 transform, Vector4 color, bool onTop = false, float seconds = 0) => AddBox(transform, color, onTop, seconds);

    static void AddBox(Matrix4x4 transform, Vector4 color, bool onTop, float seconds)
    {
        Span<Vector3> c = stackalloc Vector3[8];
        for (var i = 0; i < 8; i++)
            c[i] = Vector3.Transform(new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f), transform);
        ReadOnlySpan<int> edges = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];
        for (var e = 0; e < edges.Length; e += 2)
            Add(c[edges[e]], c[edges[e + 1]], color, onTop, seconds);
    }

    /// <summary>Three circles around the centre, one per axis.</summary>
    [Conditional("TRINITY_DEBUG_DRAW")]
    public static void Sphere(Vector3 centre, float radius, Vector4 color, bool onTop = false, float seconds = 0, int segments = 32)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            var prev = Vector3.Zero;
            for (var s = 0; s <= segments; s++)
            {
                var a = s * MathF.Tau / segments;
                var (x, y) = (MathF.Cos(a) * radius, MathF.Sin(a) * radius);
                var p = centre + axis switch { 0 => new Vector3(0, x, y), 1 => new Vector3(x, 0, y), _ => new Vector3(x, y, 0) };
                if (s > 0)
                    Add(prev, p, color, onTop, seconds);
                prev = p;
            }
        }
    }

    /// <summary>A line with a small head at <paramref name="to"/>.</summary>
    [Conditional("TRINITY_DEBUG_DRAW")]
    public static void Arrow(Vector3 from, Vector3 to, Vector4 color, bool onTop = false, float seconds = 0)
    {
        Add(from, to, color, onTop, seconds);
        var d = to - from;
        var length = d.Length();
        if (length < 1e-5f)
            return;
        d /= length;
        var side = Vector3.Normalize(Vector3.Cross(d, MathF.Abs(d.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX)) * length * 0.06f;
        var up = Vector3.Normalize(Vector3.Cross(side, d)) * length * 0.06f;
        var back = to - d * length * 0.15f;
        Add(to, back + side, color, onTop, seconds);
        Add(to, back - side, color, onTop, seconds);
        Add(to, back + up, color, onTop, seconds);
        Add(to, back - up, color, onTop, seconds);
    }

    /// <summary>The lines for this frame (depth-tested, then on top), and forgets one-frame lines. Called by the renderer once per frame.</summary>
    internal static (Vertex[] Tested, Vertex[] OnTop) TakeFrame(double now)
    {
        lock (Gate)
        {
            _now = now;
            var result = (Tested.ToArray(), OnTop.ToArray());
            Tested.Clear();
            OnTop.Clear();
            // Lines that last stay for the next frame too, until their time is up
            Lasting.RemoveAll(t => t.Until <= now);
            foreach (var t in Lasting)
            {
                var list = t.OnTop ? OnTop : Tested;
                list.Add(t.A);
                list.Add(t.B);
            }
            return result;
        }
    }
}
