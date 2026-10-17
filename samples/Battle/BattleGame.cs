using System.Numerics;
using Brutal.GlfwApi;
using Brutal.VulkanApi;
using Trinity;
using Trinity.Animation;
using Trinity.Graphics;

namespace BattleSample;

/// <summary>Two armies of spearmen fighting: the BRUTAL battle prototype as a Trinity game.</summary>
public sealed class BattleGame(BattleOptions options) : Game
{
    const float DetailDistance = 60f;

    BattleSim _sim = null!;
    CrowdRenderer _crowd = null!;
    OrbitCamera _camera = new() { Yaw = 0.9f, Pitch = 0.32f, Distance = 55f };
    FrameConstants _constants;
    bool _paused;

    public override void Load()
    {
        var model = new SkinnedModel(Path.Combine(AppContext.BaseDirectory, "Assets", "soldier.glb"));
        _sim = new BattleSim(Jobs, options.PerSide, options.Gap);
        _crowd = new CrowdRenderer(Gpu, Shaders, model, _sim.Count);
        Jobs.SingleThreaded = options.SingleThreaded;
        if (options.WideView)
            _camera = _camera with { Yaw = 0.6f, Pitch = 0.62f, Distance = 240f };
        Log.Info("battle", $"{_sim.Count:N0} soldiers, {model.JointCount} bones, {_crowd.TrianglesNear} / {_crowd.TrianglesFar} triangles near / far");

        // Keyboard and mouse, and a gamepad: left stick pans, right stick turns and tilts, triggers zoom
        Input.Bind("forward", GlfwKey.W, GlfwKey.Up, Binding.Axis(GlfwGamepadAxis.LeftY, positive: false));
        Input.Bind("back", GlfwKey.S, GlfwKey.Down, Binding.Axis(GlfwGamepadAxis.LeftY));
        Input.Bind("left", GlfwKey.A, GlfwKey.Left, Binding.Axis(GlfwGamepadAxis.LeftX, positive: false));
        Input.Bind("right", GlfwKey.D, GlfwKey.Right, Binding.Axis(GlfwGamepadAxis.LeftX));
        Input.Bind("turn_left", GlfwKey.Q, Binding.Axis(GlfwGamepadAxis.RightX, positive: false));
        Input.Bind("turn_right", GlfwKey.E, Binding.Axis(GlfwGamepadAxis.RightX));
        Input.Bind("tilt_up", GlfwKey.R, Binding.Axis(GlfwGamepadAxis.RightY, positive: false));
        Input.Bind("tilt_down", GlfwKey.F, Binding.Axis(GlfwGamepadAxis.RightY));
        Input.Bind("zoom_in", GlfwKey.Z, Binding.Axis(GlfwGamepadAxis.RightTrigger));
        Input.Bind("zoom_out", GlfwKey.X, Binding.Axis(GlfwGamepadAxis.LeftTrigger));
        Input.Bind("pause", GlfwKey.Space, GlfwGamepadButton.A);
        Input.Bind("restart", GlfwKey.Enter, GlfwGamepadButton.Start);
        Input.Bind("toggle_jobs", GlfwKey.J, GlfwGamepadButton.Y);
        Input.Bind("more", GlfwKey.Equal, GlfwKey.KpAdd, GlfwGamepadButton.RightBumper);
        Input.Bind("fewer", GlfwKey.Minus, GlfwKey.KpSubtract, GlfwGamepadButton.LeftBumper);
        Input.Bind("quit", GlfwKey.Escape, GlfwGamepadButton.Back);

        if (Engine.Recording && options.Warmup > 0)
        {
            // Let the armies close in before the camera rolls
            var step = 1f / Engine.Options.FixedRate;
            for (var t = 0f; t < options.Warmup; t += step)
                _sim.Simulate(step);
        }
    }

    public override void FixedUpdate(in FrameTime time)
    {
        if (!_paused)
            _sim.Simulate(time.Delta);
    }

    public override void Update(in FrameTime time)
    {
        var dt = time.Delta;
        if (Engine.Recording)
            FlyRecordingCamera(time);
        else
            Controls(dt);

        var camera = _camera.ToCamera();
        var viewProj = camera.ViewProjection(Gpu.Extent.Width / (float)Gpu.Extent.Height);
        _sim.Gather(_crowd.Instances(Gpu.FrameIndex)[.._sim.Count], new Frustum(viewProj), camera.Position, DetailDistance, _paused ? 1 : time.Alpha);
        _constants = new FrameConstants
        {
            ViewProj = viewProj,
            CameraPos = new Vector4(camera.Position, 1),
            SunDir = new Vector4(Vector3.Normalize(new Vector3(-0.4f, -0.7f, -0.35f)), 0),
            Params = new Vector4((float)time.Total, Engine.Recording ? 0.0009f : 0.0014f, 0, 0),
        };
    }

    void Controls(float dt)
    {
        if (Input.Pressed("quit"))
            Window.ShouldClose = true;
        var (forward, right) = _camera.GroundAxes;
        var pan = _camera.Distance * 0.9f * dt;
        _camera.Target += forward * Input.Axis("back", "forward") * pan + right * Input.Axis("left", "right") * pan;
        _camera.Yaw += Input.Axis("turn_right", "turn_left") * 1.4f * dt;
        _camera.Pitch = Math.Clamp(_camera.Pitch + Input.Axis("tilt_down", "tilt_up") * 0.8f * dt, 0.05f, 1.45f);
        var zoom = (float)Input.Scroll.Y + Input.Axis("zoom_out", "zoom_in") * 6f * dt;
        _camera.Distance = Math.Clamp(_camera.Distance * MathF.Pow(0.88f, zoom), 4f, 900f);
        if (Input.Pressed("pause")) _paused = !_paused;
        if (Input.Pressed("restart")) _sim.Spawn(_sim.Count / 2);
        if (Input.Pressed("toggle_jobs")) Jobs.SingleThreaded = !Jobs.SingleThreaded;
        if (Input.Pressed("more")) Resize(Math.Min(_sim.Count, 128_000));
        if (Input.Pressed("fewer")) Resize(Math.Max(_sim.Count / 4, 50));
    }

    void FlyRecordingCamera(in FrameTime time)
    {
        // Wide shot of the whole battle, sweeping down to end low along the front line
        var u = (float)Math.Clamp(time.Total / Engine.Options.RecordSeconds, 0, 1);
        var ease = u * u * (3 - 2 * u);
        _camera = new OrbitCamera
        {
            Yaw = float.Lerp(0.55f, 1.42f, ease),
            Pitch = float.Lerp(0.62f, 0.19f, ease),
            Distance = float.Lerp(240f, 24f, ease),
        };
    }

    void Resize(int perSide)
    {
        _crowd.EnsureCapacity(perSide * 2);
        _sim.Spawn(perSide);
    }

    public override void Render(CommandBuffer cmd, in FrameTime time) =>
        _crowd.Draw(cmd, Gpu.FrameIndex, _sim.NearCount, _sim.FarCount, _constants);

    public override string Status =>
        $"{_sim.Count:N0} soldiers, blue {_sim.AliveBlue:N0} / red {_sim.AliveRed:N0}, drawn {_sim.NearCount:N0} + {_sim.FarCount:N0} | "
        + $"sim {_sim.LastSimMs:F2} ms ({(Jobs.SingleThreaded ? "1 thread" : $"{Jobs.ThreadCount} threads")})"
        + (_paused ? " | paused" : "");

    public override void Dispose() => _crowd?.Dispose();
}
