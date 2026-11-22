// M4 "prove it": live hardware tests for FM RDS names and ADS-B. The air is unknowable, so
// these tests log what they saw and never fail on an empty sky (the brief's live-RF rule, as
// in the M1/M2 hardware tests). What *is* asserted is what the code controls: the capture
// completes, the decoder runs over real IQ without throwing, and every identification it
// returns is internally consistent (non-empty protocol/id, confidence in 0..1).
//
// Run with HARDWARE_TESTS=1 and HARDWARE_USB_DEVICES=hackrf=1d50:6089, then
// dotnet test --filter-trait Category=Hardware. One radio: both tests join the HackRF
// collection so they never open it at the same time.
using System.Globalization;
using System.Text;
using RfScout.Core;
using RfScout.Core.Decoders;
using RfScout.Core.Spectrum;
using RfScout.Services.HackRf;
using Xunit;

namespace RfScout.Tests;

[Trait("Category", "Hardware")]
[Collection(HackRfHardwareGroup.Name)]
public sealed class DecoderLiveHardwareTests
{
    [Fact]
    public void LiveFmRdsCaptureRunsTheDecoderAndLogsStationNames()
    {
        using HackRfDevice radio = SweepPipelineHardwareTests.OpenRadio();

        // A strong local FM station's 57 kHz RDS subcarrier sits inside a 400 kHz capture
        // centred on the station. Pick the loudest station in the band first, then capture it.
        long centerHz = FindStrongestFmStation(radio);
        if (centerHz == 0)
        {
            Log("no FM station found in the sweep; running the decoder on a mid-band capture anyway");
            centerHz = 98_500_000;
        }

        IqCapture? capture = Capture(radio, centerHz, 400_000, TimeSpan.FromSeconds(3));
        Assert.NotNull(capture);

        DecoderContext ctx = DecoderContext.ForPeak(
            capture!.Samples, capture.CenterHz, capture.SampleRateHz, BandPlan.LoadShipped());
        IDecoder decoder = new RdsDecoder();
        IReadOnlyList<DeviceIdentification> ids = decoder.Decode(ctx); // must not throw on live air

        var log = new StringBuilder($"live RDS @{capture.CenterHz / 1e6:F1} MHz: {ids.Count} identification(s)");
        foreach (DeviceIdentification id in ids)
        {
            AssertSane(id);
            log.Append(CultureInfo.InvariantCulture,
                $"\n    rds {id.Id} pi={id.Extra.GetValueOrDefault("pi_hex", "?")} conf={id.Confidence:F2}");
        }

        if (ids.Count == 0)
        {
            log.Append("\n    no RDS names decoded: logged, not failed (empty sky / weak signal / wrong station)");
        }

        Log(log.ToString());
    }

    [Fact]
    public void LiveAdsbCaptureRunsTheDecoderAndLogsAircraft()
    {
        using HackRfDevice radio = SweepPipelineHardwareTests.OpenRadio();

        // 1090 MHz ADS-B: the brief's Mode S rate is 2 Msps, the HackRF's minimum capture
        // rate, so capture at 2 Msps centred on 1090 MHz.
        IqCapture? capture = Capture(radio, 1_090_000_000, 2_000_000, TimeSpan.FromSeconds(3));
        Assert.NotNull(capture);

        DecoderContext ctx = DecoderContext.ForPeak(
            capture!.Samples, capture.CenterHz, capture.SampleRateHz, BandPlan.LoadShipped());
        IDecoder decoder = new ModeSDecoder();
        IReadOnlyList<DeviceIdentification> ids = decoder.Decode(ctx); // must not throw on live air

        var log = new StringBuilder($"live ADS-B @{capture.CenterHz / 1e6:F3} MHz: {ids.Count} identification(s)");
        foreach (DeviceIdentification id in ids)
        {
            AssertSane(id);
            log.Append(CultureInfo.InvariantCulture,
                $"\n    modes {id.Id} icao={id.Extra.GetValueOrDefault("icao_hex", "?")} conf={id.Confidence:F2}");
        }

        if (ids.Count == 0)
        {
            log.Append("\n    no ADS-B frames decoded: logged, not failed (empty sky, no overflight, or antenna)");
        }

        Log(log.ToString());
    }

    /// <summary>Sweep 88-108 MHz and return the centre of the strongest 200 kHz-wide bin, or 0.</summary>
    private static long FindStrongestFmStation(HackRfDevice radio)
    {
        var frames = new List<SpectrumFrame>();
        radio.SpectrumAvailable += (_, e) =>
        {
            if (e.Frame.SweepCompleted)
            {
                lock (frames)
                {
                    frames.Add(e.Frame);
                }
            }
        };

        radio.StartSweep(new SweepPlan(
            [new FrequencyRange(88_000_000, 108_000_000)],
            BinWidthHz: 200_000,
            LnaGainDb: 24,
            VgaGainDb: 32));
        try
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                lock (frames)
                {
                    if (frames.Count >= 3)
                    {
                        break;
                    }
                }

                Thread.Sleep(100);
            }
        }
        finally
        {
            radio.StopStream();
        }

        lock (frames)
        {
            if (frames.Count == 0)
            {
                return 0;
            }

            SpectrumFrame frame = frames[^1];
            int best = -1;
            float bestDb = float.MinValue;
            for (int bin = 0; bin < frame.BinCount; bin++)
            {
                float db = frame.PowerDb[bin];
                if (db != SpectrumFrame.EmptyBin && db > bestDb)
                {
                    bestDb = db;
                    best = bin;
                }
            }

            return best < 0 ? 0 : (long)frame.CenterFrequencyOfBin(best);
        }
    }

    /// <summary>One narrowband capture, gains modest, bias-tee and amp off (hard rule: off by default).</summary>
    private static IqCapture? Capture(HackRfDevice radio, long centerHz, int rateHz, TimeSpan duration)
    {
        radio.SetGains(24, 32);
        radio.SetRfAmplifier(false);
        radio.SetAntennaPower(false);
        radio.StartCapture(centerHz, rateHz, duration);
        return radio.WaitForCapture(duration + TimeSpan.FromSeconds(5));
    }

    /// <summary>What the code controls: an identification is internally consistent.</summary>
    private static void AssertSane(DeviceIdentification id)
    {
        Assert.False(string.IsNullOrWhiteSpace(id.Protocol));
        Assert.False(string.IsNullOrWhiteSpace(id.Id));
        Assert.True(id.Confidence is >= 0 and <= 1, $"confidence {id.Confidence} outside 0..1");
    }

    /// <summary>Console plus the TRX StdOut, so the run log shows what the air looked like.</summary>
    private static void Log(string message)
    {
        Console.WriteLine($"[hardware] {message}");
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "decoder-live-hardware.log"), message + Environment.NewLine);
    }
}
