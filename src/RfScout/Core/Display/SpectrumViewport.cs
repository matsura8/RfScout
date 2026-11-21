using System.Globalization;

namespace RfScout.Core.Display;

/// <summary>
/// The visible slice of the spectrum: which part of the swept span the plot is showing after the
/// operator zoomed or panned. Everything is in Hz and in 0..1 view fractions rather than pixels,
/// so one set of numbers drives the spectrum, the waterfall, the peak markers and the axis labels,
/// and all of it is unit testable without a window.
///
/// The view always stays inside the swept span: panning past an edge stops rather than wrapping,
/// and zooming out stops at the full span. That matters because a sweep only fills the grid it was
/// asked for, so anything outside it has no data to draw.
/// </summary>
public sealed class SpectrumViewport
{
    /// <summary>Smallest span worth showing; below this the view is narrower than one bin.</summary>
    public const double AbsoluteMinSpanHz = 1_000;

    /// <summary>Tick mantissas, in the order a readable axis wants them.</summary>
    private static readonly double[] TickMantissas = [1.0, 2.0, 5.0, 10.0];

    private readonly long _minHz;
    private readonly long _maxHz;
    private double _minSpanHz;

    public SpectrumViewport(long minHz, long maxHz, double minSpanHz = AbsoluteMinSpanHz)
    {
        if (maxHz <= minHz)
        {
            throw new ArgumentException($"Span must end above it starts ({minHz}..{maxHz}).", nameof(maxHz));
        }

        _minHz = minHz;
        _maxHz = maxHz;
        _minSpanHz = Math.Clamp(minSpanHz, AbsoluteMinSpanHz, FullSpanHz);
        CenterHz = (minHz + (double)maxHz) / 2;
        SpanHz = FullSpanHz;
    }

    /// <summary>Lowest frequency the sweep covers.</summary>
    public long MinHz => _minHz;

    /// <summary>Highest frequency the sweep covers.</summary>
    public long MaxHz => _maxHz;

    /// <summary>Full swept span, which is also the maximum zoom-out.</summary>
    public double FullSpanHz => _maxHz - (double)_minHz;

    /// <summary>Smallest span this view allows, driven by the sweep's bin width.</summary>
    public double MinSpanHz => _minSpanHz;

    /// <summary>Middle of the visible slice.</summary>
    public double CenterHz { get; private set; }

    /// <summary>Width of the visible slice in Hz.</summary>
    public double SpanHz { get; private set; }

    public double ViewStartHz => CenterHz - (SpanHz / 2);

    public double ViewStopHz => CenterHz + (SpanHz / 2);

    /// <summary>True when the view is narrower than the sweep, i.e. there is a zoom to undo.</summary>
    public bool IsZoomed => SpanHz < FullSpanHz - 0.5;

    /// <summary>Stop zooming in at a few bins wide, so the view never exceeds the data's resolution.</summary>
    public void SetMinSpanFromBinWidth(double binWidthHz)
    {
        _minSpanHz = Math.Clamp(Math.Max(AbsoluteMinSpanHz, binWidthHz * 4), AbsoluteMinSpanHz, FullSpanHz);
        Clamp();
    }

    /// <summary>Zoom to a span, keeping the view inside the swept region.</summary>
    public void SetSpan(double centerHz, double spanHz)
    {
        SpanHz = Math.Clamp(spanHz, _minSpanHz, FullSpanHz);
        CenterHz = centerHz;
        Clamp();
    }

    /// <summary>
    /// Wheel zoom about the pointer. <paramref name="viewFraction"/> is where the pointer sits
    /// across the plot (0 left, 1 right) and <paramref name="factor"/> scales the span, so 0.5
    /// zooms in by two and 2 zooms out by two. The frequency under the pointer stays under it,
    /// which is what makes wheel zoom usable.
    /// </summary>
    public void ZoomAt(double viewFraction, double factor)
    {
        if (factor <= 0 || double.IsNaN(factor) || double.IsInfinity(factor))
        {
            return;
        }

        double anchorHz = HzAt(viewFraction);
        double newSpan = Math.Clamp(SpanHz * factor, _minSpanHz, FullSpanHz);
        if (Math.Abs(newSpan - SpanHz) < 1e-9)
        {
            return; // already at a limit, so there is nothing to move
        }

        SpanHz = newSpan;
        CenterHz = anchorHz - ((Math.Clamp(viewFraction, 0, 1) - 0.5) * newSpan);
        Clamp();
    }

    /// <summary>Drag by pixels across a plot <paramref name="pixelWidth"/> wide. Positive delta drags right.</summary>
    public void PanPixels(double pixelDelta, double pixelWidth)
    {
        if (pixelWidth <= 0)
        {
            return;
        }

        PanHz(-pixelDelta * SpanHz / pixelWidth);
    }

    /// <summary>Shift the centre by a frequency delta, stopping at the swept edges.</summary>
    public void PanHz(double deltaHz)
    {
        if (double.IsNaN(deltaHz))
        {
            return;
        }

        CenterHz += deltaHz;
        Clamp();
    }

    /// <summary>Double-click: recentre on the clicked frequency without changing the span.</summary>
    public void CenterAt(double viewFraction)
    {
        CenterHz = HzAt(viewFraction);
        Clamp();
    }

    /// <summary>Zoom so a given span, centred on a view fraction, fills the plot.</summary>
    public void Focus(double viewFraction, double spanHz) => SetSpan(HzAt(viewFraction), spanHz);

    /// <summary>Back out to the whole swept span.</summary>
    public void ResetToFullSpan() => SetSpan((_minHz + (double)_maxHz) / 2, FullSpanHz);

    /// <summary>Frequency at a position across the plot.</summary>
    public double HzAt(double viewFraction) => ViewStartHz + (Math.Clamp(viewFraction, 0, 1) * SpanHz);

    /// <summary>Position across the plot for a frequency; outside the view it clamps to the edge.</summary>
    public double FractionOf(double hz) => Math.Clamp((hz - ViewStartHz) / SpanHz, 0, 1);

    /// <summary>True when a frequency is inside the visible slice, used to skip off-screen markers.</summary>
    public bool IsVisible(double hz) => hz >= ViewStartHz && hz <= ViewStopHz;

    /// <summary>
    /// Axis labels at round frequencies. The step is the smallest of 1, 2 or 5 times a power of ten
    /// that keeps the tick count at or below the target, so a 20 MHz span gets 2 MHz ticks and a
    /// 20 kHz span gets 2 kHz ones: the axis stays readable whatever the zoom.
    /// </summary>
    public IReadOnlyList<FrequencyTick> Ticks(int targetCount = 8)
    {
        targetCount = Math.Clamp(targetCount, 2, 40);
        double step = NiceStepHz(SpanHz / targetCount);
        if (step <= 0)
        {
            return [];
        }

        List<FrequencyTick> ticks = [];
        double first = Math.Ceiling(ViewStartHz / step) * step;
        for (double hz = first; hz <= ViewStopHz + (step * 1e-9); hz += step)
        {
            // A decade step is the major gridline; 2x and 5x inside it are minors. That is how a
            // frequency axis reads, and it keeps the labels from crowding each other.
            ticks.Add(new FrequencyTick(hz, FrequencyFormat.TickLabel(hz, step), IsDecadeMultiple(step)));
            if (ticks.Count > 64)
            {
                break; // guards against a runaway loop from a denormal step
            }
        }

        return ticks;
    }

    private static bool IsDecadeMultiple(double step)
    {
        double mantissa = step / Math.Pow(10, Math.Floor(Math.Log10(step)));
        return Math.Abs(mantissa - 1) < 1e-9;
    }

    private void Clamp()
    {
        SpanHz = Math.Clamp(SpanHz, Math.Min(_minSpanHz, FullSpanHz), FullSpanHz);

        double half = SpanHz / 2;
        double lo = _minHz + half;
        double hi = _maxHz - half;
        if (lo > hi)
        {
            // The span is the whole sweep, so the centre is fixed and cannot be panned.
            CenterHz = (_minHz + (double)_maxHz) / 2;
            return;
        }

        CenterHz = Math.Clamp(CenterHz, lo, hi);
    }

    /// <summary>Smallest 1, 2 or 5 times a power of ten that is at least <paramref name="atLeastHz"/>.</summary>
    internal static double NiceStepHz(double atLeastHz)
    {
        if (atLeastHz <= 0 || double.IsNaN(atLeastHz) || double.IsInfinity(atLeastHz))
        {
            return 0;
        }

        double power = Math.Pow(10, Math.Floor(Math.Log10(atLeastHz)));
        foreach (double mantissa in TickMantissas)
        {
            double candidate = mantissa * power;
            if (candidate >= atLeastHz * (1 - 1e-12))
            {
                return candidate;
            }
        }

        return 10 * power;
    }
}

/// <summary>One axis label: where it goes and what it says.</summary>
public readonly record struct FrequencyTick(double Hz, string Label, bool Major);

/// <summary>
/// Frequency text. Hz stays the internal unit everywhere; this is only presentation, and it is
/// shared by the axis, the peak table, the status bar and the detail pane so that a frequency
/// never reads one way in one place and another way somewhere else.
/// </summary>
public static class FrequencyFormat
{
    /// <summary>e.g. "88.000000 MHz", the form used wherever a precise frequency is shown.</summary>
    public static string MHz(double hz) =>
        (hz / 1e6).ToString("F6", CultureInfo.InvariantCulture) + " MHz";

    /// <summary>e.g. "433.920 MHz", with just as many decimals as the tick step needs.</summary>
    public static string TickLabel(double hz, double stepHz)
    {
        // The unit and the number of decimals are chosen together, from the step. Deriving the
        // decimals from the step in MHz and then dividing the printed number by 1000 for GHz
        // throws away exactly the digits that tell the ticks apart, which is how a 200 MHz step
        // ended up labelling 2.4 GHz and 2.6 GHz both as "2 GHz".
        double scale;
        string unit;
        if (Math.Abs(hz) >= 1e9)
        {
            // 1000.000 MHz reads better as 1.000 GHz, and the switch sits at 1 GHz exactly, which
            // is also where the sweep presets change band.
            scale = 1e9;
            unit = "GHz";
        }
        else if (Math.Abs(hz) < 1e6)
        {
            scale = 1e3;
            unit = "kHz";
        }
        else
        {
            scale = 1e6;
            unit = "MHz";
        }

        double inUnit = hz / scale;
        int decimals = DecimalsFor(stepHz / scale);
        if (decimals == 0 && Math.Abs(inUnit) < 1 && inUnit != 0)
        {
            decimals = 1; // stops a sub-unit value printing as "-0" or "0"
        }

        return inUnit.ToString("F" + decimals, CultureInfo.InvariantCulture) + " " + unit;
    }

    /// <summary>Bin width text: "25 kHz", "625 kHz" or "1.25 MHz" depending on size.</summary>
    public static string BinWidth(double binWidthHz) =>
        binWidthHz >= 1e6
            ? (binWidthHz / 1e6).ToString("F2", CultureInfo.InvariantCulture) + " MHz"
            : (binWidthHz / 1e3).ToString("F0", CultureInfo.InvariantCulture) + " kHz";

    /// <summary>Duration text for the status bar and capture list, e.g. "1.20 s".</summary>
    public static string Seconds(double seconds) => seconds.ToString("F2", CultureInfo.InvariantCulture) + " s";

    /// <summary>
    /// Decimal places that make a tick step visible, for a step already expressed in the unit the
    /// label prints in: a 2-unit step needs none, a 0.2-unit step needs one, a 0.002-unit step
    /// needs three. One decimal per decade below 1, never more than six.
    /// </summary>
    private static int DecimalsFor(double stepInUnit)
    {
        if (stepInUnit <= 0 || double.IsNaN(stepInUnit) || double.IsInfinity(stepInUnit))
        {
            return 3;
        }

        if (stepInUnit >= 1)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Ceiling(-Math.Log10(stepInUnit)), 0, 6);
    }

    /// <summary>dBFS text with fixed decimals so a column of levels lines up.</summary>
    public static string Db(double db) => db.ToString("F2", CultureInfo.InvariantCulture) + " dBFS";
}
