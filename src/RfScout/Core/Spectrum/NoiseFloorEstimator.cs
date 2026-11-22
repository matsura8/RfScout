using System.Globalization;

namespace RfScout.Core.Spectrum;

/// <summary>
/// A per-bin noise-floor estimate for the peak detector: the low percentile (by default the 20th)
/// of the power each bin has held over the last N completed sweeps.
///
/// The floor has to follow gain changes and frequency-dependent noise, so a single global number
/// is useless: the LNA can move 40 dB and the bottom of a 6 GHz span is tens of dB noisier than
/// the middle. A per-bin low percentile is the standard answer. A mean would be dragged up by
/// every transmission passing through the bin; a pure minimum would never let go after the gains
/// come down. The percentile sits at the level a bin spends most of its time at, and a fixed
/// window means a gain change flushes the old, louder samples out within N sweeps.
///
/// Only frames that close a complete pass over the span (<see cref="SpectrumFrame.SweepCompleted"/>)
/// are folded in. A partial frame has holes, and the empty bins in a hole would otherwise pull
/// the percentile down to the frame sentinel and invent peaks out of missing coverage.
///
/// This estimator is deliberately separate from <see cref="Display.NoiseFloorEstimator"/>, which
/// draws the floor line under the live trace and updates on every refresh so the line reacts
/// instantly. Statistics for the peak detector want whole sweeps, so they live here.
/// </summary>
public sealed class NoiseFloorEstimator
{
    /// <summary>Percentile the floor is taken at, 0..1. The brief asks for the 20th.</summary>
    public const double DefaultPercentile = 0.20;

    /// <summary>Completed sweeps each bin remembers. Long enough to be stable, short enough that
    /// a gain change settles in a second or two of sweeps.</summary>
    public const int DefaultWindowSweeps = 64;

    private readonly Lock _gate = new();
    private readonly double _percentile;
    private readonly int _window;
    private double[][] _history = [];
    private int _write;
    private int _filled;
    private long _startHz;
    private double _binWidthHz;
    private double[] _scratch = [];
    private double[] _floor = [];

    public NoiseFloorEstimator(double percentile = DefaultPercentile, int windowSweeps = DefaultWindowSweeps)
    {
        if (percentile is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be between 0 and 1.");
        }

        if (windowSweeps < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSweeps), windowSweeps, "The window must hold at least two sweeps.");
        }

        _percentile = percentile;
        _window = windowSweeps;
    }

    /// <summary>Bins tracked, 0 before the first completed sweep.</summary>
    public int BinCount
    {
        get
        {
            lock (_gate)
            {
                return _floor.Length;
            }
        }
    }

    /// <summary>Completed sweeps folded in so far, so the UI can say how confident the floor is.</summary>
    public int Sweeps
    {
        get
        {
            lock (_gate)
            {
                return _filled;
            }
        }
    }

    /// <summary>Grid the floor describes, so the caller can check it has not gone stale.</summary>
    public (long StartHz, double BinWidthHz) Grid
    {
        get
        {
            lock (_gate)
            {
                return (_startHz, _binWidthHz);
            }
        }
    }

    /// <summary>
    /// Fold one frame into the history and return the floor per bin. Only frames with
    /// <see cref="SpectrumFrame.SweepCompleted"/> set are counted; any other frame returns the
    /// floor held so far (or an empty array before the first sweep) without changing it.
    /// A grid change (new span, bin width or gains) resets the history, because the old bins
    /// measured a different radio.
    /// </summary>
    public IReadOnlyList<double> Update(SpectrumFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            bool resized = _floor.Length != frame.BinCount
                || _startHz != frame.StartHz
                || _binWidthHz != frame.BinWidthHz;

            if (resized)
            {
                // Allocate one slot more than the frame can hold: a zero-bin frame (a plan with
                // no coverage yet) must still get a usable history, and the loops below are
                // bounded by frame.BinCount so the extra row is never read.
                // The floor array matches the frame exactly (callers index it by bin), but the
                // scratch used by the percentile sort needs at least one slot even for a
                // zero-bin frame, so it is sized separately.
                int rows = frame.BinCount;
                int scratchRows = Math.Max(_window, rows);
                _history = new double[rows][];
                for (int i = 0; i < rows; i++)
                {
                    _history[i] = new double[_window];
                }

                _scratch = new double[scratchRows];
                _floor = new double[rows];
                _startHz = frame.StartHz;
                _binWidthHz = frame.BinWidthHz;
                _write = 0;
                _filled = 0;
                Array.Fill(_floor, double.NaN);
            }

            if (!frame.SweepCompleted)
            {
                return (double[])_floor.Clone();
            }

            for (int i = 0; i < frame.BinCount; i++)
            {
                float measured = frame.PowerDb[i];
                if (measured != SpectrumFrame.EmptyBin && !float.IsNaN(measured))
                {
                    _history[i][_write] = measured;
                }
            }

            _write = (_write + 1) % _window;
            _filled = Math.Min(_filled + 1, _window);

            for (int i = 0; i < frame.BinCount; i++)
            {
                _floor[i] = PercentileOf(_history[i], _filled, _percentile, _scratch);
            }

            return (double[])_floor.Clone();
        }
    }

    /// <summary>Forget everything (call when the gains change so an old setting's floor is not used).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _history = [];
            _floor = [];
            _scratch = [];
            _write = 0;
            _filled = 0;
        }
    }

    /// <summary>
    /// The <paramref name="percentile"/> of the first <paramref name="count"/> values of
    /// <paramref name="values"/>, nearest-rank. Copies into <paramref name="scratch"/> (at least
    /// <paramref name="count"/> long) so the ring buffer stays intact. NaN values are skipped, so
    /// a bin that has not been measured yet has no floor rather than a fake one.
    /// </summary>
    internal static double PercentileOf(double[] values, int count, double percentile, double[] scratch)
    {
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            if (!double.IsNaN(values[i]))
            {
                scratch[n++] = values[i];
            }
        }

        if (n == 0)
        {
            return double.NaN;
        }

        Array.Sort(scratch, 0, n);
        int rank = (int)Math.Round(percentile * (n - 1), MidpointRounding.AwayFromZero);
        return scratch[Math.Clamp(rank, 0, n - 1)];
    }

    /// <summary>Format the floor for the status bar, e.g. "floor 20th pct over 30 sweeps".</summary>
    public string Describe()
    {
        lock (_gate)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "floor {0:P0} pct over {1} sweep(s)",
                _percentile,
                _filled);
        }
    }
}
