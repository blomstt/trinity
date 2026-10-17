using System.Runtime.InteropServices;
using Brutal;
using Brutal.GlfwApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Trinity.Graphics;

namespace Trinity;

/// <summary>
/// The BRUTAL logo shown before gameplay, as the BRUTAL License v1.0 section 2.2 requires: the logo
/// unaltered except for scaling, on black, at full opacity, long enough to be legible.
/// It can be skipped after <see cref="MinimumSeconds"/> and ends by itself after <see cref="Seconds"/>.
/// </summary>
public sealed unsafe class Splash : IDisposable
{
    public const double Seconds = 2.5;
    public const double MinimumSeconds = 1.5;
    const float WidthOfScreen = 0.42f;

    readonly GraphicsDevice _gpu;
    readonly Texture _logo;
    readonly TextureBinding _binding;
    readonly VkPipelineLayout _layout;
    readonly VkPipeline _pipeline;

    /// <summary>
    /// Shows the logo until <see cref="Seconds"/> pass, or a key or click after <see cref="MinimumSeconds"/>.
    /// With a recorder, frames go into the video and time follows the recording.
    /// </summary>
    public static void Show(Window window, GraphicsDevice gpu, ShaderLibrary shaders, FrameRecorder? recorder = null, int recordFps = 60)
    {
        using var splash = new Splash(gpu, shaders);
        var skip = false;
        GlfwKeyCallback onKey = (_, _, _, action, _) => skip |= action == GlfwKeyAction.Press;
        GlfwMouseButtonCallback onClick = (_, _, action, _) => skip |= action == GlfwButtonAction.Press;
        window.Native.OnKey += onKey;
        window.Native.OnMouseButton += onClick;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!window.ShouldClose)
        {
            Window.PollEvents();
            var t = recorder != null ? recorder.Frames / (double)recordFps : clock.Elapsed.TotalSeconds;
            if (t >= Seconds || (t >= MinimumSeconds && skip))
                break;
            skip = false;
            if (!gpu.BeginFrame(out var cmd))
                continue;
            gpu.BeginRendering(cmd, new VkClearColorValue());
            splash.Draw(cmd);
            gpu.EndFrame(cmd, recorder?.Buffer);
            recorder?.Capture();
        }
        window.Native.OnKey -= onKey;
        window.Native.OnMouseButton -= onClick;
        gpu.WaitIdle();
    }

    Splash(GraphicsDevice gpu, ShaderLibrary shaders)
    {
        _gpu = gpu;
        _logo = Texture.Load(gpu, Path.Combine(AppContext.BaseDirectory, "Trinity", "brutal-logo-white.png"));
        _binding = new TextureBinding(gpu, _logo);
        var push = new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit, Size = (ByteSize32)16 };
        _layout = gpu.Device.CreatePipelineLayout([_binding.SetLayout], [push], null);
        _pipeline = Pipelines.Create(gpu, shaders, new GraphicsPipelineDesc
        {
            VertexShader = "splash.vert",
            FragmentShader = "splash.frag",
            Layout = _layout,
            Cull = Cull.None,
            DepthTest = false,
            DepthWrite = false,
            Blend = Blend.Alpha,
        });
    }

    void Draw(CommandBuffer cmd)
    {
        // Centred, a fixed share of the screen width, aspect ratio kept
        var e = _gpu.Extent;
        var halfWidth = WidthOfScreen;
        var halfHeight = WidthOfScreen * _logo.Height / _logo.Width * e.Width / e.Height;
        Span<float> rect = [0f, 0f, halfWidth, halfHeight];
        cmd.PushConstants(_layout, VkShaderStageFlags.VertexBit, 0, MemoryMarshal.AsBytes(rect));
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_binding.Set], []);
        cmd.Draw(6, 1, 0, 0);
    }

    public void Dispose()
    {
        _gpu.Device.DestroyPipeline(_pipeline, null);
        _gpu.Device.DestroyPipelineLayout(_layout, null);
        _binding.Dispose();
        _logo.Dispose();
    }
}
