using RfScout.Core;
using RfScout.Core.Display;
using RfScout.Core.Streaming;
using Xunit;

namespace RfScout.Tests;

/// <summary>
/// M6 coverage: the streaming hand-off classes (ring buffer, frame buffer, paint heartbeat) and
/// the display helpers (plot readout, capture plots, plot trace) had no direct tests. These pin
/// the behaviour the sweep pipeline relies on: block-aligned drops, newest-frame snapshots,
/// stale-paint detection, and the hover/plot math.
/// </summary>
public class CoverageGapTests
{
    private static SpectrumFrame Frame(float[] power, bool completed = false) => new()
    {
        StartHz = 144_000_000,
        StopHz = 144_000_000 + (power.Length * 12_500),
        BinWidthHz = 12_500,
        PowerDb = power,
        FilledBins = power.Length,
        SweepCompleted = completed,
        UtcTimestamp = DateTimeOffset.UnixEpoch,
    };

    // ---- ByteRingBuffer ----

    [Fact]
    public void RingBufferWriteReadRoundTripsAcrossWrap()
    {
        ByteRingBuffer ring = new(16);
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];

        Assert.True(ring.Write(payload));
        Assert.Equal(8, ring.BytesAvailable);

        // Drain half so the next write wraps around the end of the array.
        Span<byte> half = stackalloc byte[4];
        Assert.Equal(4, ring.Read(half));
        Assert.Equal([1, 2, 3, 4], half.ToArray());

        Assert.True(ring.Write(payload)); // 8 used, 12 free: fits, wraps
        byte[] outBuf = new byte[12];
        Assert.Equal(12, ring.Read(outBuf));
        Assert.Equal([5, 6, 7, 8, 1, 2, 3, 4, 5, 6, 7, 8], outBuf);
        Assert.Equal(0, ring.DroppedChunks);
    }

    [Fact]
    public void RingBufferDropsChunksThatDoNotFitAndKeepsQueuedData()
    {
        ByteRingBuffer ring = new(8); // capacity is exactly 8 (rounded up to a power of two)
        Assert.Equal(8, ring.Capacity);
        Assert.True(ring.Write([1, 2, 3]));
        Assert.True(ring.Write([1, 2, 3, 4, 5])); // 5 free, 5 needed -> fits exactly
        Assert.False(ring.Write([7, 7, 7]));      // 3 needed, 3 free, but only 3-3=0 contiguous at the
                                                  // tail: the wrap gap makes it not fit, so it is dropped whole
        Assert.False(ring.Write([9, 9, 9, 9, 9, 9])); // 6 needed, 3 free -> dropped whole
        Assert.Equal(8, ring.BytesAvailable);     // queued data survives the rejected write untouched

        // The buffer is full (head == tail + capacity), so any non-empty write is dropped and
        // the queued data is untouched. The exact-fit boundary is already covered above by the
        // 5-into-5 write on line 56.
        Assert.False(ring.Write([8]));
        Assert.Equal(8, ring.BytesAvailable);

        // Drain, then the counters show exactly what was dropped.
        Assert.Equal(8, ring.Read(new byte[8]));
        Assert.Equal(0, ring.BytesAvailable);
        Assert.Equal(3, ring.DroppedChunks);
        Assert.Equal(10, ring.DroppedBytes); // 3 + 6 + 1
    }

    [Fact]
    public void RingBufferEmptyWriteAndResetBehave()
    {
        ByteRingBuffer ring = new(8);
        Assert.True(ring.Write(ReadOnlySpan<byte>.Empty)); // empty write is a no-op success
        Assert.True(ring.Write([1, 2, 3]));
        ring.Reset();
        Assert.Equal(0, ring.BytesAvailable);
        Assert.Equal(0, ring.Read(stackalloc byte[4]));
        Assert.Equal(8, ring.Capacity);
    }

    // ---- SpectrumFrameBuffer ----

    [Fact]
    public void FrameBufferSnapshotMergesPeakHoldAndTracksCompletion()
    {
        SpectrumFrameBuffer buffer = new();
        Assert.False(buffer.TryGetSnapshot(out _, out _));

        buffer.Offer(Frame([-50, -40, -30]));
        buffer.Offer(Frame([-45, -60, -20], completed: true));

        Assert.True(buffer.TryGetSnapshot(out SpectrumFrame? latest, out SpectrumFrame? hold));
        Assert.NotNull(latest);
        Assert.Equal(-45, latest!.PowerDb[0]);
        Assert.NotNull(hold);
        Assert.Equal(-45, hold!.PowerDb[0]); // max(-50, -45)
        // (the next line is superseded by the corrected assertion below)
        Assert.Equal(-20, hold.PowerDb[2]);
        Assert.Equal(2, buffer.FramesSeen);
        Assert.Equal(1, buffer.CompletedFramesSeen);
        Assert.NotNull(buffer.LastCompleted);
        Assert.True(buffer.Sequence >= 2);
        Assert.True(buffer.HasFrame);

        buffer.Reset();
        Assert.False(buffer.HasFrame);
        Assert.Null(buffer.LastCompleted);
        Assert.False(buffer.TryGetSnapshot(out _, out _));
    }

    [Fact]
    public void FrameBufferResetKeepsPeakHoldWhenAsked()
    {
        SpectrumFrameBuffer buffer = new();
        buffer.Offer(Frame([-10]));
        buffer.Reset(clearPeakHold: false);
        Assert.False(buffer.HasFrame);
        buffer.Offer(Frame([-90]));
        Assert.True(buffer.TryGetSnapshot(out _, out SpectrumFrame? hold));
        Assert.Equal(-10, hold!.PowerDb[0]); // old peak hold survived the reset
    }

    [Fact]
    public void FrameBufferOfferRejectsNull()
    {
        SpectrumFrameBuffer buffer = new();
        Assert.Throws<ArgumentNullException>(() => buffer.Offer(null!));
    }

    // ---- UiFrameHeartbeat ----

    [Fact]
    public void HeartbeatRecordsPaintsAndWorstGap()
    {
        UiFrameHeartbeat beat = new();
        Assert.Equal(0, beat.Record()); // first paint has no previous
        Assert.Equal(1, beat.PaintCount);

        System.Threading.Thread.Sleep(20);
        double gap = beat.Record();
        Assert.True(gap >= 10, $"expected a measurable gap, got {gap} ms");
        Assert.True(beat.WorstGapMilliseconds >= 10);
        Assert.Equal(2, beat.PaintCount);

        beat.Reset();
        Assert.Equal(0, beat.PaintCount);
        Assert.Equal(0, beat.WorstGapMilliseconds);
        Assert.Equal(0, beat.Record());
    }

    // ---- PlotReadout / render models / display floor estimator ----

    [Fact]
    public void PlotReadoutHoverFormatsFrequencyAndPower()
    {
        SpectrumFrame frame = Frame([-50f, -42.5f, -30f]);
        SpectrumViewport view = new(frame.StartHz, frame.StopHz);
        string text = PlotReadout.Hover(frame, view, 0.5, 0.5);
        Assert.Contains("144", text, StringComparison.Ordinal);
        Assert.Contains("-42", text, StringComparison.Ordinal);
        Assert.Equal("No sweep yet", PlotReadout.Hover(null, view, 0.5, 0.5));
    }

    [Fact]
    public void PlotReadoutSpanSummaryNamesTheStrongestBin()
    {
        SpectrumFrame frame = Frame([-90, -30, -80]);
        string text = PlotReadout.SpanSummary(frame);
        Assert.Contains("3 of 3", text, StringComparison.Ordinal);
        Assert.Contains("-30", text, StringComparison.Ordinal);
        Assert.Equal("Waiting for the first sweep.", PlotReadout.SpanSummary(null));
    }

    [Fact]
    public void SpectrumPlotModelBuildMapsFrameToTraces()
    {
        SpectrumFrame frame = Frame([-50, -40, -30, -20]);
        SpectrumViewport view = new(frame.StartHz, frame.StopHz);
        SpectrumPlotModel model = SpectrumPlotModel.Build(
            frame, frame, [-60, -60, -60, -60], view, new DbRange(-140, -20));
        Assert.True(model.HasData);
        Assert.Equal(4, model.Live.Count);
        Assert.Equal(4, model.PeakHold.Count);
        Assert.Equal(4, model.Floor.Count);
        Assert.Equal(4, model.VisibleBins);
        Assert.False(SpectrumPlotModel.Empty.HasData);
    }

    [Fact]
    public void WaterfallModelWritesRowsAndSnapshots()
    {
        WaterfallModel model = new();
        model.Resize(4);
        int row = model.WriteRow([-50, -40, -30, -20], new DbRange(-140, -20));
        Assert.Equal(0, row);
        byte[] dest = new byte[model.BytesPerRow];
        Assert.True(model.TryCopyRow(row, dest));
        Assert.True(model.Writes >= 1);
        Assert.NotEqual(Array.Empty<byte>(), model.Snapshot());
        model.Clear();
        Assert.False(model.TryCopyRow(999, dest));
    }

    [Fact]
    public void DisplayNoiseFloorEstimatorFollowsFrames()
    {
        NoiseFloorEstimator estimator = new();
        double[] floor = [];
        for (int i = 0; i < 10; i++)
        {
            floor = estimator.Update(Frame([-60, -55, -58, -61], completed: true));
        }

        Assert.Equal(4, floor.Length);
        Assert.True(floor.All(v => v is >= -80 and <= -40));
        estimator.Reset();
        Assert.Equal(0, estimator.Sweeps);
    }
}
