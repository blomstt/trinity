using Brutal.Numerics;
using Brutal.VulkanApi;
using Trinity.Graphics;

namespace Trinity;

/// <summary>
/// A game run by <see cref="Engine.Run"/>. Each frame: zero or more <see cref="FixedUpdate"/> steps at
/// <see cref="EngineOptions.FixedRate"/>, then <see cref="Update"/>, then <see cref="Render"/> inside the
/// frame's colour + depth pass. Per-frame GPU memory indexed by
/// <see cref="Graphics.GraphicsDevice.FrameIndex"/> is safe to write from <see cref="FixedUpdate"/> on.
/// </summary>
public abstract class Game : IDisposable
{
    public Engine Engine { get; internal set; } = null!;
    protected Window Window => Engine.Window;
    protected Input Input => Engine.Input;
    protected GraphicsDevice Gpu => Engine.Gpu;
    protected Jobs Jobs => Engine.Jobs;
    protected ShaderLibrary Shaders => Engine.Shaders;

    /// <summary>Colour the frame is cleared to before <see cref="Render"/>, in linear light.</summary>
    public virtual float4 ClearColor => new(0.149f, 0.187f, 0.231f, 1f);

    /// <summary>Called once, after the window and GPU exist.</summary>
    public virtual void Load() { }

    /// <summary>
    /// Runs at exactly <see cref="EngineOptions.FixedRate"/> steps a second, whatever the frame rate:
    /// the place for physics and simulation that must behave the same on every machine.
    /// </summary>
    public virtual void FixedUpdate(in FrameTime time) { }

    /// <summary>Runs once per frame, for input, cameras and anything that follows the display.</summary>
    public virtual void Update(in FrameTime time) { }

    /// <summary>The game's player settings file, also used by the engine for display and controls.</summary>
    protected Settings Settings => Engine.Settings;

    public virtual void Render(CommandBuffer cmd, in FrameTime time) { }

    /// <summary>Text for the window title and stats output, refreshed twice a second.</summary>
    public virtual string Status => "";

    public virtual void Dispose() { }
}
