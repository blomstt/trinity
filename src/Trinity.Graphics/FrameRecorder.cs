using System.Diagnostics;
using Brutal.VulkanApi;

namespace Trinity.Graphics;

/// <summary>
/// Records rendered frames to a video with ffmpeg (H.264). Use with an off-screen
/// <see cref="GraphicsDevice"/> and a fixed time step so the video is smooth whatever the real
/// frame rate. Each <see cref="Capture"/> waits for the GPU, so recording is slower than real time.
/// </summary>
public sealed class FrameRecorder : IDisposable
{
    readonly GraphicsDevice _gpu;
    readonly Process _ffmpeg;
    public readonly GpuBuffer Buffer;
    public int Frames { get; private set; }
    public string Path { get; }

    public FrameRecorder(GraphicsDevice gpu, string path, int fps = 60, int crf = 18)
    {
        _gpu = gpu;
        Path = path;
        var e = gpu.Extent;
        Buffer = new GpuBuffer(gpu, (long)e.Width * e.Height * 4, VkBufferUsageFlags.TransferDstBit, readback: true);
        var pixels = gpu.ColorFormat is VkFormat.B8G8R8A8SRGB or VkFormat.B8G8R8A8UNorm ? "bgra" : "rgba";
        _ffmpeg = Process.Start(
            new ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", pixels, "-s", $"{e.Width}x{e.Height}",
                    "-r", $"{fps}", "-i", "-", "-c:v", "libx264", "-preset", "slow", "-crf", $"{crf}",
                    "-pix_fmt", "yuv420p", "-movflags", "+faststart", path,
                },
                RedirectStandardInput = true,
                UseShellExecute = false,
            }
        ) ?? throw new Exception("Couldn't start ffmpeg");
        Log.Info("recorder", $"recording {e.Width}x{e.Height} at {fps} fps to {path}");
    }

    /// <summary>Call after <see cref="GraphicsDevice.EndFrame"/> was given <see cref="Buffer"/>.</summary>
    public void Capture()
    {
        _gpu.WaitIdle();
        _ffmpeg.StandardInput.BaseStream.Write(Buffer.Bytes);
        Frames++;
    }

    public void Dispose()
    {
        _ffmpeg.StandardInput.Close();
        _ffmpeg.WaitForExit();
        Buffer.Dispose();
        Log.Info("recorder", $"wrote {Frames} frames to {Path}");
    }
}
