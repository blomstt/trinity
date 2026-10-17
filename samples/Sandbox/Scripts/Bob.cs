using System.Numerics;
using Trinity;
using Trinity.Scenes;

/// <summary>Floats up and down.</summary>
public sealed class Bob : Script
{
    public float Height = 0.5f;
    public float CyclesPerSecond = 0.5f;

    Vector3 _start;

    public override void Ready() => _start = Node.Position;

    public override void Update(in FrameTime time) =>
        Node.Position = _start + new Vector3(0, Height * (1 + MathF.Sin((float)time.Total * CyclesPerSecond * MathF.Tau)), 0);
}
