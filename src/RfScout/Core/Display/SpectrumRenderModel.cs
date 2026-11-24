using System.Globalization;

namespace RfScout.Core.Display;

/// <summary>
/// A point on a hand-drawn plot: a position across the plot and a height up it, both as fractions.
/// Pixels stay in the control, so the same model is correct at 100 %, 150 % and 200 % scaling and a
/// unit test never has to know about DPI.
/// </summary>
public readonly record struct PlotPoint(double XFraction, double YFraction);

/// <summary>
/// What the spectrum plot draws, worked out from a frame and the current view. The control is
/// deliberately dumb: it asks for a model and paints the points it is handed, so the decisions
/// that can go wrong (which bins are visible, how an empty bin is treated, where the floor line
/// sits) live here where a test can check them.
/// </summary>
public sealed class SpectrumPlotModel
{
    /// <summary>Bottom of the vertical scale in dBFS. Fixed, so the trace never jumps when the
    /// colour range changes: only the waterfall's colours follow the range.</summary>
    public const double BottomDb = -160;

    private SpectrumPlotModel()
    {
    }

    /// <summary>Live trace. Empty when there is no frame yet.</summary>
    public IReadOnlyList<PlotPoint> Live { get; private init; } = [];

    /// <summary>Peak-hold trace, merged across sweeps since the plan changed.</summary>
    public IReadOnlyList<PlotPoint> PeakHold { get; private init; } = [];

    /// <summary>Noise-floor line, or empty when the estimator has nothing to say.</summary>
    public IReadOnlyList<PlotPoint> Floor { get; private init; } = [];

    /// <summary>dBFS at the top of the plot.</summary>
    public double TopDb { get; private init; }

    /// <summary>dBFS at the bottom of the plot. Fixed at <see cref="BottomDb"/> for an explicit
    /// toolbar range; follows <see cref="DbRange.MinDb"/> while auto-ranging, so the axis labels
    /// match the display range instead of showing -160 to -40 while Auto range reads -59 to -38.</summary>
    public double BottomDbValue { get; private init; } = BottomDb;

    /// <summary>dBFS at the bottom of the plot for a fixed-scale model (an explicit toolbar range).</summary>
    public static double FixedBottomDb => BottomDb;

    /// <summary>Frequency at the left edge of the visible slice.</summary>
    public double ViewStartHz { get; private init; }

    /// <summary>Frequency at the right edge of the visible slice.</summary>
    public double ViewStopHz { get; private init; }

    /// <summary>How many bins of the frame fall inside the view.</summary>
    public int VisibleBins { get; private init; }

    /// <summary>True when a frame has been drawn at all.</summary>
    public bool HasData => Live.Count > 0;

    /// <summary>An empty model, painted before the first frame arrives.</summary>
    public static SpectrumPlotModel Empty => new() { TopDb = -20 };

    /// <summary>
    /// Build the three traces for <paramref name="view"/> from <paramref name="frame"/> (the latest
    /// sweep) and <paramref name="peakHold"/> (the merged maximum). Either may be null before the
    /// first frame arrives. <paramref name="floorDb"/> is the noise-floor estimate, one value per
    /// bin, or empty.
    /// </summary>
    public static SpectrumPlotModel Build(
        SpectrumFrame? frame,
        SpectrumFrame? peakHold,
        IReadOnlyList<double>? floorDb,
        SpectrumViewport view,
        DbRange range)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(range);

        double top = range.MaxDb;
        double bottom = range.AutoScaled ? range.MinDb : BottomDb;
        if (frame is null || frame.BinCount == 0)
        {
            return new SpectrumPlotModel
            {
                TopDb = top,
                BottomDbValue = bottom,
                ViewStartHz = view.ViewStartHz,
                ViewStopHz = view.ViewStopHz,
            };
        }

        int first = frame.BinIndexAt(view.ViewStartHz);
        int last = frame.BinIndexAt(view.ViewStopHz);
        if (last < first)
        {
            return new SpectrumPlotModel
            {
                TopDb = top,
                BottomDbValue = bottom,
                ViewStartHz = view.ViewStartHz,
                ViewStopHz = view.ViewStopHz,
            };
        }

        var live = new List<PlotPoint>(last - first + 2);
        var hold = new List<PlotPoint>(last - first + 2);
        var floor = new List<PlotPoint>(last - first + 2);
        IReadOnlyList<double> floors = floorDb ?? [];

        for (int i = first; i <= last; i++)
        {
            double x = view.FractionOf(frame.CenterFrequencyOfBin(i));

            float value = frame.PowerDb[i];
            if (value != SpectrumFrame.EmptyBin)
            {
                live.Add(new PlotPoint(x, Height(value, top, bottom)));
            }

            float held = peakHold is not null && peakHold.BinCount == frame.BinCount
                ? peakHold.PowerDb[i]
                : SpectrumFrame.EmptyBin;
            if (held != SpectrumFrame.EmptyBin)
            {
                hold.Add(new PlotPoint(x, Height(held, top, bottom)));
            }

            if (i < floors.Count && !double.IsNaN(floors[i]))
            {
                floor.Add(new PlotPoint(x, Height(floors[i], top, bottom)));
            }
        }

        return new SpectrumPlotModel
        {
            TopDb = top,
            BottomDbValue = bottom,
            Live = live,
            PeakHold = hold,
            Floor = floor,
            ViewStartHz = view.ViewStartHz,
            ViewStopHz = view.ViewStopHz,
            VisibleBins = last - first + 1,
        };
    }

    /// <summary>Height of a dB value on the vertical scale (top and bottom of the current axis),
    /// clamped into 0..1.</summary>
    private static double Height(double db, double topDb, double bottomDb)
    {
        double span = topDb - bottomDb;
        if (span <= 0 || double.IsNaN(db))
        {
            return 0;
        }

        return Math.Clamp((db - bottomDb) / span, 0, 1);
    }

    /// <summary>
    /// The dBFS a pointer at <paramref name="yFraction"/> is over on this plot's vertical scale, so
    /// the hover readout shows an axis value rather than a pixel.
    /// </summary>
    public double DbAtHeight(double yFraction) =>
        BottomDbValue + (Math.Clamp(yFraction, 0, 1) * (TopDb - BottomDbValue));
}

/// <summary>
/// The text a plot shows while the pointer is over it, and the span a double-click asks for. Both
/// are pure, so the readout can be tested without a window; the same string is handed to
/// AutomationProperties so Narrator announces what a sighted user reads off the plot.
/// </summary>
public static class PlotReadout
{
    /// <summary>
    /// Hover text for a pointer at (<paramref name="xFraction"/>, <paramref name="yFraction"/>)
    /// over <paramref name="frame"/>. The frequency comes from the viewport, so it stays correct
    /// while zoomed; the power comes from the bin under the pointer, so it is a measurement rather
    /// than whatever the mouse happened to be vertically.
    /// </summary>
    public static string Hover(SpectrumFrame? frame, SpectrumViewport view, double xFraction, double yFraction)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (frame is null || frame.FilledBins == 0)
        {
            return "No sweep yet";
        }

        // QA defect 1: while a sweep is running the label above the plot must say so, not
        // "No sweep yet". The hover text is the label, so state the sweep state here.
        if (!frame.SweepCompleted)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Sweeping {0} to {1}",
                FrequencyFormat.MHz(frame.StartHz),
                FrequencyFormat.MHz(frame.StopHz));
        }

        double hz = view.HzAt(xFraction);
        int bin = frame.BinIndexAt(hz);
        float power = frame.PowerDb[bin];
        string powerText = power == SpectrumFrame.EmptyBin ? "no data" : FrequencyFormat.Db(power);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}  {1}",
            FrequencyFormat.MHz(hz),
            powerText);
    }

    /// <summary>
    /// A short summary of the whole span for the plot's automation help text: where the strongest
    /// signal in the last sweep is, and how full the grid is. The plots are hand-drawn, so without
    /// something like this the information exists only as pixels.
    /// </summary>
    public static string SpanSummary(SpectrumFrame? frame)
    {
        if (frame is null || frame.FilledBins == 0)
        {
            return "Waiting for the first sweep.";
        }

        int best = -1;
        float bestDb = float.MinValue;
        for (int i = 0; i < frame.BinCount; i++)
        {
            float value = frame.PowerDb[i];
            if (value != SpectrumFrame.EmptyBin && value > bestDb)
            {
                bestDb = value;
                best = i;
            }
        }

        if (best < 0)
        {
            return "Waiting for the first sweep.";
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:F0} of {1:F0} bins measured. Strongest {2} at {3}.",
            frame.FilledBins,
            frame.BinCount,
            FrequencyFormat.Db(bestDb),
            FrequencyFormat.MHz(frame.CenterFrequencyOfBin(best)));
    }

    /// <summary>
    /// The span a double-click asks for: a tenth of the span currently shown, a clear "zoom in
    /// hard here" that still leaves the signal on screen. The viewport clamps it to what the bin
    /// width allows.
    /// </summary>
    public static double DoubleClickSpanHz(SpectrumViewport view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return Math.Max(SpectrumViewport.AbsoluteMinSpanHz, view.SpanHz / 10);
    }
}
