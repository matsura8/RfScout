using RfScout.Core;
using RfScout.Core.Display;

namespace RfScout.Tests;

/// <summary>
/// The colour and zoom layer sits between the numbers and the screen, so its invariants are what
/// keep a waterfall honest: the ramp must be the real viridis, the auto-range must follow the
/// energy without hiding a peak, and zooming must never show a frequency that is not what the
/// pointer was over.
/// </summary>
public sealed class DisplayLayerTests
{
    // ---- viridis table ----

    [Fact]
    public void ViridisTableHasTheCanonicalEndpoints()
    {
        byte[] table = ColorMap.Lookup(ColorMapKind.Viridis);
        Assert.Equal(ColorMap.Entries * 3, table.Length);

        // #440154 and #FDE725 are the documented first and last colours of matplotlib's viridis.
        Assert.Equal(((byte)68, (byte)1, (byte)84), ColorMap.Entry(0, ColorMapKind.Viridis));
        Assert.Equal(((byte)253, (byte)231, (byte)37), ColorMap.Entry(ColorMap.Entries - 1, ColorMapKind.Viridis));

        // The exact middle of the ramp is the well-known teal #21918C. Pinning a middle entry too
        // means a transcription slip in the body of the string cannot pass on endpoints alone.
        Assert.Equal(((byte)33, (byte)145, (byte)140), ColorMap.Entry(ColorMap.Entries / 2, ColorMapKind.Viridis));
    }

    [Fact]
    public void ViridisLuminanceRisesMonotonically()
    {
        // Perceptual uniformity is the reason this ramp was chosen over a rainbow: equal dB steps
        // must look like equal brightness steps, and brightness must never go back down. A wrong
        // byte in the table shows up here as a local dip.
        double previous = double.MinValue;
        for (int i = 0; i < ColorMap.Entries; i++)
        {
            (byte r, byte g, byte b) = ColorMap.Entry(i, ColorMapKind.Viridis);
            double luminance = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
            Assert.True(
                luminance > previous - 1.0,
                $"viridis goes darker at entry {i}: {luminance:F1} after {previous:F1}");
            previous = Math.Max(previous, luminance);
        }
    }

    [Fact]
    public void GrayscaleIsAMonotoneRamp()
    {
        for (int i = 0; i < ColorMap.Entries; i += 17)
        {
            Assert.Equal(((byte)i, (byte)i, (byte)i), ColorMap.Entry(i, ColorMapKind.Grayscale));
        }
    }

    [Fact]
    public void IndexClampsAndTreatsNaNAsTheBottom()
    {
        Assert.Equal(0, ColorMap.IndexOf(double.NaN));
        Assert.Equal(0, ColorMap.IndexOf(-1));
        Assert.Equal(0, ColorMap.IndexOf(0));
        Assert.Equal(ColorMap.Entries - 1, ColorMap.IndexOf(1));
        Assert.Equal(ColorMap.Entries - 1, ColorMap.IndexOf(50));
        // 0.5 maps to (int)(0.5 * 255) = 127, one short of the middle entry: the LUT spans
        // 0..255 inclusive, so the exact centre of the ramp is index 127.5.
        Assert.Equal(127, ColorMap.IndexOf(0.5));
    }

    [Fact]
    public void WritePixelEmitsBgraWithAnOpaqueAlpha()
    {
        byte[] pixel = new byte[4];
        ColorMap.WritePixel(pixel, 0, ColorMapKind.Viridis);
        Assert.Equal(0x54, pixel[0]);   // blue first: this is what Bgra32 wants
        Assert.Equal(0x01, pixel[1]);
        Assert.Equal(0x44, pixel[2]);
        Assert.Equal(255, pixel[3]);
    }

    // ---- dB range ----

    [Fact]
    public void NormalizeMapsTheEndsOfTheRangeAndClampsOutside()
    {
        var range = new DbRange(-100, -40);
        Assert.Equal(0, range.Normalize(-100), 6);
        Assert.Equal(1, range.Normalize(-40), 6);
        Assert.Equal(0.5, range.Normalize(-70), 6);
        Assert.Equal(0, range.Normalize(-200), 6);
        Assert.Equal(1, range.Normalize(0), 6);
        Assert.Equal(0, range.Normalize(double.NaN), 6);
        Assert.Equal(60, range.SpanDb, 6);
    }

    [Fact]
    public void SetCorrectsAReversedRangeInsteadOfRejectingIt()
    {
        // The toolbar can hand over min > max while the operator is mid-typing, and a plot with a
        // negative span draws nothing at all.
        var range = new DbRange();
        range.Set(-20, -100);
        Assert.Equal(-100, range.MinDb, 6);
        Assert.Equal(-20, range.MaxDb, 6);
        Assert.Equal(1, range.Normalize(-20), 6);
    }

    [Fact]
    public void DbAtInvertsNormalize()
    {
        var range = new DbRange(-90, -30);
        foreach (double db in new[] { -90.0, -72.5, -31.25 })
        {
            Assert.Equal(db, range.DbAt(range.Normalize(db)), 4);
        }
    }

    [Fact]
    public void AutoRangeFollowsTheEnergyAndIgnoresEmptyBins()
    {
        // A realistic sweep: a floor at -95 with a handful of signals up to -40, and the rest of
        // the grid still empty because this is the first pass over the span.
        float[] bins = new float[512];
        for (int i = 0; i < bins.Length; i++)
        {
            bins[i] = i % 4 == 0 ? SpectrumFrame.EmptyBin : -95f;
        }

        bins[100] = -60;
        bins[200] = -45;
        bins[300] = -40;

        var range = new DbRange(-140, -20);
        Assert.True(range.AutoRange(bins, bins.Length));

        // The floor must sit near the bottom of the ramp and the strongest signal near the top,
        // otherwise the waterfall is either all one colour or all black.
        Assert.True(range.MinDb is > -100 and < -90, $"floor at {range.MinDb:F1} dBFS");
        // The 99.5th percentile of 381 floor bins plus three signals falls between the
        // -60 and -45 dB signals, so the top of the ramp lands just under the weakest of them.
        Assert.True(range.MaxDb is > -65 and < -55, $"top at {range.MaxDb:F1} dBFS");
        Assert.True(range.Normalize(-95) < 0.15, "the floor should be near the bottom of the ramp");
        Assert.True(range.Normalize(-40) > 0.9, "the strongest signal should be near the top");
    }

    [Fact]
    public void AutoRangeKeepsThePreviousRangeWhenThereIsNothingToJudge()
    {
        var range = new DbRange(-80, -50);
        float[] empty = [SpectrumFrame.EmptyBin, SpectrumFrame.EmptyBin, SpectrumFrame.EmptyBin];
        Assert.False(range.AutoRange(empty, empty.Length));
        Assert.Equal(-80, range.MinDb, 6);
        Assert.Equal(-50, range.MaxDb, 6);

        Assert.False(range.AutoRange([], 0));
    }

    [Fact]
    public void AutoRangeScansTheWholeGridNotAFilledPrefix()
    {
        // FilledBins counts populated bins anywhere in the frame. Here only the last 20 of 100
        // bins hold data, so treating the count as a prefix length would see nothing at all.
        float[] bins = new float[100];
        Array.Fill(bins, SpectrumFrame.EmptyBin);
        for (int i = 80; i < 100; i++)
        {
            bins[i] = -90f + (i - 80);
        }

        var range = new DbRange(-140, -20);
        Assert.True(range.AutoRange(bins, filledBins: 20));
        Assert.True(range.MinDb is > -100 and < -85, $"floor at {range.MinDb:F1} dBFS");
    }

    [Fact]
    public void AutoRangeNeverProducesAnUnreadablyNarrowOrWideSpan()
    {
        // A dead-flat floor would otherwise collapse the ramp onto a couple of dB and turn the
        // waterfall into noise from rounding alone.
        var range = new DbRange();
        float[] flat = Enumerable.Repeat(-95f, 200).ToArray();
        Assert.True(range.AutoRange(flat, flat.Length));
        Assert.True(range.SpanDb >= DbRange.MinSpanDb, $"span {range.SpanDb:F1} dB");

        // One huge spike among a quiet floor must not stretch the ramp so far that the floor
        // disappears either.
        float[] spiky = Enumerable.Repeat(-100f, 400).ToArray();
        spiky[0] = 0;
        Assert.True(range.AutoRange(spiky, spiky.Length));
        Assert.True(range.SpanDb <= DbRange.MaxSpanDb, $"span {range.SpanDb:F1} dB");
    }

    // ---- QA defect 3: the plot's dB axis must follow the display range while auto-ranging ----

    [Fact]
    public void AutoRangeMarksTheRangeAutoScaledAndSetClearsIt()
    {
        var range = new DbRange(-140, -20);
        Assert.False(range.AutoScaled);
        float[] bins = Enumerable.Range(0, 200).Select(i => -95f + (i % 30)).Select(v => (float)v).ToArray();
        Assert.True(range.AutoRange(bins, bins.Length));
        Assert.True(range.AutoScaled, "AutoRange must mark the range auto-scaled");
        range.Set(-120, -30);
        Assert.False(range.AutoScaled, "an explicit toolbar range is not auto-scaled");
    }

    [Fact]
    public void PlotModelBottomFollowsTheAutoRangeAndStaysFixedOtherwise()
    {
        float[] power = [-50f, -45f, -40f, -38f];
        SpectrumFrame frame = new()
        {
            StartHz = 88_000_000,
            StopHz = 88_050_000,
            BinWidthHz = 12_500,
            PowerDb = power,
            FilledBins = power.Length,
            SweepCompleted = true,
            UtcTimestamp = DateTimeOffset.UnixEpoch,
        };
        var view = new SpectrumViewport(frame.StartHz, frame.StopHz);

        // Explicit toolbar range: the fixed -160 dBFS floor keeps trace heights stable.
        var manual = new DbRange(-140, -20);
        SpectrumPlotModel fixedModel = SpectrumPlotModel.Build(frame, frame, [], view, manual);
        Assert.Equal(SpectrumPlotModel.BottomDb, fixedModel.BottomDbValue);

        // Auto-ranged: the axis must read the display range (eg -59..-38), not -160..-40.
        // Four bins are too few for AutoRange's "nothing to judge" guard, so use a realistic
        // floor-plus-signals grid like the AutoRange tests do.
        float[] bins = new float[512];
        for (int i = 0; i < bins.Length; i++)
        {
            bins[i] = -59f + (i % 21);
        }

        var auto = new DbRange(-140, -20);
        Assert.True(auto.AutoRange(bins, bins.Length));
        SpectrumFrame wide = new()
        {
            StartHz = 88_000_000,
            StopHz = 88_000_000 + (bins.Length * 12_500),
            BinWidthHz = 12_500,
            PowerDb = bins,
            FilledBins = bins.Length,
            SweepCompleted = true,
            UtcTimestamp = DateTimeOffset.UnixEpoch,
        };
        var wideView = new SpectrumViewport(wide.StartHz, wide.StopHz);
        SpectrumPlotModel autoModel = SpectrumPlotModel.Build(wide, wide, [], wideView, auto);
        Assert.Equal(auto.MinDb, autoModel.BottomDbValue, 3);
        Assert.Equal(auto.MaxDb, autoModel.TopDb, 3);
        Assert.True(autoModel.BottomDbValue > -100, "the auto axis bottom must follow the data, not the fixed floor");

        // The hover readout uses the same axis: mid-height reads mid-range, not -100.
        double mid = autoModel.DbAtHeight(0.5);
        Assert.True(Math.Abs(mid - ((auto.MinDb + auto.MaxDb) / 2)) < 1.0, $"mid-height read {mid:F1} dB");
    }

    // ---- viewport ----

    [Fact]
    public void ANewViewShowsTheWholeSweptSpan()
    {
        var view = new SpectrumViewport(88_000_000, 108_000_000);
        Assert.Equal(88_000_000, view.ViewStartHz, 3);
        Assert.Equal(108_000_000, view.ViewStopHz, 3);
        Assert.Equal(98_000_000, view.CenterHz, 3);
        Assert.False(view.IsZoomed);
    }

    [Fact]
    public void RejectsASpanThatEndsWhereItStarts() =>
        Assert.Throws<ArgumentException>(() => new SpectrumViewport(100_000_000, 100_000_000));

    [Fact]
    public void MappingBetweenFrequencyAndViewFractionRoundTrips()
    {
        var view = new SpectrumViewport(400_000_000, 470_000_000);
        foreach (double fraction in new[] { 0.0, 0.25, 0.5, 0.999 })
        {
            Assert.Equal(fraction, view.FractionOf(view.HzAt(fraction)), 6);
        }

        Assert.Equal(400_000_000, view.HzAt(0), 3);
        Assert.Equal(470_000_000, view.HzAt(1), 3);
        Assert.True(view.IsVisible(435_000_000));
        Assert.False(view.IsVisible(500_000_000));
    }

    [Fact]
    public void ZoomKeepsTheFrequencyUnderThePointerStill()
    {
        var view = new SpectrumViewport(1_000_000, 6_000_000_000);
        double pointer = 0.25;
        double underPointer = view.HzAt(pointer);

        view.ZoomAt(pointer, 0.5);
        Assert.Equal(underPointer, view.HzAt(pointer), 1);

        view.ZoomAt(pointer, 0.5);
        Assert.Equal(underPointer, view.HzAt(pointer), 1);

        // Zooming back out by the same factors returns to the original view.
        view.ZoomAt(pointer, 2);
        view.ZoomAt(pointer, 2);
        Assert.False(view.IsZoomed);
        Assert.Equal(1_000_000, view.ViewStartHz, 3);
        Assert.Equal(6_000_000_000, view.ViewStopHz, 3);
    }

    [Fact]
    public void ZoomStopsAtTheSweptEdgesRatherThanShowingBlankSky()
    {
        var view = new SpectrumViewport(88_000_000, 108_000_000, minSpanHz: 100_000);

        // Pointer hard left, zoomed in: the left edge cannot move further left than the sweep.
        view.ZoomAt(0, 0.25);
        Assert.True(view.ViewStartHz >= 88_000_000 - 1, $"view starts at {view.ViewStartHz:F0}");
        Assert.Equal(20_000_000 * 0.25, view.SpanHz, 3);

        // Pointer hard right, same zoom: the right edge stops at the sweep's top.
        var other = new SpectrumViewport(88_000_000, 108_000_000, minSpanHz: 100_000);
        other.ZoomAt(1, 0.25);
        Assert.True(other.ViewStopHz <= 108_000_000 + 1, $"view stops at {other.ViewStopHz:F0}");
    }

    [Fact]
    public void ZoomingOutPastTheSweepAndInPastTheBinWidthBothStop()
    {
        var view = new SpectrumViewport(1_000_000, 6_000_000_000, minSpanHz: 200_000);
        view.SetSpan(3_000_000_000, 10_000_000);

        view.ZoomAt(0.5, 1000);
        Assert.Equal(view.FullSpanHz, view.SpanHz, 3);

        view.ZoomAt(0.5, 1e-6);
        Assert.Equal(200_000, view.SpanHz, 3);

        // Nonsense factors change nothing rather than collapsing the view.
        var sane = new SpectrumViewport(1_000_000, 6_000_000_000);
        double before = sane.SpanHz;
        sane.ZoomAt(0.5, 0);
        sane.ZoomAt(0.5, double.NaN);
        sane.ZoomAt(0.5, double.PositiveInfinity);
        sane.ZoomAt(0.5, -2);
        Assert.Equal(before, sane.SpanHz, 6);
    }

    [Fact]
    public void DraggingMovesTheViewButStopsAtTheEdges()
    {
        var view = new SpectrumViewport(88_000_000, 108_000_000, minSpanHz: 1_000_000);
        view.SetSpan(98_000_000, 4_000_000);

        view.PanPixels(-100, 1_000); // drag left: the view slides up in frequency
        Assert.True(view.CenterHz > 98_000_000);

        view.PanPixels(100, 1_000);
        Assert.Equal(98_000_000, view.CenterHz, 3);

        // A drag far longer than the span cannot push the view off the end of the sweep.
        view.PanPixels(-100_000, 1_000);
        Assert.Equal(108_000_000, view.ViewStopHz, 3);
        view.PanPixels(100_000, 1_000);
        Assert.Equal(88_000_000, view.ViewStartHz, 3);

        // A NaN centre would paint nothing at all, and it is what a divide-by-zero drag returns.
        Assert.False(double.IsNaN(view.CenterHz));
        Assert.False(double.IsNaN(view.SpanHz));
        view.PanPixels(10, 0); // zero-width plot: ignored rather than dividing by zero
        Assert.Equal(88_000_000, view.ViewStartHz, 3);
    }

    [Fact]
    public void DoubleClickRecentresWithoutChangingTheSpan()
    {
        var view = new SpectrumViewport(88_000_000, 108_000_000, minSpanHz: 1_000_000);
        view.SetSpan(98_000_000, 4_000_000);
        view.CenterAt(0.0); // the left edge of this 4 MHz view is 96 MHz
        Assert.Equal(96_000_000, view.CenterHz, 3);
        Assert.Equal(4_000_000, view.SpanHz, 3);

        // Now the view spans 94..98 MHz, so 0.75 across is 97 MHz: inside the band, no clamp.
        view.CenterAt(0.75);
        Assert.Equal(97_000_000, view.CenterHz, 3);
        Assert.Equal(99_000_000, view.ViewStopHz, 3);
    }

    [Fact]
    public void BinWidthSetsHowFarInZoomCanGo()
    {
        var view = new SpectrumViewport(1_000_000, 6_000_000_000);
        view.SetMinSpanFromBinWidth(625_000);
        Assert.Equal(2_500_000, view.MinSpanHz, 3);

        view.ZoomAt(0.5, 1e-9);
        Assert.Equal(2_500_000, view.SpanHz, 3);

        // A bin width wider than the whole sweep cannot be zoomed into at all.
        var narrow = new SpectrumViewport(1_000_000, 2_000_000);
        narrow.SetMinSpanFromBinWidth(625_000);
        narrow.ZoomAt(0.5, 1e-9);
        Assert.Equal(narrow.FullSpanHz, narrow.SpanHz, 3);
    }

    [Fact]
    public void ResetUndoesTheZoom()
    {
        var view = new SpectrumViewport(1_000_000, 6_000_000_000, minSpanHz: 100_000);
        view.SetSpan(433_920_000, 1_000_000);
        Assert.True(view.IsZoomed);
        view.ResetToFullSpan();
        Assert.False(view.IsZoomed);
        Assert.Equal(6_000_000_000 - 1_000_000, view.SpanHz, 3);
    }

    [Fact]
    public void TicksLandOnRoundFrequenciesAndStayInsideTheView()
    {
        // A 20 MHz span over 8 labels wants a raw step of 2.5 MHz, which rounds up to the
        // next nice step, 5 MHz: 90, 95, 100, 105. The step is never finer than span/target, so
        // the labels cannot crowd each other.
        var view = new SpectrumViewport(88_000_000, 108_000_000);
        IReadOnlyList<FrequencyTick> ticks = view.Ticks(8);
        Assert.NotEmpty(ticks);
        Assert.All(ticks, t => Assert.True(t.Hz >= view.ViewStartHz - 1 && t.Hz <= view.ViewStopHz + 1));
        Assert.All(ticks, t => Assert.Equal(0, t.Hz % 5_000_000, 6));
        Assert.Equal(4, ticks.Count); // 90, 95, 100, 105 MHz

        // Steps must increase monotonically or the axis labels would come out in the wrong order.
        for (int i = 1; i < ticks.Count; i++)
        {
            Assert.True(ticks[i].Hz > ticks[i - 1].Hz);
        }

        // Every label has to be something a person can read off the plot.
        Assert.All(ticks, t => Assert.False(string.IsNullOrWhiteSpace(t.Label)));
        Assert.Contains(ticks, t => t.Label.Contains("MHz", StringComparison.Ordinal));
    }

    [Fact]
    public void TickDensityFollowsTheZoom()
    {
        var view = new SpectrumViewport(1_000_000, 6_000_000_000);
        view.SetSpan(433_920_000, 200_000);
        IReadOnlyList<FrequencyTick> ticks = view.Ticks(8);

        Assert.NotEmpty(ticks);
        Assert.All(ticks, t => Assert.Equal(0, t.Hz % 50_000, 6));

        // A 200 kHz span is below 1 GHz but its ticks are still labelled in MHz, which is what an
        // operator reading 433.900 / 433.950 expects.
        Assert.Contains(ticks, t => t.Label.Contains("MHz", StringComparison.Ordinal));
    }

    [Fact]
    public void NiceStepsAreAlwaysOneTwoOrFiveTimesAPowerOfTen()
    {
        double[] probes = [0.3, 1, 3, 7.5, 20, 333, 12_345, 1e6, 3.7e8, 9.99e9];
        foreach (double probe in probes)
        {
            double step = SpectrumViewport.NiceStepHz(probe);
            Assert.True(step >= probe * (1 - 1e-9), $"step {step:E3} is below the requested {probe:E3}");

            double reduced = step / Math.Pow(10, Math.Floor(Math.Log10(step)));
            Assert.True(
                Math.Abs(reduced - 1) < 1e-9 || Math.Abs(reduced - 2) < 1e-9 || Math.Abs(reduced - 5) < 1e-9,
                $"step {step:E3} is not a 1/2/5 decade step (mantissa {reduced:F3})");
        }

        Assert.Equal(0, SpectrumViewport.NiceStepHz(0));
        Assert.Equal(0, SpectrumViewport.NiceStepHz(-5));
        Assert.Equal(0, SpectrumViewport.NiceStepHz(double.NaN));
        Assert.Equal(0, SpectrumViewport.NiceStepHz(double.PositiveInfinity));
    }

    // ---- frequency text ----

    [Fact]
    public void MHzTextAlwaysUsesSixDecimalsAndInvariantCulture()
    {
        // A comma decimal separator would break the column alignment and the copy-to-clipboard
        // round trip, so the text is pinned to InvariantCulture regardless of the machine locale.
        Assert.Equal("88.000000 MHz", FrequencyFormat.MHz(88_000_000));
        Assert.Equal("433.920000 MHz", FrequencyFormat.MHz(433_920_000));
        Assert.Equal("1000.000000 MHz", FrequencyFormat.MHz(1_000_000_000));
    }

    [Theory]
    [InlineData(88_000_000, 2_000_000, "88 MHz")]              // whole MHz for a MHz-scale step
    [InlineData(101_000_000, 2_000_000, "101 MHz")]
    [InlineData(1_000_000_000, 500_000_000, "1.0 GHz")]        // GHz unit, 0.5 GHz step needs one decimal
    [InlineData(2_400_000_000, 200_000_000, "2.4 GHz")]
    [InlineData(433_900_000, 50_000, "433.90 MHz")]            // a 0.05 MHz step needs two decimals
    [InlineData(900_000, 2_000, "900 kHz")]                    // below 1 MHz reads in kHz
    public void TickLabelsPickTheUnitAndDecimalsForTheStep(double hz, double stepHz, string expected) =>
        Assert.Equal(expected, FrequencyFormat.TickLabel(hz, stepHz));

    [Theory]
    [InlineData(78_125, "78 kHz")]
    [InlineData(625_000, "625 kHz")]
    [InlineData(1_250_000, "1.25 MHz")]
    public void BinWidthTextScalesWithTheWidth(double binWidthHz, string expected) =>
        Assert.Equal(expected, FrequencyFormat.BinWidth(binWidthHz));

    [Fact]
    public void DbTextIsFixedWidthSoAColumnLinesUp()
    {
        Assert.Equal("-42.30 dBFS", FrequencyFormat.Db(-42.3));
        Assert.Equal("0.00 dBFS", FrequencyFormat.Db(0));
        Assert.Equal("-140.00 dBFS", FrequencyFormat.Db(-140));
    }
}
