namespace RfScout.Core.Locate;

/// <summary>
/// M5 locate state machine, kept out of WPF so the strength timeline, the peak hold and the
/// audio tone are unit-testable with a fake clock and no radio:
///
///  * every IQ window folds into one current-strength number (dBFS);
///  * the newest value wins the peak hold, and a reset starts the hold again from the next
///    window (a stale hold never outlives a reset);
///  * the graph timeline buckets by seconds from <see cref="Start"/> on the caller's clock,
///    keeping the loudest window per second and dropping buckets older than the window
///    (60 s by default), so the graph is a true rolling 60 seconds;
///  * the tone pitch follows strength: stronger is higher, clamped to a fixed range, and the
///    tone is silent at the noise floor so an idle locate does not hum.
///
/// Not thread safe on its own: the caller serialises <see cref="Offer"/> with the clock it
/// passes (the view model keeps its own lock, like the rest of the pipeline).
/// </summary>
public sealed class LocateController
{
    /// <summary>Default rolling window of the strength graph, in seconds.</summary>
    public const int DefaultGraphSeconds = 60;

    /// <summary>Tone frequency at the bottom of the meter range and at the top of it.</summary>
    public const double ToneMinHz = 300d;
    public const double ToneMaxHz = 1800d;

    /// <summary>
    /// Meter range the tone maps across. The bottom is the silence floor (a quiet band sits
    /// near it and gets a near-silent low tone); the top is a strong in-band signal.
    /// </summary>
    public const double MeterMinDb = SignalStrengthTracker.SilenceDb;
    public const double MeterMaxDb = -20d;

    private readonly int _graphSeconds;
    private readonly List<double[]> _buckets = [];
    private double _currentDb = SignalStrengthTracker.SilenceDb;
    private double _peakDb = SignalStrengthTracker.SilenceDb;
    private DateTimeOffset _startUtc = DateTimeOffset.UtcNow;
    private int _nextBucketSecond;

    /// <summary>Create a tracker with a <paramref name="graphSeconds"/>-wide rolling graph.</summary>
    public LocateController(int graphSeconds = DefaultGraphSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(graphSeconds);
        _graphSeconds = graphSeconds;
    }

    /// <summary>Strength of the newest window, dBFS.</summary>
    public double CurrentDb
    {
        get
        {
            lock (_buckets)
            {
                return _currentDb;
            }
        }
    }

    /// <summary>Loudest window since the start or the last <see cref="ResetPeakHold"/>.</summary>
    public double PeakDb
    {
        get
        {
            lock (_buckets)
            {
                return _peakDb;
            }
        }
    }

    /// <summary>When the current locate session started (the graph's t=0).</summary>
    public DateTimeOffset StartUtc
    {
        get
        {
            lock (_buckets)
            {
                return _startUtc;
            }
        }
    }

    /// <summary>Start (or restart) a locate session: the timeline and the hold begin fresh.</summary>
    public void Start(DateTimeOffset nowUtc)
    {
        lock (_buckets)
        {
            _startUtc = nowUtc;
            _buckets.Clear();
            _nextBucketSecond = 0;
            _currentDb = SignalStrengthTracker.SilenceDb;
            _peakDb = SignalStrengthTracker.SilenceDb;
        }
    }

    /// <summary>Fold one IQ window in: current strength, peak hold, and the graph bucket.</summary>
    public void Offer(ReadOnlyMemory<byte> iq, DateTimeOffset nowUtc)
    {
        double db = SignalStrengthTracker.MeasureDb(iq);
        OfferDb(db, nowUtc);
    }

    /// <summary>Strength-only entry point, so tests and the tone path need no IQ bytes.</summary>
    public void OfferDb(double db, DateTimeOffset nowUtc)
    {
        lock (_buckets)
        {
            _currentDb = db;
            if (db > _peakDb)
            {
                _peakDb = db;
            }

            int second = (int)Math.Max(0, Math.Floor((nowUtc - _startUtc).TotalSeconds));
            while (_buckets.Count > 0 && second - _buckets[0][0] >= _graphSeconds)
            {
                _buckets.RemoveAt(0);
            }

            if (_buckets.Count == 0 || _nextBucketSecond < second)
            {
                _buckets.Add([second, db]);
                _nextBucketSecond = second;
            }
            else
            {
                double[] head = _buckets[^1];
                if (db > head[1])
                {
                    head[1] = db;
                }
            }
        }
    }

    /// <summary>
    /// Clear the peak hold. The hold restarts from the next window; the current reading and
    /// the graph are untouched.
    /// </summary>
    public void ResetPeakHold()
    {
        lock (_buckets)
        {
            _peakDb = SignalStrengthTracker.SilenceDb;
        }
    }

    /// <summary>
    /// Snapshot of the graph as (seconds-since-start, dB) pairs, oldest first. The view draws
    /// this; the buckets themselves stay private.
    /// </summary>
    public IReadOnlyList<(int Second, double Db)> SnapshotGraph()
    {
        lock (_buckets)
        {
            return _buckets.Select(static b => ((int)b[0], b[1])).ToList();
        }
    }

    /// <summary>
    /// Tone frequency for a strength: linear across the meter range, clamped to
    /// <see cref="ToneMinHz"/>..<see cref="ToneMaxHz"/>. Silence gives the minimum.
    /// </summary>
    public static double ToneHzFor(double db) =>
        ToneMinHz + (Math.Clamp(db, MeterMinDb, MeterMaxDb) - MeterMinDb) / (MeterMaxDb - MeterMinDb) * (ToneMaxHz - ToneMinHz);

    /// <summary>
    /// Tone gain 0..1 for a strength: zero at the floor, full at the top of the range. The
    /// tone is off by default; this only shapes it when the operator turns it on.
    /// </summary>
    public static double ToneGainFor(double db) =>
        Math.Clamp((db - MeterMinDb) / (MeterMaxDb - MeterMinDb), 0d, 1d);
}
