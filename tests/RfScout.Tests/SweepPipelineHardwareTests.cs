using RfScout.Core;
using RfScout.Core.Spectrum;
using RfScout.Services.HackRf;

namespace RfScout.Tests;

/// <summary>
/// The M1 hardware checks against a real HackRF One, written to the brief's rules: read
/// <c>HARDWARE_USB_DEVICES</c> for the serial rather than hard-coding one, and treat the RF
/// environment as unknowable - a test that needs an FM station to be broadcasting would fail
/// at 3 a.m. on a quiet band, so anything about what is on air is logged, not asserted.
///
/// What *is* asserted is what the code controls: the wrapper opens and closes cleanly, reports
/// the board honestly, frames a sweep into a populated spectrum grid with plausible dBFS, and
/// delivers the sample count a narrowband capture was asked for.
/// </summary>
[Trait("Category", "Hardware")]
[Collection(HackRfHardwareGroup.Name)]
public sealed class SweepPipelineHardwareTests
{
    private static string? Serial => ResolveSerialFromEnvironment();

    [Fact]
    public void OpenReadsBoardFirmwareAndSerialAndClosesCleanly()
    {
        RadioDeviceInfo info;

        using (HackRfDevice radio = OpenRadio())
        {
            info = radio.DeviceInfo;

            Assert.False(string.IsNullOrWhiteSpace(info.SerialNumber));
            Assert.False(string.IsNullOrWhiteSpace(info.FirmwareVersion));
            Assert.DoesNotContain("unknown", info.FirmwareVersion);
            Assert.Contains("HackRF", info.BoardName, StringComparison.OrdinalIgnoreCase);

            // Sweep mode needs firmware API 1.02; the device layer refuses older firmware at open,
            // so reaching here at all already proves the API is new enough. Assert the number too,
            // because it is what the About box shows.
            Assert.True(info.UsbApiVersion >= RadioDeviceInfo.MinSweepApiVersion, $"API {info.UsbApiVersionText}");
            Assert.StartsWith("1.", info.UsbApiVersionText, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(info.LibraryVersion));
            Assert.DoesNotContain("unknown", info.LibraryVersion);

            if (Serial is { Length: > 0 } wanted)
            {
                Assert.Equal(wanted, info.SerialNumber);
            }
            else
            {
                Log($"no serial in HARDWARE_USB_DEVICES; device reported {info.ShortSerial}");
            }
        }

        // The radio is closed at this point, and that is the whole point of the second open: a
        // close that left libhackrf or libusb still holding the device would make every later open
        // fail with HACKRF_ERROR_NOT_FOUND or _BUSY, which is what happens when the user replugs
        // the radio and presses Start again. Two using declarations in one scope would open both at
        // once and fail here for the wrong reason.
        using (HackRfDevice again = OpenRadio())
        {
            Assert.Equal(info.SerialNumber, again.DeviceInfo.SerialNumber);
        }
    }

    [Fact]
    public void ShortFullSpanSweepProducesAPopulatedSpectrumGrid()
    {
        using HackRfDevice radio = OpenRadio();

        // A wide span at the coarsest bins exercises the whole stitching path: many radio ranges,
        // interleaved sub-steps, and the scatter into a grid far narrower than the block FFT.
        var plan = new SweepPlan(
            [new FrequencyRange(1_000_000, 6_000_000_000)],
            BinWidthHz: 625_000,
            LnaGainDb: 32,
            VgaGainDb: 44);

        var frames = new List<SpectrumFrame>();
        radio.SpectrumAvailable += (_, e) => { lock (frames) { frames.Add(e.Frame); } };

        DateTime startedAt = DateTime.UtcNow;
        radio.StartSweep(plan);
        try
        {
            // A pass over 6 GHz takes about a third of a second at 20 Msps, longer than the 100 ms
            // refresh, so early frames are legitimately partial. Wait for the one that closes a
            // pass: that is the frame whose completeness the display and the sweep counter rely on.
            SpectrumFrame frame = WaitForCompletedFrame(frames, TimeSpan.FromSeconds(20));
            Assert.True(frame.SweepCompleted, "the frame waited for did not close a pass");
            double firstPassSeconds = (DateTime.UtcNow - startedAt).TotalSeconds;

            // A completed pass means every bin was measured. A hole anywhere would mean blocks
            // framed wrongly, the quarter-keeping dropped chunks of the span, or the header-to-
            // frequency mapping put bins outside the grid.
            Assert.Equal(frame.BinCount, frame.FilledBins);
            for (int i = 0; i < frame.BinCount; i++)
            {
                Assert.False(frame.IsEmpty(i), $"bin {i} of {frame.BinCount} is empty");
            }

            // Plausible power: above the quantisation floor, below full scale. With LNA 32 and
            // VGA 44 the ADC rarely sits quieter than -80 dBFS median or louder than -10 dBFS.
            float[] filled = frame.PowerDb[..frame.FilledBins];
            float[] sorted = (float[])filled.Clone();
            Array.Sort(sorted);
            float median = sorted[sorted.Length / 2];
            float max = sorted[^1];
            Assert.True(median > -90 && median < 0, $"median power {median:F1} dBFS looks wrong");
            Assert.True(max <= 5, $"peak power {max:F1} dBFS above full scale");

            // The frequency mapping has to survive the whole 1-6000 MHz trip.
            Assert.Equal(1_000_000L, frame.StartHz);
            Assert.Equal(6_000_000_000L, frame.StopHz);
            Assert.Equal(625_000d, frame.BinWidthHz);

            // M1's "done when" is a full 1-6000 MHz refresh at least once a second, so measure the
            // steady-state rate rather than just "a second pass eventually arrives": count completed
            // passes over a fixed five-second window after the first one. The first pass is excluded
            // because it includes tuning and buffer warm-up, which is not what the user watches.
            //
            // This also catches the grid freezing on the first pass: a completed frame is only
            // produced when the header wraps back to the start of the range, so a stream that stops
            // reframing, or an assembler that never clears its measured mask, scores zero here.
            DateTime windowStart = DateTime.UtcNow;
            int completed;
            do
            {
                Thread.Sleep(100);
                lock (frames)
                {
                    completed = frames.Count(f => f.SweepCompleted);
                }
            }
            while (DateTime.UtcNow - windowStart < TimeSpan.FromSeconds(5));

            double windowSeconds = (DateTime.UtcNow - windowStart).TotalSeconds;
            int steadyPasses = completed - 1;
            double passesPerSecond = steadyPasses / windowSeconds;

            Assert.True(
                steadyPasses >= 5,
                $"{steadyPasses} completed sweep(s) in {windowSeconds:F1} s ({passesPerSecond:F2}/s); M1 wants at least one full refresh per second");
            Assert.True(
                firstPassSeconds < 1.0,
                $"the first full pass took {firstPassSeconds:F2} s, which is slower than the refresh the milestone asks for");

            Log($"{completed} completed sweeps in {windowSeconds:F1} s ({passesPerSecond:F2}/s), median {median:F1} dBFS, peak {max:F1} dBFS");
        }
        finally
        {
            radio.StopStream();
        }
    }

    [Fact]
    public void NarrowbandCaptureDeliversTheRequestedSampleCount()
    {
        using HackRfDevice radio = OpenRadio();

        // FM broadcast band at 4 Msps: the radio's own filter chain handles this rate cleanly,
        // and 200 ms is long enough to prove the window fills and short enough to keep the test
        // quick.
        const long centerHz = 98_000_000;
        const int rateHz = 4_000_000;
        var duration = TimeSpan.FromMilliseconds(200);

        radio.StartCapture(centerHz, rateHz, duration);
        IqCapture? capture = radio.WaitForCapture(TimeSpan.FromSeconds(10));

        Assert.NotNull(capture);
        Assert.Equal(centerHz, capture!.CenterHz);
        Assert.Equal(rateHz, capture.SampleRateHz);

        int expected = rateHz / 5; // 200 ms
        Assert.True(
            capture.SampleCount >= expected && capture.SampleCount <= (expected + 65_536),
            $"captured {capture.SampleCount} samples, wanted {expected}");

        // The IQ must be real bytes, not a silent buffer: a HackRF with no antenna still shows
        // thermal noise, so the I/Q excursions cannot all sit at the s8 zero point (0x80 in the
        // unsigned storage the callback copies).
        ReadOnlySpan<byte> iq = capture.Samples.Span;
        int quiet = 0;
        int maxDeviation = 0;
        foreach (byte b in iq)
        {
            if (b == 0x80)
            {
                quiet++;
            }

            maxDeviation = Math.Max(maxDeviation, Math.Abs(b - 0x80));
        }

        Assert.True(quiet < iq.Length, "every sample is exactly zero - the stream is not delivering samples");
        Assert.True(maxDeviation > 1, "capture has no excursion at all");

        Log($"capture {capture.SampleCount} samples in {capture.Duration.TotalMilliseconds:F0} ms, center {centerHz / 1e6:F3} MHz");
    }

    [Fact]
    public void GainChangesAreAcceptedAndRejectedValuesThrow()
    {
        using HackRfDevice radio = OpenRadio();

        radio.SetGains(0, 0);
        radio.SetGains(40, 62);
        radio.SetGains(24, 34);

        Assert.Throws<ArgumentOutOfRangeException>(() => radio.SetGains(20, 34)); // not a multiple of 8
        Assert.Throws<ArgumentOutOfRangeException>(() => radio.SetGains(24, 33)); // not a multiple of 2
        Assert.Throws<ArgumentOutOfRangeException>(() => radio.SetGains(48, 34)); // above 40

        // Amp and antenna power default off and round-trip through the confirmation-free setter.
        (int lna, int vga, bool ampOn, bool antPwr) = radio.Gains;
        Assert.False(ampOn);
        Assert.False(antPwr);
        Assert.Equal(24, lna);
        Assert.Equal(34, vga);

        radio.SetRfAmplifier(true);
        radio.SetAntennaPower(true);
        (_, _, ampOn, antPwr) = radio.Gains;
        Assert.True(ampOn);
        Assert.True(antPwr);

        // Always leave the antenna power off for the next test and for the operator.
        radio.SetAntennaPower(false);
        radio.SetRfAmplifier(false);
    }

    [Fact]
    public void SweepAndCaptureRefuseToShareTheRadio()
    {
        using HackRfDevice radio = OpenRadio();
        var plan = new SweepPlan(
            [new FrequencyRange(88_000_000, 108_000_000)],
            BinWidthHz: 78_125);

        DateTime startedAt = DateTime.UtcNow;
        radio.StartSweep(plan);
        try
        {
            // The device is one resource: the brief requires a clean stop before switching, so
            // a second stream while one runs must be refused, not queued or silently replaced.
            Assert.Throws<InvalidOperationException>(() => radio.StartCapture(98_000_000, 4_000_000, TimeSpan.FromMilliseconds(50)));
            Assert.Throws<InvalidOperationException>(() => radio.StartSweep(plan));
            Assert.True(radio.IsRunning);
        }
        finally
        {
            radio.StopStream();
        }

        Assert.False(radio.IsRunning);

        // And after stopping cleanly the other mode must start normally.
        radio.StartCapture(98_000_000, 4_000_000, TimeSpan.FromMilliseconds(50));
        Assert.NotNull(radio.WaitForCapture(TimeSpan.FromSeconds(10)));
    }

    internal static HackRfDevice OpenRadio()
    {
        if (Environment.GetEnvironmentVariable("HARDWARE_TESTS") != "1")
        {
            Assert.Skip("set HARDWARE_TESTS=1 to run hardware tests");
        }
        return new HackRfDevice(Serial);
    }

    /// <summary>
    /// The serial from HARDWARE_USB_DEVICES if it carries one (the list holds "name=id" pairs,
    /// and some configurations append the serial); otherwise null, which means open the first
    /// HackRF found. No test hard-codes a serial.
    /// </summary>
    private static string? ResolveSerialFromEnvironment()
    {
        string? raw = Environment.GetEnvironmentVariable("HARDWARE_USB_DEVICES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        foreach (string entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Accepted forms: "hackrf=1d50:6089", "hackrf=1d50:6089:0000000000000000088869dc382c711b".
            string[] parts = entry.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 && parts[1] == "1d50")
            {
                string serial = string.Join(':', parts[2..]).Trim();
                if (serial.Length > 0)
                {
                    return serial;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The first frame that closed a complete pass, or a timeout naming how many partial frames
    /// did arrive - "0 frames" and "40 partial frames, none complete" are different faults and the
    /// message has to say which one happened.
    /// </summary>
    private static SpectrumFrame WaitForCompletedFrame(List<SpectrumFrame> frames, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (frames)
            {
                SpectrumFrame? done = frames.FirstOrDefault(f => f.SweepCompleted);
                if (done is not null)
                {
                    return done;
                }
            }

            Thread.Sleep(50);
        }

        SpectrumFrame? fullest = null;
        int seen;
        lock (frames)
        {
            seen = frames.Count;
            fullest = seen == 0 ? null : frames.MaxBy(f => f.FilledBins);
        }

        // "It never finished" is only useful if it says what was missing: a sweep that stops
        // covering new bins is a different fault from one that covers the span slowly.
        string holes = fullest is null ? "no frames at all" : HoleReport(fullest);
        throw new TimeoutException(
            $"no completed sweep within {timeout.TotalSeconds:F0} s " +
            $"({seen} frame(s), best fill {fullest?.FilledBins ?? 0}/{fullest?.BinCount ?? 0} bins; {holes})");
    }

    /// <summary>
    /// Where a partial frame is missing bins, as MHz runs. The sweep list is programmed in whole
    /// MHz and each block covers header+2.5..+12.5 MHz, so the shape of the holes says whether the
    /// gap is a range edge, a step that jumped, or a stretch of blocks dropped in the ring.
    /// </summary>
    private static string HoleReport(SpectrumFrame frame)
    {
        var runs = new List<string>();
        int start = -1;
        for (int i = 0; i <= frame.BinCount; i++)
        {
            bool empty = i < frame.BinCount && frame.IsEmpty(i);
            if (empty && start < 0)
            {
                start = i;
            }
            else if (!empty && start >= 0)
            {
                double from = frame.StartHz + (start * frame.BinWidthHz) / 1e6;
                double to = frame.StartHz + (i * frame.BinWidthHz) / 1e6;
                runs.Add($"{from:F1}-{to:F1} MHz ({i - start} bins)");
                start = -1;
            }
        }

        if (runs.Count == 0)
        {
            return "no holes";
        }

        string shown = string.Join(", ", runs.Take(12));
        string more = runs.Count > 12 ? $", ... +{runs.Count - 12} more runs" : string.Empty;
        return $"holes: {shown}{more}";
    }

    private static void Log(string message) => Console.WriteLine($"[hardware] {message}");
}
