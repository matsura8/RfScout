namespace RfScout.Core.Streaming;

/// <summary>
/// What the display keeps from the frames the radio produces.
///
/// The device raises <see cref="IRadioSource.SpectrumAvailable"/> on its DSP thread about ten
/// times a second. The UI must not touch those frames directly, so this class holds the newest
/// frame and the peak-hold merge behind a lock, and the dispatcher timer takes a snapshot when it
/// paints. The UI thread therefore never waits on the DSP thread and the DSP thread never waits on
/// the UI thread, which is what keeps the window responsive while a sweep runs.
///
/// Peak hold is merged here rather than in the plot so a resize or a second plot's repaint does not
/// reset the hold, and so the merge runs once per frame instead of once per paint.
/// </summary>
public sealed class SpectrumFrameBuffer
{
    private readonly Lock _gate = new();
    private SpectrumFrame? _latest;
    private SpectrumFrame? _peakHold;
    private SpectrumFrame? _lastCompleted;
    private int _framesSeen;
    private int _completedSeen;
    private long _sequence;

    /// <summary>Frames offered since the plan changed.</summary>
    public int FramesSeen => Volatile.Read(ref _framesSeen);

    /// <summary>Frames that closed a whole pass over the span.</summary>
    public int CompletedFramesSeen => Volatile.Read(ref _completedSeen);

    /// <summary>True when at least one frame has arrived since <see cref="Reset"/>.</summary>
    public bool HasFrame => Volatile.Read(ref _framesSeen) > 0;

    /// <summary>Offer one frame, from whatever thread the source raises it on.</summary>
    public void Offer(SpectrumFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            _latest = frame;
            _peakHold = frame.MergePeakHold(_peakHold);
            if (frame.SweepCompleted)
            {
                _lastCompleted = frame;
                Interlocked.Increment(ref _completedSeen);
            }

            Interlocked.Increment(ref _framesSeen);
            Interlocked.Increment(ref _sequence);
        }
    }

    /// <summary>
    /// The newest frame and the peak-hold merge as one snapshot. False when nothing has arrived
    /// yet, which is how the plots know to paint an empty grid instead of stale data.
    /// </summary>
    public bool TryGetSnapshot(out SpectrumFrame? latest, out SpectrumFrame? peakHold)
    {
        lock (_gate)
        {
            latest = _latest;
            peakHold = _peakHold;
            return latest is not null;
        }
    }

    /// <summary>The last frame that closed a complete pass, or null while the first pass runs.</summary>
    public SpectrumFrame? LastCompleted
    {
        get
        {
            lock (_gate)
            {
                return _lastCompleted;
            }
        }
    }

    /// <summary>
    /// Sequence number of the newest frame. The dispatcher timer compares it with the last value it
    /// painted and skips the repaint when nothing changed, so an idle window does not redraw two
    /// hand-drawn plots sixty times a second for nothing.
    /// </summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    /// <summary>Forget the held frames (new plan, or the operator cleared the peak hold).</summary>
    public void Reset(bool clearPeakHold = true)
    {
        lock (_gate)
        {
            _latest = null;
            _lastCompleted = null;
            if (clearPeakHold)
            {
                _peakHold = null;
            }

            Interlocked.Exchange(ref _framesSeen, 0);
            Interlocked.Exchange(ref _completedSeen, 0);
        }
    }
}

/// <summary>
/// Counts the gaps between UI paints, which is how "the UI stays responsive while sweeping" is
/// proven without a human watching: a dispatcher timer records the time between ticks and a blocked
/// UI thread shows up as a long gap. The sweep runs on its own thread, so a gap over a second means
/// the UI thread is the one that is stuck.
/// </summary>
public sealed class UiFrameHeartbeat
{
    private const long NoPrevious = -1;

    private long _lastTicks = NoPrevious;
    private long _worstTicks;
    private long _ticks;

    /// <summary>Longest gap between paints in milliseconds since the last <see cref="Reset"/>.</summary>
    public double WorstGapMilliseconds => Interlocked.Read(ref _worstTicks) / (double)TimeSpan.TicksPerMillisecond;

    /// <summary>Paints recorded.</summary>
    public long PaintCount => Interlocked.Read(ref _ticks);

    /// <summary>Call on the UI thread each paint; returns the gap since the previous one.</summary>
    public double Record()
    {
        long now = DateTime.UtcNow.Ticks;
        long last = Interlocked.Exchange(ref _lastTicks, now);
        Interlocked.Increment(ref _ticks);

        if (last == NoPrevious)
        {
            return 0;
        }

        double gapMs = (now - last) / (double)TimeSpan.TicksPerMillisecond;
        long wanted = (long)(gapMs * TimeSpan.TicksPerMillisecond);
        long worst = Interlocked.Read(ref _worstTicks);
        while (wanted > worst && Interlocked.CompareExchange(ref _worstTicks, wanted, worst) != worst)
        {
            worst = Interlocked.Read(ref _worstTicks);
        }

        return gapMs;
    }

    /// <summary>Start counting again.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _lastTicks, NoPrevious);
        Interlocked.Exchange(ref _worstTicks, 0);
        Interlocked.Exchange(ref _ticks, 0);
    }
}
