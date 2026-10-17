using Trinity;
using Trinity.Scenes;

/// <summary>Slowly circles the scene's camera around its target. Needs no model: attach it to an empty node.</summary>
public sealed class TurnTable : Script
{
    public float DegreesPerSecond = 10;

    public override void Update(in FrameTime time) =>
        Scene.Camera.Yaw += float.DegreesToRadians(DegreesPerSecond) * time.Delta;
}
