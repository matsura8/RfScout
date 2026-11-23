using System;
using System.IO;
using System.Linq;
using RfScout.Core;
using RfScout.Core.Analysis;
using RfScout.Core.Display;
using RfScout.Core.Dsp;
using RfScout.Core.Recording;
using RfScout.Core.Spectrum;
using Xunit;

namespace RfScout.Tests;

/// <summary>
/// Release QA coverage for display and DSP paths that milestone tests missed: phase unwrapping
/// edges, waterfall and colour-map robustness, auto-range behaviour (QA defect 3), peak bandwidth
/// classes, and the capture metadata sidecar.
/// </summary>
public sealed class DeepCoverageTests
{
    // ---------- IqMath.UnwrapPhase edge cases ----------

    [Fact]
    public void UnwrapPhaseTwoElementJumpWrapsOnce()
    {
        // +2.9 -> -2.9 rad is a +3.4 jump; unwrapping should add 2*pi to the second sample.
        double[] phase = [2.9, -2.9];
        IqMath.UnwrapPhase(phase);
        Assert.Equal(2.9, phase[0], 9);
        Assert.Equal(-2.9 + (2 * Math.PI), phase[1], 9);
    }

    [Fact]
    public void UnwrapPhaseExactlyPiBoundaryDoesNotWrap()
    {
        // A jump of exactly +pi is below the strict threshold, so no correction.
        double[] phase = [0.0, Math.PI];
        IqMath.UnwrapPhase(phase);
        Assert.Equal(Math.PI, phase[1], 9);
    }

    [Fact]
    public void UnwrapPhaseAccumulatesOverManyWraps()
    {
        // Ramp of 0.1..(n-1)*0.1 sampled modulo 2*pi should unwrap back to the ramp.
        const int N = 200;
        double[] phase = new double[N];
        for (int i = 0; i < N; i++)
        {
            double target = i * 0.1;
            phase[i] = (target % (2 * Math.PI)) - (target % (2 * Math.PI) > Math.PI ? 2 * Math.PI : 0);
        }

        IqMath.UnwrapPhase(phase);
        Assert.Equal((N - 1) * 0.1, phase[N - 1], 5);
    }

    // ---------- WaterfallModel / ColorMap / DbRange ----------

    [Fact]
    public void WaterfallFillRowHandlesNaNAndInfiniteValues()
    {
        const int Columns = 8;
        byte[] bgra = new byte[Columns * 4];
        float[] powerDb =
        [
            float.NaN,
            float.NegativeInfinity,
            float.PositiveInfinity,
            -100f,
            -60f,
            -20f,
            -140f,
            0f,
        ];
        DbRange range = new(-120, -20);

        // Every pixel alpha byte must be 0xFF regardless of input sanity.
        WaterfallModel.FillRow(bgra, powerDb, range, ColorMap.Lookup(ColorMapKind.Viridis), Columns);
        for (int i = 3; i < bgra.Length; i += 4)
        {
            Assert.Equal(0xFF, bgra[i]);
        }
    }

    [Fact]
    public void WaterfallWriteRowAndTryCopyRowRoundTrip()
    {
        WaterfallModel model = new();
        model.Resize(16, 4);

        float[] row = Enumerable.Range(0, 16).Select(i => -120f + (i * 6f)).ToArray();
        DbRange range = new(-120, -20);

        int newest = model.WriteRow(row, range);
        Assert.Equal(1, model.Writes);
        Assert.Equal(newest, model.NewestRow);

        byte[] copy = new byte[model.BytesPerRow];
        Assert.True(model.TryCopyRow(newest, copy));
        Assert.False(model.TryCopyRow(newest, copy[..4])); // too small
        Assert.False(model.TryCopyRow(99, copy)); // out of range

        byte[] snapshot = model.Snapshot();
        Assert.Equal(model.Columns * model.Rows * 4, snapshot.Length);
    }

    [Fact]
    public void WaterfallClearResetsWritesAndSnapshotIsBlank()
    {
        WaterfallModel model = new();
        model.Resize(8, 3);
        model.WriteRow(Enumerable.Repeat(-60f, 8).ToArray(), new DbRange(-120, -20));
        model.Clear();

        Assert.Equal(0, model.Writes);
        Assert.All(model.Snapshot(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ColorMapLookupIsMonotonicLuminanceForViridis()
    {
        byte[] viridis = ColorMap.Lookup(ColorMapKind.Viridis);
        Assert.Equal(ColorMap.Entries * 4, viridis.Length);

        // First and last entries are the endpoints of the ramp.
        Assert.Equal(0, ColorMap.IndexOf(0.0));
        Assert.Equal(ColorMap.Entries - 1, ColorMap.IndexOf(1.0));
        Assert.Equal(0, ColorMap.IndexOf(-5.0)); // clamped low
        Assert.Equal(ColorMap.Entries - 1, ColorMap.IndexOf(5.0)); // clamped high

        (byte r0, byte g0, byte b0) = ColorMap.Entry(0, ColorMapKind.Viridis);
        (byte r1, byte g1, byte b1) = ColorMap.Entry(ColorMap.Entries - 1, ColorMapKind.Viridis);
        Assert.True((int)r0 + g0 + b0 < (int)r1 + g1 + b1);
    }

    [Fact]
    public void ColorMapWritePixelMatchesEntry()
    {
        foreach (ColorMapKind kind in new[] { ColorMapKind.Viridis, ColorMapKind.Magma, ColorMapKind.Turbo })
        {
            byte[] bgra = new byte[4];
            ColorMap.WritePixel(bgra, 0.5, kind);
            (byte r, byte g, byte b) = ColorMap.Entry(ColorMap.IndexOf(0.5), kind);
            Assert.Equal(b, bgra[2]);
            Assert.Equal(g, bgra[1]);
            Assert.Equal(r, bgra[0]);
            Assert.Equal(0xFF, bgra[3]);
        }
    }

    [Fact]
    public void DbRangeSetClampsSpanAndNormalizes()
    {
        DbRange range = new(-140, -20);
        Assert.Equal(120, range.SpanDb, 9);

        // Too-narrow span gets widened to MinSpanDb around the midpoint.
        range.Set(-100, -99);
        Assert.True(range.SpanDb >= DbRange.MinSpanDb);

        // Too-wide span gets narrowed to MaxSpanDb.
        range.Set(-200, 0);
        Assert.True(range.SpanDb <= DbRange.MaxSpanDb);

        range.Set(-120, -20);
        Assert.Equal(0.0, range.Normalize(-120), 9);
        Assert.Equal(1.0, range.Normalize(-20), 9);
        Assert.Equal(-70, range.DbAt(0.5), 9);
        Assert.False(range.AutoScaled); // an explicit toolbar range is not auto-scaled
    }

    [Fact]
    public void DbRangeAutoRangeFromSpectrumUpdatesBoundsAndMarksAutoScaled()
    {
        DbRange range = new(-140, -20);
        float[] spectrum = new float[64];
        for (int i = 0; i < spectrum.Length; i++)
        {
            spectrum[i] = -90f + (i % 10);
        }

        bool changed = range.AutoRange(spectrum, spectrum.Length);
        Assert.True(changed);
        Assert.True(range.AutoScaled);
        Assert.True(range.MinDb < range.MaxDb);
    }

    // ---------- PeakDetector bandwidth classification ----------

    [Fact]
    public void PeakDetectorClassifiesNarrowAndWidePeaks()
    {
        const int Bins = 256;
        const double BinWidthHz = 1_000;

        // Very narrow CW-like peak: single bin dominant.
        float[] narrow = Enumerable.Repeat(-100f, Bins).ToArray();
        narrow[128] = -10f;
        narrow[127] = -12f;
        narrow[129] = -12f;

        SpectrumFrame narrowFrame = Frame(narrow, BinWidthHz);
        DetectedPeak narrowPeak = new PeakDetector(-40).Detect(narrowFrame, Floor(narrow, -90)).Single();
        Assert.Equal(PeakBandwidthClass.Narrow, narrowPeak.BandwidthClass);

        // Wide broadcast-like peak spanning ~30 bins.
        float[] wide = Enumerable.Repeat(-100f, Bins).ToArray();
        for (int i = 40; i <= 70; i++)
        {
            wide[i] = -20f - Math.Abs(i - 55);
        }

        SpectrumFrame wideFrame = Frame(wide, BinWidthHz);
        DetectedPeak widePeak = new PeakDetector(-40).Detect(wideFrame, Floor(wide, -90)).Single();
        Assert.Equal(PeakBandwidthClass.Wide, widePeak.BandwidthClass);
    }

    private static SpectrumFrame Frame(float[] powerDb, double binWidthHz) => new()
    {
        StartHz = 0,
        StopHz = (long)(powerDb.Length * binWidthHz),
        BinWidthHz = binWidthHz,
        PowerDb = powerDb,
        FilledBins = powerDb.Length,
        SweepCompleted = true,
        UtcTimestamp = DateTimeOffset.UtcNow,
    };

    private static double[] Floor(float[] powerDb, double db) =>
        Enumerable.Repeat(db, powerDb.Length).ToArray();

    // ---------- CaptureRecorder metadata sidecar ----------

    [Fact]
    public void CaptureRecorderWritesReadableMetaSidecar()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rfscout-deep-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] samples = new byte[2048];
            IqCapture capture = new(
                106_500_000,
                10_000_000,
                24,
                34,
                false,
                false,
                DateTimeOffset.UtcNow,
                samples);

            RecordedCapture saved = new CaptureRecorder(dir).Save(capture, new ClassificationSummary("WFM", 0.9));
            Assert.True(File.Exists(saved.Cs8Path));
            Assert.True(File.Exists(saved.MetaPath));

            SigMfMeta meta = SigMfMeta.Parse(File.ReadAllText(saved.MetaPath));
            Assert.Equal(10_000_000, meta.SampleRate, 9);
            Assert.Equal(106_500_000, meta.Core, 9);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

    // ---------- CaptureRecorder metadata sidecar ----------

    [Fact]
    public void CaptureRecorderWritesReadableMetaSidecar()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rfscout-deep-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] samples = new byte[2048];
            IqCapture capture = new(
                106_500_000,
                10_000_000,
                24,
                34,
                false,
                false,
                DateTimeOffset.UtcNow,
                samples);

            RecordedCapture saved = new CaptureRecorder(dir).Save(capture, new ClassificationSummary("WFM", 0.9));
            Assert.True(File.Exists(saved.Cs8Path));
            Assert.True(File.Exists(saved.MetaPath));

            SigMfMeta meta = SigMfMeta.Parse(File.ReadAllText(saved.MetaPath));
            Assert.Equal(10_000_000, meta.SampleRate, 9);
            Assert.Equal(106_500_000, meta.Core, 9);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
