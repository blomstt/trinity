using System.Numerics;

namespace Trinity.Graphics;

/// <summary>
/// A perspective camera in Trinity's conventions: right-handed, Y up, depth 0..1, and Vulkan's
/// downward clip-space Y. Matrices are .NET row-vector style (point * View * Projection), which a
/// GLSL <c>mat4</c> reads correctly as <c>viewProj * vec4(p, 1)</c>.
/// </summary>
public struct Camera
{
    public Vector3 Position;
    public Vector3 Target;
    public float FieldOfView; // vertical, radians
    public float Near, Far;

    public Camera()
    {
        FieldOfView = 0.9f;
        Near = 0.3f;
        Far = 4000f;
    }

    public readonly Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);

    public readonly Matrix4x4 Projection(float aspect)
    {
        var p = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect, Near, Far);
        p.M22 = -p.M22; // Vulkan's clip space has Y pointing down
        return p;
    }

    public readonly Matrix4x4 ViewProjection(float aspect) => View * Projection(aspect);
}

/// <summary>An RTS-style camera orbiting a point on the ground.</summary>
public struct OrbitCamera
{
    public Vector3 Target;
    public float Yaw, Pitch, Distance;

    public readonly Vector3 Eye =>
        Target + new Vector3(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch)) * Distance;

    /// <summary>Ground direction away from the camera, and to its right.</summary>
    public readonly (Vector3 Forward, Vector3 Right) GroundAxes
    {
        get
        {
            var back = new Vector3(MathF.Sin(Yaw), 0, MathF.Cos(Yaw));
            return (-back, new Vector3(back.Z, 0, -back.X)); // right = forward x up
        }
    }

    public readonly Camera ToCamera() => new() { Position = Eye, Target = Target };
}

/// <summary>The six planes of a view frustum, normals pointing inward, for culling.</summary>
public readonly struct Frustum
{
    readonly Vector4 _left, _right, _bottom, _top, _near, _far;

    /// <summary>From a view-projection matrix in Trinity's conventions (row-vector, depth 0..1).</summary>
    public Frustum(Matrix4x4 m)
    {
        var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        _left = Normalize(c4 + c1);
        _right = Normalize(c4 - c1);
        _bottom = Normalize(c4 + c2);
        _top = Normalize(c4 - c2);
        _near = Normalize(c3);
        _far = Normalize(c4 - c3);
    }

    static Vector4 Normalize(Vector4 p) => p / new Vector3(p.X, p.Y, p.Z).Length();

    /// <summary>True if any part of the sphere is inside.</summary>
    public bool Contains(Vector3 centre, float radius) =>
        D(_left, centre) > -radius && D(_right, centre) > -radius && D(_bottom, centre) > -radius
        && D(_top, centre) > -radius && D(_near, centre) > -radius && D(_far, centre) > -radius;

    static float D(Vector4 p, Vector3 c) => p.X * c.X + p.Y * c.Y + p.Z * c.Z + p.W;
}
