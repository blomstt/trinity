using System.Numerics;
using Trinity.Graphics;

namespace Trinity.Rendering;

/// <summary>Fits cascaded shadow maps for the sun around a camera's view.</summary>
static class Cascades
{
    /// <summary>How far behind each cascade (towards the sun) casters are still caught, in metres.</summary>
    const float CasterReach = 300;

    /// <summary>
    /// Splits the view up to <paramref name="distance"/> into four cascades (between even and
    /// logarithmic by <paramref name="lambda"/>) and fits each one's shadow map around its slice of the
    /// view. Each cascade is a sphere around its slice, so its size never changes as the camera turns,
    /// and its centre snaps to whole shadow-map texels, so shadow edges don't crawl as the camera moves.
    /// </summary>
    /// <param name="matrices">World to shadow map: xy in 0..1, depth reversed (1 nearest the sun).</param>
    /// <param name="splits">View distance where each cascade ends.</param>
    /// <param name="texel">World size of one shadow-map texel, per cascade.</param>
    public static void Fit(in Camera camera, float aspect, Vector3 sunDir, float distance, float lambda, int resolution,
        Span<Matrix4x4> matrices, Span<float> splits, Span<float> texel)
    {
        var near = camera.Near;
        var far = MathF.Max(distance, near * 2);
        var forward = Vector3.Normalize(camera.Target - camera.Position);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        var tanY = MathF.Tan(camera.FieldOfView / 2);
        var tanX = tanY * aspect;

        // The sun's view: looking along the light, with any stable up
        var lightUp = MathF.Abs(sunDir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var light = Matrix4x4.CreateLookAt(Vector3.Zero, sunDir, lightUp);

        var start = near;
        for (var c = 0; c < 4; c++)
        {
            var p = (c + 1) / 4f;
            var log = near * MathF.Pow(far / near, p);
            var uniform = near + (far - near) * p;
            var end = lambda * log + (1 - lambda) * uniform;
            splits[c] = end;

            // A sphere around the slice of the view between start and end: its centre lies on the view
            // axis where it's equidistant from the near and far corners
            var k = tanX * tanX + tanY * tanY;
            var centreDistance = MathF.Min(end, (start + end) / 2 * (1 + k));
            var radius = MathF.Sqrt(MathF.Pow(end - centreDistance, 2) + end * end * k);
            radius = MathF.Ceiling(radius * 16) / 16; // tiny changes in size would make edges shimmer
            var centre = camera.Position + forward * centreDistance;

            // Snap the centre to the texel grid, in the light's space
            var size = 2 * radius;
            var texelSize = size / resolution;
            var lc = Vector3.Transform(centre, light);
            lc.X = MathF.Floor(lc.X / texelSize) * texelSize;
            lc.Y = MathF.Floor(lc.Y / texelSize) * texelSize;

            // Depth: from the slice's far side up to well past it towards the sun (view-space z grows towards the sun)
            var zFar = lc.Z - radius;
            var zNear = lc.Z + radius + CasterReach;
            var scale = new Matrix4x4(
                1 / size, 0, 0, 0,
                0, 1 / size, 0, 0,
                0, 0, 1 / (zNear - zFar), 0,
                (radius - lc.X) / size, (radius - lc.Y) / size, -zFar / (zNear - zFar), 1
            );
            matrices[c] = light * scale;
            texel[c] = texelSize;
            start = end;
        }
    }
}
