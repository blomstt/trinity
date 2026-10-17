using Brutal.Concurrency.Jobs;

namespace Trinity;

/// <summary>
/// Parallel loops on BRUTAL's job system. One scheduler for the whole engine, sized to the CPU.
/// Not re-entrant: a <see cref="For"/> body must not start another <see cref="For"/>.
/// </summary>
public sealed class Jobs
{
    public delegate void RangeBody(int start, int end, int chunk);

    readonly JobScheduler _scheduler;
    readonly RangeJob[] _pool;

    public int ThreadCount { get; }

    /// <summary>Run everything on the calling thread instead (for comparing and debugging).</summary>
    public bool SingleThreaded { get; set; }

    public Jobs(int? threads = null)
    {
        ThreadCount = Math.Max(1, threads ?? Environment.ProcessorCount);
        _scheduler = new JobScheduler((uint)ThreadCount, "trinity");
        _pool = new RangeJob[ThreadCount * 4];
        for (var i = 0; i < _pool.Length; i++)
            _pool[i] = new RangeJob();
    }

    /// <summary>Number of chunks <see cref="For"/> splits work into; size per-chunk scratch arrays with this.</summary>
    public int ChunkCount => _pool.Length;

    /// <summary>
    /// Runs <paramref name="body"/> over [0, count) split into <see cref="ChunkCount"/> contiguous
    /// chunks, in parallel, and waits. Each call gets (start, end, chunk index); chunks are stable for
    /// a given count, so per-chunk results can be combined in order afterwards.
    /// </summary>
    public void For(int count, RangeBody body)
    {
        var chunk = (count + _pool.Length - 1) / _pool.Length;
        for (var j = 0; j < _pool.Length; j++)
        {
            var job = _pool[j];
            job.Body = body;
            job.Start = Math.Min(count, j * chunk);
            job.End = Math.Min(count, (j + 1) * chunk);
            job.Chunk = j;
        }
        if (SingleThreaded || count < 64)
        {
            foreach (var job in _pool)
                job.Run();
            return;
        }
        foreach (var job in _pool)
            if (job.End > job.Start)
                _scheduler.Queue(job, job.End - job.Start);
        _scheduler.ExecuteJobs();
        _scheduler.Wait();
    }

    sealed class RangeJob : IJob
    {
        public RangeBody? Body;
        public int Start, End, Chunk;

        public void Run()
        {
            if (End > Start)
                Body!(Start, End, Chunk);
        }
    }
}
