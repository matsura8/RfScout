using System.Diagnostics;

namespace RfScout.Core;

/// <summary>
/// Percent of one CPU core the current process has used since the previous read.
///
/// Uses the process-wide CPU time counter (user + kernel, all threads) against wall-clock
/// elapsed time, so a multi-threaded DSP pipeline can legitimately read above 100 on a
/// multi-core machine, which is the usual meaning of "% CPU" in task managers scaled per
/// core. The first read returns 0 because there is no window yet. Thread-safe: reads may
/// come from the UI timer while other threads keep running.
/// </summary>
public sealed class ProcessCpuTracker
{
    private readonly object _gate = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private long _lastCpuTicks;
    private long _lastWallTicks;

    public ProcessCpuTracker()
    {
        _lastCpuTicks = ReadCpuTime().Ticks;
        _lastWallTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>CPU percent since the last call (0 on the first call).</summary>
    public double ReadPercent()
    {
        lock (_gate)
        {
            long wallNow = Stopwatch.GetTimestamp();
            TimeSpan cpuNow = ReadCpuTime();

            long wallDelta = wallNow - _lastWallTicks;
            double cpuDeltaMs = (cpuNow.Ticks - _lastCpuTicks) * 1000.0 / TimeSpan.TicksPerMillisecond;
            double wallDeltaMs = wallDelta * 1000.0 / Stopwatch.Frequency;

            _lastWallTicks = wallNow;
            _lastCpuTicks = cpuNow.Ticks;

            if (wallDeltaMs <= 0.0 || cpuDeltaMs < 0.0)
            {
                return 0.0;
            }

            return Math.Round((cpuDeltaMs / wallDeltaMs) * 100.0, 1);
        }
    }

    private TimeSpan ReadCpuTime()
    {
        try
        {
            return _process.TotalProcessorTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Process handle went away (only matters at shutdown): report no delta.
            return TimeSpan.Zero;
        }
    }
}
