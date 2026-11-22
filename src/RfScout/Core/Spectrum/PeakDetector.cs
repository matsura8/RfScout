using System.Globalization;

namespace RfScout.Core.Spectrum;

/// <summary>How wide a detected peak is, bucketed so the UI and the tracker can group like with like.</summary>
public enum PeakBandwidthClass
{
    /// <summary>Noise-like or too narrow to classify; the -3 dB width is under a bin and a half.</summary>
    Unknown = 0,

    /// <summary>A carrier or narrow emission: -3 dB width of about one to three bins.</summary>
    Narrow = 1,

    /// <summary>A typical channel: -3 dB width of three to eight bins (a radio channel, not a carrier).</summary>
    Medium = 2,

    /// <summary>Wideband digital: -3 dB width beyond eight bins (OFDM, video, a whole sub-band).</summary>
    Wide = 3,
}

/// <summary>
/// One peak measured from a single spectrum frame. All frequencies are absolute Hz; all levels dBFS.
/// Produced by <see cref="PeakDetector"/> and consumed by <see cref="PeakTracker"/> and the peak table.
/// </summary>
public sealed record DetectedPeak(
    int StartBin,
    int EndBin,
    double CenterHz,
    double PeakDb,
    int PeakBin,
    double FloorDb,
    double SnrDb,
    double BandwidthHz3Db,
    double BandwidthHz10Db,
    PeakBandwidthClass BandwidthClass)
{
    /// <summary>Frequency text for the table and markers, e.g. "98.700000 MHz".</summary>
    public string CenterText => Display.FrequencyFormat.MHz(CenterHz);

    /// <summary>One-line description used for automation names on markers and table rows.</summary>
    public string Describe(double binWidthHz) => string.Format(
        CultureInfo.InvariantCulture,
        "{0}, {1}, SNR {2:F1} dB, {3} wide",
        CenterText,
        Display.FrequencyFormat.Db(PeakDb),
        SnrDb,
        Display.FrequencyFormat.BinWidth(BandwidthHz3Db <= 0 ? binWidthHz : BandwidthHz3Db));
}

/// <summary>
/// Finds peaks in one spectrum frame: bins more than <see cref="ThresholdDb"/> above the per-bin
/// noise floor, with neighbouring bins merged into a single peak.
///
/// A real signal is never one bin wide: a carrier bleeds into its neighbours through the FFT
/// window, and a wide signal covers dozens of bins. Without merging, one FM station would show up
/// as a column of five "peaks" and the tracker would chase five IDs for one transmitter. So the
/// detector walks contiguous runs of above-threshold bins and reports each run once, with:
///
///  * a power-weighted centre, because the strongest bin alone is quantised to the bin grid and
///    a tone between two bins would read half a bin low or high depending on rounding; weighting
///    by linear power pulls the estimate toward the true frequency;
///  * the peak power and the SNR against the floor of the peak's own bin;
///  * the -3 dB and -10 dB bandwidths, measured by walking out from the peak bin until the trace
///    drops below peak-3 dB (and peak-10 dB), interpolating between bins so a wide signal reports
///    a width finer than the bin grid.
///
/// The detector works on a frame plus a floor array of the same length; it does no statistics of
/// its own, which is <see cref="NoiseFloorEstimator"/>'s job, and does no tracking, which is
/// <see cref="PeakTracker"/>'s. That separation is what makes each piece testable on synthetic
/// spectra.
/// </summary>
public sealed class PeakDetector
{
    /// <summary>Default detection threshold above the floor, in dB. The brief asks for 10.</summary>
    public const double DefaultThresholdDb = 10.0;

    /// <summary>Most peaks one frame may report; the strongest survive. Bounds the table and the
    /// tracker work when a saturated band produces hundreds of runs.</summary>
    public const int MaxPeaks = 256;

    private readonly double _thresholdDb;

    public PeakDetector(double thresholdDb = DefaultThresholdDb)
    {
        if (thresholdDb < 0 || double.IsNaN(thresholdDb) || double.IsInfinity(thresholdDb))
        {
            throw new ArgumentOutOfRangeException(nameof(thresholdDb), thresholdDb, "Threshold must be a finite non-negative dB value.");
        }

        _thresholdDb = thresholdDb;
    }

    /// <summary>Detection threshold in dB above the floor.</summary>
    public double ThresholdDb => _thresholdDb;

    /// <summary>
    /// Detect peaks in <paramref name="frame"/> against <paramref name="floorDb"/> (one value per
    /// bin, NaN where the floor is not known yet, e.g. from
    /// <see cref="NoiseFloorEstimator.Update"/>). Empty bins and bins with no floor are skipped,
    /// so a partial sweep cannot invent peaks in the holes. Peaks come back strongest first,
    /// capped at <see cref="MaxPeaks"/>.
    /// </summary>
    public IReadOnlyList<DetectedPeak> Detect(SpectrumFrame frame, IReadOnlyList<double> floorDb)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(floorDb);

        var peaks = new List<DetectedPeak>();
        int bins = frame.BinCount;
        if (bins == 0)
        {
            return peaks;
        }

        int i = 0;
        while (i < bins)
        {
            if (!IsAboveThreshold(frame, floorDb, i))
            {
                i++;
                continue;
            }

            int start = i;
            while (i + 1 < bins && IsAboveThreshold(frame, floorDb, i + 1))
            {
                i++;
            }

            int end = i;
            DetectedPeak? peak = BuildPeak(frame, floorDb, start, end);
            if (peak is not null)
            {
                peaks.Add(peak);
            }

            i++;
        }

        peaks.Sort((a, b) => b.PeakDb.CompareTo(a.PeakDb));
        if (peaks.Count > MaxPeaks)
        {
            peaks.RemoveRange(MaxPeaks, peaks.Count - MaxPeaks);
        }

        return peaks;
    }

    private bool IsAboveThreshold(SpectrumFrame frame, IReadOnlyList<double> floorDb, int bin)
    {
        if (bin >= floorDb.Count)
        {
            return false;
        }

        float power = frame.PowerDb[bin];
        if (power == SpectrumFrame.EmptyBin || float.IsNaN(power))
        {
            return false;
        }

        double floor = floorDb[bin];
        return !double.IsNaN(floor) && power - floor >= _thresholdDb;
    }

    private static DetectedPeak? BuildPeak(SpectrumFrame frame, IReadOnlyList<double> floorDb, int start, int end)
    {
        int peakBin = start;
        float peakDb = frame.PowerDb[start];
        double weighted = 0;
        double total = 0;

        for (int b = start; b <= end; b++)
        {
            float power = frame.PowerDb[b];
            if (power > peakDb)
            {
                peakDb = power;
                peakBin = b;
            }

            // Weight by linear power so a tone between bins lands between them rather than on
            // whichever bin the rounding favoured.
            double linear = Math.Pow(10.0, power / 10.0);
            weighted += linear * frame.CenterFrequencyOfBin(b);
            total += linear;
        }

        if (total <= 0)
        {
            return null;
        }

        double centerHz = weighted / total;
        double floor = floorDb[peakBin];
        (double hz3, double hz10, PeakBandwidthClass kind) = MeasureBandwidth(frame, peakBin, start, end, peakDb);

        return new DetectedPeak(
            start,
            end,
            centerHz,
            peakDb,
            peakBin,
            floor,
            peakDb - floor,
            hz3,
            hz10,
            kind);
    }

    /// <summary>
    /// -3 dB and -10 dB widths of the run around <paramref name="peakBin"/>. Walks outward from
    /// the peak bin while the trace stays above the drop level, interpolating the crossing between
    /// the last inside bin and the first outside one, and clamps the walk to the run's bounds so a
    /// neighbouring peak sharing the threshold region cannot steal the measurement.
    /// </summary>
    private static (double H3, double H10, PeakBandwidthClass Kind) MeasureBandwidth(
        SpectrumFrame frame,
        int peakBin,
        int runStart,
        int runEnd,
        float peakDb)
    {
        double binWidth = frame.BinWidthHz;
        double h3 = EdgeWidth(frame, peakBin, runStart, runEnd, peakDb - 3.0, binWidth);
        double h10 = EdgeWidth(frame, peakBin, runStart, runEnd, peakDb - 10.0, binWidth);

        // The run itself is the honest width floor: a real signal is at least as wide as the
            // bins that cleared the threshold, even when its skirt drops fast (a steep-sided
            // wide signal measures a narrow -3 dB width but is obviously not a carrier).
            // The run itself is the honest width floor: a real signal is at least as wide as the
            // bins that cleared the threshold, even when its skirt drops fast (a steep-sided
            // wide signal measures a narrow -3 dB width but is obviously not a carrier). The
            // -3 dB measure only counts when it is wider than the run.
            double widthBins = h3 / binWidth;
        PeakBandwidthClass kind = widthBins switch
        {
            < 1.5 => PeakBandwidthClass.Unknown,
            < 3.0 => PeakBandwidthClass.Narrow,
            < 8.0 => PeakBandwidthClass.Medium,
            _ => PeakBandwidthClass.Wide,
        };

        return (h3, h10, kind);
    }

    /// <summary>
    /// Width of the run around <paramref name="peakBin"/> at <paramref name="dropDb"/>: the
    /// distance between the left and right crossings of that level, interpolated between bins.
    /// A drop level at or below the run's edge bins measures at least one bin wide, because the
    /// whole run sits above it.
    /// </summary>
    private static double EdgeWidth(SpectrumFrame frame, int peakBin, int runStart, int runEnd, double dropDb, double binWidth)
    {
        // The walk is bounded by the run, but a flat-topped signal's skirt can still be
            // above the drop level at the run's own edge (the threshold run ends before the
            // skirt does, because those skirt bins sit below the floor threshold). Extend the
            // walk one bin past each edge; bins outside the run are still real measurements.
            int leftLimit = Math.Max(0, runStart - 1);
            int rightLimit = Math.Min(frame.BinCount - 1, runEnd + 1);
            double left = CrossingHz(frame, peakBin, leftLimit, dropDb, step: -1, binWidth);
            double right = CrossingHz(frame, peakBin, rightLimit, dropDb, step: +1, binWidth);

            // The run itself is the honest floor for the width: a signal is at least as wide
            // as the bins that cleared the threshold, even when its skirt drops fast.
            double runSpan = ((runEnd - runStart) + 1) * binWidth;
            return Math.Max(runSpan, right - left);
    }

    /// <summary>
    /// Frequency where the trace, walking from <paramref name="peakBin"/> toward
    /// <paramref name="limitBin"/>, falls through <paramref name="dropDb"/>. Returns the bin-edge
    /// frequency at the limit when the level never falls (the run is entirely above the drop).
    /// </summary>
    private static double CrossingHz(SpectrumFrame frame, int peakBin, int limitBin, double dropDb, int step, double binWidth)
    {
        double center = frame.CenterFrequencyOfBin(peakBin);
        int b = peakBin;
        while (true)
        {
            int next = b + step;
            if (next < 0 || next > limitBin || next >= frame.BinCount)
            {
                // Never fell through the level inside the run: the edge is half a bin beyond the
                // outermost bin centre.
                return frame.CenterFrequencyOfBin(b) + (step * binWidth / 2.0);
            }

            float here = frame.PowerDb[b];
            float there = frame.PowerDb[next];
            if (there <= dropDb)
            {
                // Linear-in-dB interpolation between the two bin centres.
                double span = here - there;
                double fraction = span <= 0 ? 0 : Math.Clamp((here - dropDb) / span, 0, 1);
                return center + (step * fraction * binWidth);
            }

            b = next;
        }
    }
}
