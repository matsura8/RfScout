using System.Globalization;

namespace RfScout.Core.Spectrum;

/// <summary>How a tracked peak behaves over time, from its duty cycle.</summary>
public enum PeakActivityClass
{
    /// <summary>Present in most sweeps: a broadcast station, a carrier, a constant emitter.</summary>
    Continuous = 0,

    /// <summary>Present often enough to be a real repeated emitter, but not all the time.</summary>
    Intermittent = 1,

    /// <summary>Present rarely: a push-to-talk burst, a sensor transmission, an ADS-B squawk.</summary>
    Bursty = 2,
}

/// <summary>
/// One peak as the tracker has seen it across sweeps: stable ID, first and last sighting, hit
/// count, duty cycle, power statistics, a short power history, and an activity class.
/// Immutable snapshot; the tracker owns the mutable state and hands out copies.
/// </summary>
public sealed record TrackedPeak(
    int Id,
    double CenterHz,
    double PeakDb,
    double SnrDb,
    double BandwidthHz3Db,
    PeakBandwidthClass BandwidthClass,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    int HitCount,
    int SweepsSeen,
    double DutyCycle,
    double MinDb,
    double MaxDb,
    double AverageDb,
    PeakActivityClass Activity,
    IReadOnlyList<double> PowerHistory)
{
    /// <summary>History entries kept per peak: enough for a sparkline, small enough to be free.</summary>
    public const int HistoryLength = 32;

    /// <summary>Duty at or above which a peak is continuous. A station that fades still clears this.</summary>
    public const double ContinuousDuty = 0.85;

    /// <summary>Duty at or above which a peak is intermittent rather than bursty.</summary>
    public const double IntermittentDuty = 0.15;

    /// <summary>Sweeps that must pass before the class means anything; before that, bursty is a guess.</summary>
    public const int MinSweepsForClass = 8;

    public string CenterText => Display.FrequencyFormat.MHz(CenterHz);

    /// <summary>Duty cycle as a percentage string for the table.</summary>
    public string DutyText => (DutyCycle * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Classify from duty cycle, holding "bursty" until enough sweeps have passed.</summary>
    public static PeakActivityClass ClassFor(double duty, int sweepsSeen)
    {
        if (sweepsSeen < MinSweepsForClass)
        {
            return PeakActivityClass.Bursty;
        }

        if (duty >= ContinuousDuty)
        {
            return PeakActivityClass.Continuous;
        }

        return duty >= IntermittentDuty ? PeakActivityClass.Intermittent : PeakActivityClass.Bursty;
    }

    /// <summary>One-line description for automation names and the detail pane.</summary>
    public string Describe() => string.Format(
        CultureInfo.InvariantCulture,
        "peak {0}: {1}, {2}, SNR {3:F1} dB, duty {4}, {5}",
        Id,
        CenterText,
        Display.FrequencyFormat.Db(PeakDb),
        SnrDb,
        DutyText,
        Activity.ToString().ToLowerInvariant());
}

/// <summary>
/// Gives peaks stable identities across sweeps.
///
/// The detector reports fresh, anonymous peaks every frame; the tracker decides which peak is
/// which. A new detection matches an existing track when its centre falls within a frequency
/// tolerance (a few bins: the centre estimate jitters by a fraction of a bin sweep to sweep, and
/// a real transmitter drifts a little too). A match updates that track; no match starts a new ID.
/// IDs are never reused within a session, so "peak 17" means the same emitter from the first
/// sweep to the last, which is what makes the table, the markers and (from M3) the analyze queue
/// usable.
///
/// Each track carries the brief's statistics: first and last seen, hit count, duty cycle (hits
/// over sweeps seen), power min/max/average, a short power history for the detail pane, and an
/// activity class derived from the duty cycle: continuous, intermittent or bursty.
///
/// Tracks that stop being seen age out after <see cref="DropAfterMissedSweeps"/> missed sweeps
/// so a transmitter that goes off air stops occupying the table, and a transmitter that returns
/// after that gets a fresh ID rather than a stale history.
///
/// Thread safety: <see cref="Update"/> runs on the DSP/UI-refresh path and
/// <see cref="Snapshot"/> on the paint path, both behind one lock.
/// </summary>
public sealed class PeakTracker
{
    /// <summary>Missed sweeps before an unseen track is dropped.</summary>
    public const int DropAfterMissedSweeps = 120;

    /// <summary>Bin widths of frequency tolerance when matching a detection to a track.</summary>
    public const double MatchToleranceBins = 2.0;

    private sealed class Track
    {
        internal int Id;
        internal double CenterHz;
        internal double PeakDb;
        internal double SnrDb;
        internal double BandwidthHz3Db;
        internal PeakBandwidthClass BandwidthClass;
        internal DateTimeOffset FirstSeenUtc;
        internal DateTimeOffset LastSeenUtc;
        internal bool SeenThisSweep;
        internal int HitCount;
        internal int MissedSweeps;
        internal double MinDb = double.MaxValue;
        internal double MaxDb = double.MinValue;
        internal double SumDb;
        internal readonly double[] History = new double[TrackedPeak.HistoryLength];
        internal int HistoryWrite;
        internal int HistoryFilled;
    }

    private readonly Lock _gate = new();
    private readonly List<Track> _tracks = [];
    private readonly double _toleranceBins;
    private int _nextId = 1;
    private int _sweepsSeen;

    public PeakTracker(double matchToleranceBins = MatchToleranceBins)
    {
        if (matchToleranceBins <= 0 || double.IsNaN(matchToleranceBins) || double.IsInfinity(matchToleranceBins))
        {
            throw new ArgumentOutOfRangeException(nameof(matchToleranceBins), matchToleranceBins, "Tolerance must be a positive number of bins.");
        }

        _toleranceBins = matchToleranceBins;
    }

    /// <summary>Completed sweeps folded in so far.</summary>
    public int SweepsSeen
    {
        get
        {
            lock (_gate)
            {
                return _sweepsSeen;
            }
        }
    }

    /// <summary>
    /// Fold one sweep's detections into the tracks and return the new snapshot. Call once per
    /// completed sweep (a frame with <see cref="SpectrumFrame.SweepCompleted"/>), with the peaks
    /// <see cref="PeakDetector"/> found in that frame.
    /// </summary>
    public IReadOnlyList<TrackedPeak> Update(IReadOnlyList<DetectedPeak> peaks, double binWidthHz, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        if (binWidthHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(binWidthHz), binWidthHz, "Bin width must be positive.");
        }

        lock (_gate)
        {
            _sweepsSeen++;
            double toleranceHz = _toleranceBins * binWidthHz;

            foreach (Track track in _tracks)
            {
                track.SeenThisSweep = false;
            }

            foreach (DetectedPeak peak in peaks)
            {
                Track? match = FindNearestTrackLocked(peak.CenterHz, toleranceHz);
                if (match is null)
                {
                    match = new Track { Id = _nextId++ };
                    _tracks.Add(match);
                }

                if (match.HitCount == 0)
                {
                    match.FirstSeenUtc = utcNow;
                }

                match.SeenThisSweep = true;
                match.CenterHz = peak.CenterHz;
                match.PeakDb = peak.PeakDb;
                match.SnrDb = peak.SnrDb;
                match.BandwidthHz3Db = peak.BandwidthHz3Db;
                match.BandwidthClass = peak.BandwidthClass;
                match.LastSeenUtc = utcNow;
                match.HitCount++;
                match.MissedSweeps = 0;
                match.MinDb = Math.Min(match.MinDb, peak.PeakDb);
                match.MaxDb = Math.Max(match.MaxDb, peak.PeakDb);
                match.SumDb += peak.PeakDb;

                match.History[match.HistoryWrite] = peak.PeakDb;
                match.HistoryWrite = (match.HistoryWrite + 1) % TrackedPeak.HistoryLength;
                match.HistoryFilled = Math.Min(match.HistoryFilled + 1, TrackedPeak.HistoryLength);
            }

            // Second pass: anything not matched this sweep ages. Matching stamped SeenThisSweep;
            // everything else missed one.
            foreach (Track track in _tracks)
            {
                if (!track.SeenThisSweep)
                {
                    track.MissedSweeps++;
                }
            }

            _tracks.RemoveAll(t => t.MissedSweeps > DropAfterMissedSweeps);

            return BuildSnapshotLocked();
        }
    }

    /// <summary>Current tracks as an immutable snapshot, strongest first. Safe from any thread.</summary>
    public IReadOnlyList<TrackedPeak> Snapshot()
    {
        lock (_gate)
        {
            return BuildSnapshotLocked();
        }
    }

    /// <summary>Forget all tracks and IDs (new sweep plan; IDs restart at 1).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _tracks.Clear();
            _nextId = 1;
            _sweepsSeen = 0;
        }
    }

    private Track? FindNearestTrackLocked(double centerHz, double toleranceHz)
    {
        Track? best = null;
        double bestDistance = double.MaxValue;
        foreach (Track track in _tracks)
        {
            double distance = Math.Abs(track.CenterHz - centerHz);
            if (distance <= toleranceHz && distance < bestDistance)
            {
                best = track;
                bestDistance = distance;
            }
        }

        return best;
    }

    private List<TrackedPeak> BuildSnapshotLocked()
    {
        var result = new List<TrackedPeak>(_tracks.Count);
        foreach (Track track in _tracks)
        {
            double duty = _sweepsSeen <= 0 ? 0 : (double)track.HitCount / _sweepsSeen;
            double average = track.HitCount <= 0 ? double.NaN : track.SumDb / track.HitCount;

            var history = new double[track.HistoryFilled];
            int start = (track.HistoryWrite - track.HistoryFilled + TrackedPeak.HistoryLength) % TrackedPeak.HistoryLength;
            for (int i = 0; i < track.HistoryFilled; i++)
            {
                history[i] = track.History[(start + i) % TrackedPeak.HistoryLength];
            }

            result.Add(new TrackedPeak(
                track.Id,
                track.CenterHz,
                track.PeakDb,
                track.SnrDb,
                track.BandwidthHz3Db,
                track.BandwidthClass,
                track.FirstSeenUtc,
                track.LastSeenUtc,
                track.HitCount,
                _sweepsSeen,
                duty,
                track.MinDb == double.MaxValue ? double.NaN : track.MinDb,
                track.MaxDb == double.MinValue ? double.NaN : track.MaxDb,
                average,
                TrackedPeak.ClassFor(duty, _sweepsSeen),
                history));
        }

        result.Sort((a, b) => b.PeakDb.CompareTo(a.PeakDb));
        return result;
    }
}
