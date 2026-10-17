using System.Numerics;
using Trinity;
using Trinity.Scenes;

/// <summary>Walks back and forth along X, turning at each end.</summary>
public sealed class Patrol : Script
{
    public float Distance = 4;
    public float WalkSpeed = 1.4f;

    Vector3 _start;
    float _along;
    int _direction = 1;

    public override void Ready()
    {
        _start = Node.Position;
        Node.Clip = "walk";
    }

    public override void FixedUpdate(in FrameTime time)
    {
        _along += _direction * WalkSpeed * time.Delta;
        if (_along > Distance || _along < 0)
        {
            _along = Math.Clamp(_along, 0, Distance);
            _direction = -_direction;
        }
        Node.Position = _start + new Vector3(_along, 0, 0);
        Node.Rotation = new Vector3(0, _direction > 0 ? 90 : -90, 0);
    }
}
