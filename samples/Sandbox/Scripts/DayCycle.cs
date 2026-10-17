using Trinity;
using Trinity.Scenes;

/// <summary>Turns the sun across the sky: the sky, its light and the shadows follow.</summary>
public sealed class DayCycle : Script
{
    /// <summary>Seconds for a full day.</summary>
    public float DayLength = 60;
    public float MaxElevation = 60;

    public override void Update(in FrameTime time)
    {
        var day = (float)(time.Total / DayLength % 1.0);
        Scene.Environment.SunAzimuth = 90 + day * 180;
        Scene.Environment.SunElevation = MathF.Sin(day * MathF.PI) * MaxElevation - 2;
    }
}
