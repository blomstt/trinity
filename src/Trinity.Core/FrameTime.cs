namespace Trinity;

/// <summary>Timing for one frame.</summary>
/// <param name="Delta">Seconds since the previous frame (fixed when the engine runs a fixed step).</param>
/// <param name="Total">Seconds of game time since start.</param>
/// <param name="Frame">Frame number, from 0.</param>
/// <param name="RealDelta">Wall-clock seconds since the previous frame, for performance stats.</param>
/// <param name="Alpha">
/// In <c>Update</c>/<c>Render</c>: how far time has got between the last fixed step and the next (0..1),
/// for smoothing things simulated in <c>FixedUpdate</c>.
/// </param>
public readonly record struct FrameTime(float Delta, double Total, long Frame, float RealDelta, float Alpha = 0);
