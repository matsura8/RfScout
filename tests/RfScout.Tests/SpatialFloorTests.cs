using RfScout.Core;
using RfScout.Core.Spectrum;

namespace RfScout.Tests;

/// <summary>
/// Always-on signals: the per-bin temporal floor alone makes a steady carrier its own floor, so
/// the detector found nothing on the FM band. The spatial floor (across neighbouring bins) fixes
/// that while intermittent signals still use the temporal floor.
/// </summary>
public sealed class SpatialFloorTests
{
    private const long StartHz = 88_000_000;
    private const double BinHz = 78_125;

    private static SpectrumFrame Frame(float[] powerDb) => new()
    {
        StartHz = StartHz,
        StopHz = StartHz + (long)(powerDb.Length * BinHz),
        BinWidthHz = BinHz,
        PowerDb = powerDb,
        FilledBins = powerDb.Count(p => p != SpectrumFrame.EmptyBin),
        SweepCompleted = true,
        UtcTimestamp = DateTimeOffset.UnixEpoch,
    };

    /// <summary>A flat -60 dB floor with steady 3-bin stations at the given bins and level.</summary>
    private static float[] Stations(int bins, int[] centres, float peakDb)
    {
        var power = Enumerable.Repeat(-60f, bins).ToArray();
        foreach (int c in centres)
        {
            power[c] = peakDb;
            power[c - 1] = peakDb - 3;
            power[c + 1] = peakDb - 3;
        }

        return power;
    }

    [Fact]
    public void SteadyStationsAreFoundOnlyWithTheSpatialFloor()
    {
        int[] centres = [30, 70, 110, 150, 190, 230];
        SpectrumFrame frame = Frame(Stations(256, centres, -40f));
        var temporal = new NoiseFloorEstimator();
        IReadOnlyList<double> temporalFloor = [];
        for (int sweep = 0; sweep < NoiseFloorEstimator.DefaultWindowSweeps; sweep++)
        {
            temporalFloor = temporal.Update(frame);
        }

        var detector = new PeakDetector();
        Assert.Empty(detector.Detect(frame, temporalFloor));

        double[] spatial = SpatialFloor.Compute(frame, SpatialFloor.HalfWindowBins(BinHz));
        IReadOnlyList<DetectedPeak> peaks = detector.Detect(frame, SpatialFloor.Combine(temporalFloor, spatial));

        Assert.Equal(centres.Length, peaks.Count);
        Assert.All(peaks, p => Assert.InRange(p.SnrDb, 19, 21));
    }

    [Fact]
    public void FlatAndSlopedFloorsGiveNoPeaks()
    {
        var sloped = new float[256];
        for (int i = 0; i < sloped.Length; i++)
        {
            sloped[i] = -70f + (i * 0.1f);
        }

        var detector = new PeakDetector();
        foreach (float[] power in new[] { Enumerable.Repeat(-60f, 256).ToArray(), sloped })
        {
            SpectrumFrame frame = Frame(power);
            double[] spatial = SpatialFloor.Compute(frame, SpatialFloor.HalfWindowBins(BinHz));
            Assert.Empty(detector.Detect(frame, spatial));
        }
    }

    [Fact]
    public void EmptyBinsAreIgnoredAndSparseWindowsAreUnknown()
    {
        float[] power = Enumerable.Repeat(SpectrumFrame.EmptyBin, 40).ToArray();
        power[0] = -60f;
        power[1] = -61f;
        for (int i = 20; i < 40; i++)
        {
            power[i] = -55f;
        }

        double[] floor = SpatialFloor.Compute(Frame(power), 4);

        Assert.True(double.IsNaN(floor[0]));
        Assert.Equal(-55, floor[30]);
    }

    [Fact]
    public void CombineTakesTheLowerKnownFloor()
    {
        double[] combined = SpatialFloor.Combine([-50, double.NaN, -70, double.NaN], [-60, -65, double.NaN, double.NaN]);

        Assert.Equal(-60, combined[0]);
        Assert.Equal(-65, combined[1]);
        Assert.Equal(-70, combined[2]);
        Assert.True(double.IsNaN(combined[3]));
    }

    [Theory]
    [InlineData(78_125, 13)]
    [InlineData(625_000, SpatialFloor.MinHalfWindowBins)]
    [InlineData(1_000, SpatialFloor.MaxHalfWindowBins)]
    public void HalfWindowCoversAboutOneMegahertz(double binHz, int expected) =>
        Assert.Equal(expected, SpatialFloor.HalfWindowBins(binHz));
}
