// Deep-coverage tests added by the QA milestone: exercise the remaining Core
// surfaces (IQ phase unwrap edge cases, waterfall/colour-map paths, peak
// bandwidth classification, capture-recorder metadata) that earlier suites
// only touched indirectly.
namespace RfScout.Tests;

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using RfScout.Core.Analysis;
using RfScout.Core.Display;
using RfScout.Core.Dsp;
using RfScout.Core.Recording;
using RfScout.Core.Spectrum;
using Xunit;

public class DeepCoverageTests
{
    // ---------- IqMath.UnwrapPhase edge cases ----------

    [Fact]
    public void UnwrapPhaseTwoElementJumpWrapsOnce()
    {
        // +2.9 -> -2.9 rad is a +3.4 jump; unwrapping should add 2*pi to the second sample.
        double[] phase = [2.9, -2.9];
        IqMath.UnwrapPhase(phase);
        phase[0].Should().BeApproximately(2.9, 1e-9);
        phase[1].Should().BeApproximately(-2.9 + (2 * Math.PI), 1e-9);
    }

    [Fact]
    public void UnwrapPhaseExactlyPiBoundaryDoesNotWrap()
    {
        // A jump of exactly +pi is below the strict threshold, so no correction.
        double[] phase = [0.0, Math.PI];
        IqMath.UnwrapPhase(phase);
        phase[1].Should().BeApproximately(Math.PI, 1e-9);
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
        phase[N - 1].Should().BeApproximately((N - 1) * 0.1, 1e-6);
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

        Action fill = () => WaterfallModel.FillRow(powerDb, Columns, range, ColorMap.Lookup(ColorMapKind.Viridis), bgra);
        fill.Should().NotThrow();

        // Every pixel alpha byte must be 0xFF regardless of input sanity.
        for (int i = 3; i < bgra.Length; i += 4)
        {
            bgra[i].Should().Be(0xFF);
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
        model.Writes.Should().Be(1);
        model.NewestRow.Should().Be(newest);

        byte[] copy = new byte[model.BytesPerRow];
        model.TryCopyRow(newest, copy).Should().BeTrue();
        model.TryCopyRow(newest, copy[..4]).Should().BeFalse(); // too small
        model.TryCopyRow(99, copy).Should().BeFalse(); // out of range

        byte[] snapshot = model.Snapshot();
        snapshot.Length.Should().Be(model.Columns * model.Rows * 4);
    }

    [Fact]
    public void WaterfallClearResetsWritesAndSnapshotIsBlank()
    {
        WaterfallModel model = new();
        model.Resize(8, 3);
        model.WriteRow(Enumerable.Repeat(-60f, 8).ToArray(), new DbRange(-120, -20));
        model.Clear();

        model.Writes.Should().Be(0);
        model.Snapshot().Should().AllSatisfy(b => b.Should().Be(0));
    }

    [Fact]
    public void ColorMapLookupIsMonotonicLuminanceForViridis()
    {
        byte[] viridis = ColorMap.Lookup(ColorMapKind.Viridis);
        viridis.Length.Should().Be(ColorMap.Entries * 4);

        // First and last entries are the endpoints of the ramp.
        ColorMap.IndexOf(0.0).Should().Be(0);
        ColorMap.IndexOf(1.0).Should().Be(ColorMap.Entries - 1);
        ColorMap.IndexOf(-5.0).Should().Be(0);   // clamped low
        ColorMap.IndexOf(5.0).Should().Be(ColorMap.Entries - 1); // clamped high

        (byte r0, byte g0, byte b0) = ColorMap.Entry(0, ColorMapKind.Viridis);
        (byte r1, byte g1, byte b1) = ColorMap.Entry(ColorMap.Entries - 1, ColorMapKind.Viridis);
        (r0 + g0 + b0).Should().BeLessThan(r1 + g1 + b1);
    }

    [Fact]
    public void ColorMapWritePixelMatchesEntry()
    {
        foreach (ColorMapKind kind in new[] { ColorMapKind.Viridis, ColorMapKind.Magma, ColorMapKind.Turbo })
        {
            byte[] bgra = new byte[4];
            ColorMap.WritePixel(bgra, 0.5, kind);
            (byte r, byte g, byte b) = ColorMap.Entry(ColorMap.IndexOf(0.5), kind);
            bgra[2].Should().Be(r);
            bgra[1].Should().Be(g);
            bgra[0].Should().Be(b);
            bgra[3].Should().Be(0xFF);
        }
    }

    [Fact]
    public void DbRangeAutoRangeClampsSpanAndNormalizes()
    {
        DbRange range = new(-140, -20);
        range.SpanDb.Should().Be(120);

        // Too-narrow span gets widened to MinSpanDb around the midpoint.
        range.Set(-100, -99);
        range.SpanDb.Should().BeGreaterThanOrEqualTo(DbRange.MinSpanDb);

        // Too-wide span gets narrowed to MaxSpanDb.
        range.Set(-200, 0);
        range.SpanDb.Should().BeLessThanOrEqualTo(DbRange.MaxSpanDb);

        range.Set(-120, -20);
        range.Normalize(-120).Should().BeApproximately(0.0, 1e-9);
        range.Normalize(-20).Should().BeApproximately(1.0, 1e-9);
        range.DbAt(0.5).Should().BeApproximately(-70, 1e-9);
    }

    [Fact]
    public void DbRangeAutoRangeFromSpectrumUpdatesBounds()
    {
        DbRange range = new(-140, -20);
        float[] spectrum = new float[64];
        for (int i = 0; i < spectrum.Length; i++)
        {
            spectrum[i] = -90f + (i % 10);
        }

        bool changed = range.AutoRange(spectrum, spectrum.Length);
        changed.Should().BeTrue();
        range.AutoScaled.Should().BeTrue();
        range.MinDb.Should().BeLessThan(range.MaxDb);
    }

    // ---------- PeakTracker bandwidth classification ----------

    [Fact]
    public void PeakTrackerClassifiesNarrowAndWideTones()
    {
        const int Bins = 256;
        const double SampleRate = 256_000;

        // Very narrow CW-like peak: single bin dominant.
        float[] narrow = Enumerable.Repeat(-100f, Bins).ToArray();
        narrow[128] = -10f;
        narrow[127] = -12f;
        narrow[129] = -12f;

        PeakTracker tracker = new();
        PeakInfo narrowPeak = tracker.FindPeaks(narrow, SampleRate, -60).Single();
        narrowPeak.BandwidthClass.Should().Be(PeakBandwidthClass.Narrow);

        // Wide broadcast-like peak spanning ~30 bins.
        float[] wide = Enumerable.Repeat(-100f, Bins).ToArray();
        for (int i = 40; i <= 70; i++)
        {
            wide[i] = -20f - Math.Abs(i - 55);
        }

        PeakInfo widePeak = tracker.FindPeaks(wide, SampleRate, -60).Single();
        widePeak.BandwidthClass.Should().Be(PeakBandwidthClass.Wide);
    }

    // ---------- ClassificationPipeline over synthetic frames ----------

    [Fact]
    public void ClassificationPipelineLabelsSyntheticCwPeak()
    {
        SyntheticRadioSource source = new() { ToneMode = SyntheticToneMode.Cw };
        source.StartCapture();

        ClassificationPipeline pipeline = new();
        ClassificationResult result = pipeline.Classify(source.NextSpectrum()!, source);

        result.Class.Should().NotBe(ModulationClass.Unknown);
        result.Peaks.Should().NotBeEmpty();
    }

    // ---------- CaptureRecorder metadata sidecar ----------

    [Fact]
    public void CaptureRecorderWritesReadableMetaSidecar()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rfscout-deep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            SyntheticRadioSource source = new() { ToneMode = SyntheticToneMode.Cw };
            source.StartCapture();

            string iqPath = Path.Combine(dir, "capture.iq8");
            CaptureRecorder recorder = new();
            recorder.BeginCapture(iqPath, source);
            for (int i = 0; i < 5; i++)
            {
                recorder.Write(source.NextIq()!);
            }

            recorder.EndCapture();

            File.Exists(iqPath).Should().BeTrue();
            string metaPath = iqPath + ".meta.json";
            File.Exists(metaPath).Should().BeTrue();

            SigMfMeta meta = SigMfMeta.Load(metaPath);
            meta.SampleRate.Should().BeGreaterThan(0);
            meta.CenterFrequencyHz.Should().BeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
