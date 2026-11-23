namespace RfScout.Core.Spectrum;

/// <summary>
/// A noise floor taken across frequency rather than over time: for each bin, the low percentile
/// of the bins around it in the same sweep.
///
/// <see cref="NoiseFloorEstimator"/> takes each bin's floor from that bin's own history, which
/// follows gain changes and finds signals that come and go, but an always-on transmitter (an FM
/// station, a carrier) holds its bin at the same level every sweep and so becomes its own floor;
/// it never stands out. Its neighbours are quieter, though, so a floor taken across the bins
/// around it sits at the real noise level. The detector uses the lower of the two
/// (<see cref="Combine"/>): steady signals stand out against the spatial floor, and intermittent
/// ones against the temporal floor.
///
/// A signal wider than the window is still treated as floor by this estimate, so the window is
/// set from the bin width: about 1 MHz either side, never fewer than 8 bins.
/// </summary>
public static class SpatialFloor
{
    /// <summary>Percentile across the window, matching the temporal floor's 20th.</summary>
    public const double DefaultPercentile = 0.20;

    /// <summary>Hz either side of a bin that the window aims to cover.</summary>
    public const double TargetHalfSpanHz = 1_000_000;

    /// <summary>Smallest half-window in bins.</summary>
    public const int MinHalfWindowBins = 8;

    /// <summary>Largest half-window in bins, which bounds the work at fine resolutions.</summary>
    public const int MaxHalfWindowBins = 256;

    /// <summary>The half-window in bins for a bin width.</summary>
    public static int HalfWindowBins(double binWidthHz) =>
        binWidthHz > 0 && !double.IsNaN(binWidthHz)
            ? (int)Math.Clamp(Math.Round(TargetHalfSpanHz / binWidthHz), MinHalfWindowBins, MaxHalfWindowBins)
            : MinHalfWindowBins;

    /// <summary>
    /// The spatial floor of <paramref name="frame"/>, one value per bin. Empty bins are ignored;
    /// a bin whose window holds fewer than three measured bins gets NaN (floor unknown).
    /// </summary>
    public static double[] Compute(SpectrumFrame frame, int halfWindowBins, double percentile = DefaultPercentile)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegative(halfWindowBins);
        if (percentile is < 0 or > 1 || double.IsNaN(percentile))
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be between 0 and 1.");
        }

        int bins = frame.BinCount;
        var floor = new double[bins];
        var window = new double[(2 * halfWindowBins) + 1];
        for (int i = 0; i < bins; i++)
        {
            int count = 0;
            int from = Math.Max(0, i - halfWindowBins);
            int to = Math.Min(bins - 1, i + halfWindowBins);
            for (int j = from; j <= to; j++)
            {
                float value = frame.PowerDb[j];
                if (value != SpectrumFrame.EmptyBin && !float.IsNaN(value))
                {
                    window[count++] = value;
                }
            }

            if (count < 3)
            {
                floor[i] = double.NaN;
                continue;
            }

            Array.Sort(window, 0, count);
            floor[i] = window[(int)Math.Floor(percentile * (count - 1))];
        }

        return floor;
    }

    /// <summary>
    /// The floor the detector uses: per bin, the lower of the temporal and spatial estimates, or
    /// whichever is known when the other is NaN (before the temporal history has filled, for
    /// example). The result has the length of <paramref name="spatial"/>, which matches the frame.
    /// </summary>
    public static double[] Combine(IReadOnlyList<double> temporal, IReadOnlyList<double> spatial)
    {
        ArgumentNullException.ThrowIfNull(temporal);
        ArgumentNullException.ThrowIfNull(spatial);

        var combined = new double[spatial.Count];
        for (int i = 0; i < combined.Length; i++)
        {
            double t = i < temporal.Count ? temporal[i] : double.NaN;
            double s = spatial[i];
            combined[i] = double.IsNaN(t) ? s : double.IsNaN(s) ? t : Math.Min(t, s);
        }

        return combined;
    }
}
