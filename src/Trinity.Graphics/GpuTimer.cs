using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Trinity.Graphics;

/// <summary>
/// GPU time per section of a frame, from timestamp queries: <see cref="Begin"/> at the start of a
/// frame's commands, <see cref="Mark"/> after each section. Results arrive a couple of frames later
/// (read once that frame's fence has passed, so reading never stalls) in <see cref="Last"/>.
/// </summary>
public sealed unsafe class GpuTimer : IDisposable
{
    const int MaxMarks = 16;

    readonly GraphicsDevice _gpu;
    readonly VkQueryPool _pool;
    readonly List<string>[] _names = [[], []];
    readonly ulong[] _results = new ulong[MaxMarks];
    readonly bool _supported;

    /// <summary>Milliseconds per section of the last frame read back, in order.</summary>
    public IReadOnlyList<(string Name, double Ms)> Last { get; private set; } = [];

    public GpuTimer(GraphicsDevice gpu)
    {
        _gpu = gpu;
        _supported = gpu.Features.TimestampPeriod > 0;
        if (_supported)
            _pool = gpu.Device.CreateQueryPool(
                new VkQueryPoolCreateInfo { QueryType = VkQueryType.Timestamp, QueryCount = MaxMarks * GraphicsDevice.FramesInFlight },
                null
            );
    }

    /// <summary>Reads this slot's previous results, then starts timing this frame.</summary>
    public void Begin(CommandBuffer cmd)
    {
        if (!_supported)
            return;
        var slot = _gpu.FrameIndex;
        var names = _names[slot];
        if (names.Count > 1)
        {
            fixed (ulong* r = _results)
            {
                var result = _gpu.Device.GetQueryPoolResults(_pool, slot * MaxMarks, names.Count, new ReadOnlySpan<byte>(r, names.Count * 8), (ByteSize64)8, VkQueryResultFlags._64Bit);
                if (result == VkResult.Success)
                {
                    var ms = new List<(string, double)>(names.Count - 1);
                    for (var i = 1; i < names.Count; i++)
                        ms.Add((names[i], (_results[i] - _results[i - 1]) * _gpu.Features.TimestampPeriod / 1e6));
                    Last = ms;
                }
            }
        }
        names.Clear();
        cmd.ResetQueryPool(_pool, slot * MaxMarks, MaxMarks);
        Mark(cmd, "start");
    }

    /// <summary>Ends the section called <paramref name="name"/> (it began at the previous mark).</summary>
    public void Mark(CommandBuffer cmd, string name)
    {
        var names = _names[_gpu.FrameIndex];
        if (!_supported || names.Count >= MaxMarks)
            return;
        cmd.WriteTimestamp2(VkPipelineStageFlags2.AllCommandsBit, _pool, _gpu.FrameIndex * MaxMarks + names.Count);
        names.Add(name);
    }

    /// <summary>Total of the last frame's sections, in milliseconds.</summary>
    public double TotalMs => Last.Sum(s => s.Ms);

    public void Dispose()
    {
        if (_supported)
            _gpu.Device.DestroyQueryPool(_pool, null);
    }
}
