using System.Diagnostics;
using Trinity.Graphics;

namespace Trinity.Editor;

/// <summary>Saves a captured frame as a PNG (for checking the editor without a person, via --screenshot).</summary>
static class Screenshots
{
    /// <summary>A buffer for one frame at the window's current size.</summary>
    public static GpuBuffer Buffer(GraphicsDevice gpu) =>
        new(gpu, (long)gpu.Extent.Width * gpu.Extent.Height * 4, Brutal.VulkanApi.VkBufferUsageFlags.TransferDstBit, readback: true);

    public static void Save(GraphicsDevice gpu, GpuBuffer pixels, string path)
    {
        gpu.WaitIdle();
        var e = gpu.Extent;
        var raw = path + ".bgra";
        File.WriteAllBytes(raw, pixels.Bytes[..(e.Width * e.Height * 4)].ToArray());
        var format = gpu.ColorFormat is Brutal.VulkanApi.VkFormat.B8G8R8A8SRGB or Brutal.VulkanApi.VkFormat.B8G8R8A8UNorm ? "bgra" : "rgba";
        Process.Start("magick", ["-size", $"{e.Width}x{e.Height}", "-depth", "8", $"{format}:{raw}", "-alpha", "off", path])!.WaitForExit();
        File.Delete(raw);
        Log.Info("editor", $"screenshot saved to {path}");
    }
}
