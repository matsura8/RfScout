namespace RfScout.Core.Display;

/// <summary>
/// The noise-floor line drawn under the live trace. It is a per-bin minimum that is allowed to
/// rise again slowly, which is how spectrum analysers build a floor: the noise floor is the level
/// a bin spends most of its time at, so the lowest value seen over a window is a better estimate
/// than an average, and an average would be dragged up by every transmission that passes through
/// the bin.
///
/// A pure minimum would never let go, though. Turn the LNA down and the floor drops, but a
/// minimum that is holding last week's louder setting keeps the line pinned high forever, so each
/// sweep lifts the stored value before taking the new minimum. The rise is small enough that a
/// persistent signal does not push the floor up (it would need dozens of sweeps in the same bin)
/// and large enough that a real gain change shows up in a second or two of sweeps.
///
/// The estimator resizes itself when the grid changes, because a new span or bin width means the
/// bins no longer line up with the ones it learned about.
/// </summary>
public sealed class NoiseFloorEstimator
{
    /// <summary>dB a stored floor is allowed to climb per completed sweep before a new low wins.</summary>
    public const double RisePerSweepDb = 1.5;

    private readonly Lock _gate = new();
    private double[] _floorDb = [];
    private long _startHz;
    private double _binWidthHz;
    private int _sweeps;

    /// <summary>Bins currently tracked, 0 before the first frame.</summary>
    public int BinCount
    {
        get
        {
            lock (_gate)
            {
                return _floorDb.Length;
            }
        }
    }

    /// <summary>Sweeps folded in so far, so the UI can say how old the floor line is.</summary>
    public int Sweeps
    {
        get
        {
            lock (_gate)
            {
                return _sweeps;
            }
        }
    }

    /// <summary>Grid the floor currently describes, for the caller to check it has not gone stale.</summary>
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
    /// Fold one frame's bins into the estimate and return the floor to draw. Empty bins are
    /// ignored, so a partial first pass cannot drag the floor down to the frame's sentinel value.
    /// </summary>
    public double[] Update(SpectrumFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            bool resized = _floorDb.Length != frame.BinCount
                || _startHz != frame.StartHz
                || _binWidthHz != frame.BinWidthHz;

            if (resized)
            {
                _floorDb = new double[frame.BinCount];
                _startHz = frame.StartHz;
                _binWidthHz = frame.BinWidthHz;
                _sweeps = 0;
            }

            if (frame.SweepCompleted)
            {
                _sweeps++;
            }

            double[] copy = new double[frame.BinCount];
            for (int i = 0; i < frame.BinCount; i++)
            {
                float measured = frame.PowerDb[i];
                if (measured == SpectrumFrame.EmptyBin || float.IsNaN(measured))
                {
                    // Nothing to learn from this bin right now: keep the floor held so far, but
                    // still let it rise so a stale floor does not sit there forever.
                    copy[i] = resized ? double.NaN : Climb(_floorDb[i]);
                    _floorDb[i] = copy[i];
                    continue;
                }

                double held = resized ? double.NaN : Climb(_floorDb[i]);
                copy[i] = double.IsNaN(held) ? measured : Math.Min(held, measured);
                _floorDb[i] = copy[i];
            }

            return copy;
        }
    }

    private static double Climb(double held) =>
        double.IsNaN(held) ? held : held + RisePerSweepDb;

    /// <summary>Forget everything (call when gains change, so an old gain's floor is not drawn).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _floorDb = [];
            _sweeps = 0;
        }
    }
}
