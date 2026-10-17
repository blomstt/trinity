using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Trinity.Graphics;

/// <summary>
/// A buffer in host-visible, coherent memory that stays mapped for its whole life, so the CPU
/// writes straight into it (a simulation can write instance data here with no extra copy).
/// </summary>
public sealed unsafe class GpuBuffer : IDisposable
{
    public readonly VkBuffer Buffer;
    public readonly long Size;
    readonly Device _device;
    readonly VkDeviceMemory _memory;
    readonly byte* _mapped;

    /// <param name="readback">For data the CPU reads back (video frames): CPU-cached memory, much faster to read.</param>
    public GpuBuffer(GraphicsDevice gpu, long size, VkBufferUsageFlags usage, bool readback = false)
    {
        _device = gpu.Device;
        Size = size;
        Buffer = _device.CreateBuffer(
            new VkBufferCreateInfo { Size = (ByteSize64)(ulong)size, Usage = usage, SharingMode = VkSharingMode.Exclusive },
            null
        );
        _memory = _device.AllocateMemory(
            gpu.PhysicalDevice,
            Buffer,
            VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit | (readback ? VkMemoryPropertyFlags.HostCachedBit : 0),
            null
        );
        _device.BindBufferMemory(Buffer, _memory, (ByteSize64)0);
        _mapped = (byte*)(void*)_device.MapMemory(new MapInfo(_memory));
    }

    public Span<T> AsSpan<T>() where T : unmanaged => new(_mapped, (int)(Size / sizeof(T)));

    public ReadOnlySpan<byte> Bytes => new(_mapped, (int)Size);

    public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged => data.CopyTo(AsSpan<T>());

    public void Dispose()
    {
        _device.UnmapMemory(_memory);
        _device.DestroyBuffer(Buffer, null);
        _device.FreeMemory(_memory, null);
    }
}
