using RfScout.Core;
using RfScout.Core.Analysis;

namespace RfScout.Core.Analysis;

/// <summary>
/// The analyze/record state machine, kept out of the view model so the sequencing rules are
/// unit-testable without WPF or a radio:
///
///  * sweep and analyze never run together: <see cref="BeginAnalyze"/> stops the sweep first,
///    and the sweep only resumes after the capture, features, classification and (optional)
///    recording are done;
///  * one analyze at a time; a second request while busy is refused, not queued;
///  * the tune plan is chosen from the peak's measured bandwidth: a sample rate of 2..20 Msps
///    that puts the signal in the inner half of the window, and a capture length of 0.5..2 s;
///  * the existing capture limits (MaxCaptureBytes / MaxCaptureSeconds) still bound the window.
/// </summary>
public sealed class AnalyzeController
{
    /// <summary>Smallest analyze sample rate the controller will pick.</summary>
    public const int MinSampleRateHz = 2_000_000;

    /// <summary>Largest analyze sample rate (the radio's sweep rate).</summary>
    public const int MaxSampleRateHz = 20_000_000;

    /// <summary>Shortest and longest analyze windows.</summary>
    public const double MinCaptureSeconds = 0.5;
    public const double MaxCaptureSeconds = 2.0;

    /// <summary>Guard band: the signal must fit inside this fraction of the sample rate.</summary>
    public const double OccupancyFraction = 0.5;

    /// <summary>
    /// A signal at least this wide is broadcast-band FM sized (~200 kHz channel): it needs a
    /// capture rate high enough that the HackRF baseband filter and the s8 quantiser do not
    /// distort its ±75 kHz deviation. 10 Msps puts the filter at 7.5 MHz and gives the FM tone
    /// ~133 quantiser steps; 2 Msps clips the swing and its coarse steps fake extra deviation.
    /// </summary>
    public const double WfmSignalBandwidthHz = 100_000;

    /// <summary>The capture rate raised up to for a WFM-sized signal (see <see cref="WfmSignalBandwidthHz"/>).</summary>
    public const int WfmSampleRateHz = 10_000_000;

    private readonly Lock _gate = new();
    private bool _busy;

    /// <summary>True while an analyze owns the radio.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _busy;
            }
        }
    }

    /// <summary>
    /// Choose the analyze plan for a peak of <paramref name="signalBandwidthHz"/> (the -3 dB
    /// width) at <paramref name="centerHz"/>. The rate is the smallest power-of-two-ish value
    /// in 2..20 Msps whose half-band holds twice the signal width, so the signal sits in the
    /// inner half of the window with its skirts visible; the length is 1 s clamped to 0.5..2 s
    /// and to the caller's capture limits.
    /// </summary>
    public static AnalyzePlan PlanFor(
        long centerHz,
        double signalBandwidthHz,
        long maxCaptureBytes,
        double maxCaptureSeconds)
    {
        if (signalBandwidthHz <= 0)
        {
            signalBandwidthHz = MinSampleRateHz * OccupancyFraction;
        }

        double needed = signalBandwidthHz / OccupancyFraction;
        int rate = MinSampleRateHz;
        while (rate < MaxSampleRateHz && rate < needed)
        {
            rate *= 2;
        }

        rate = Math.Min(rate, MaxSampleRateHz);

        // QA defect 4: a strong FM station measured 228 kHz deviation and 956 kHz occupied
        // bandwidth, about three times its true ~75 kHz / ~200 kHz, so no WFM rule fired. The
        // -3 dB peak bandwidth of a wide signal is roughly its modulation audio (about 15 kHz
        // for WFM), which plans a 2 Msps capture; the baseband filter then passes only ~1.5 MHz
        // and clips the ±75 kHz swing, and the s8 quantiser's coarse steps add phase noise on
        // top. The measured deviation and bandwidth land about 3x high. WFM needs a 10 Msps
        // capture (the filter and quantiser are then clean); the classifier's rate check stays
        // as the backstop.
        if (signalBandwidthHz >= WfmSignalBandwidthHz)
        {
            rate = Math.Max(rate, WfmSampleRateHz);
        }

        double seconds = Math.Clamp(
            Math.Min(MaxCaptureSeconds, maxCaptureSeconds),
            MinCaptureSeconds,
            Math.Max(MinCaptureSeconds, MaxCaptureSeconds));

        // The byte cap wins last: bytes = seconds * rate * 2 must fit maxCaptureBytes.
        double byteLimitedSeconds = (double)maxCaptureBytes / (rate * 2.0);
        seconds = Math.Min(seconds, byteLimitedSeconds);
        if (seconds < MinCaptureSeconds)
        {
            // Even the shortest window does not fit the byte cap at this rate: shrink the rate
            // until it does (the floor rate always fits any cap the sweep mode itself respects).
            while (rate > MinSampleRateHz && ((MinCaptureSeconds * rate * 2.0) > maxCaptureBytes))
            {
                rate /= 2;
            }

            seconds = Math.Min(seconds, MinCaptureSeconds);
        }

        int samples = (int)(seconds * rate);
        return new AnalyzePlan(centerHz, rate, seconds, samples);
    }

    /// <summary>
    /// Claim the analyze slot. Throws <see cref="InvalidOperationException"/> when one is
    /// already running; the caller stops the sweep before or after this call, never both.
    /// </summary>
    public void BeginAnalyze()
    {
        lock (_gate)
        {
            if (_busy)
            {
                throw new InvalidOperationException("an analyze is already running");
            }

            _busy = true;
        }
    }

    /// <summary>Release the analyze slot. Idempotent.</summary>
    public void EndAnalyze()
    {
        lock (_gate)
        {
            _busy = false;
        }
    }
}

/// <summary>The tune decision for one analyze: centre, rate, length, sample count.</summary>
public sealed record AnalyzePlan(long CenterHz, int SampleRateHz, double DurationSeconds, int SampleCount)
{
    /// <summary>True when the plan respects the controller's own bounds.</summary>
    public bool IsSane =>
        SampleRateHz >= AnalyzeController.MinSampleRateHz &&
        SampleRateHz <= AnalyzeController.MaxSampleRateHz &&
        DurationSeconds >= AnalyzeController.MinCaptureSeconds - 1e-9 &&
        DurationSeconds <= AnalyzeController.MaxCaptureSeconds + 1e-9 &&
        SampleCount == (int)(DurationSeconds * SampleRateHz);
}
