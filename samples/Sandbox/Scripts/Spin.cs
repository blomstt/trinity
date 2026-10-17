using System.Numerics;
using Trinity;
using Trinity.Scenes;

/// <summary>Turns its node around the vertical axis.</summary>
public sealed class Spin : Script
{
    public float DegreesPerSecond = 90;

    public override void Update(in FrameTime time) =>
        Node.Rotation += new Vector3(0, DegreesPerSecond * time.Delta, 0);
}
