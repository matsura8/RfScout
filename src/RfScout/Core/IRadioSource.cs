namespace RfScout.Core;

/// <summary>
/// Everything a signal source must provide, so that the DSP, peak finder and decoders can be
/// unit tested against <see cref="Fakes.SyntheticRadioSource"/> (and, from M3, a recorded-file
/// source) instead of a real HackRF.
///
/// Two modes only, never at the same time:
///  * <see cref="StartSweep"/> raises <see cref="SpectrumAvailable"/> as full-span spectra;
///  * <see cref="StartCapture"/> raises <see cref="CaptureAvailable"/> once, with a narrowband IQ window.
///
/// A source must be safe to call <see cref="IDisposable.Dispose"/> from any thread at any time;
/// that is how a sweep or capture is cancelled.
/// </summary>
public interface IRadioSource : IDisposable
{
    /// <summary>Full-span power spectrum. Fires on a source worker thread, not the UI thread.</summary>
    event EventHandler<SpectrumFrameEventArgs>? SpectrumAvailable;

    /// <summary>Narrowband IQ window from <see cref="StartCapture"/>. Fires on a source worker thread.</summary>
    event EventHandler<IqCaptureEventArgs>? CaptureAvailable;

    /// <summary>
    /// M5 locate: repeated IQ windows while a locate stream runs. Fires on a source worker
    /// thread, like the other events. Sources that cannot stream raise nothing; the locate
    /// view falls back to one-shot captures.
    /// </summary>
    event EventHandler<IqCaptureEventArgs>? LocateSampleAvailable;

    /// <summary>Raised when the device/stream stops on its own, e.g. it was unplugged. Message is for the user.</summary>
    event EventHandler<RadioStoppedEventArgs>? Stopped;

    /// <summary>Pipeline counters (blocks, dropped blocks, sweeps/s). Cheap to call from the UI timer.</summary>
    RadioStats ReadStats();

    bool IsRunning { get; }

    /// <summary>What the device reported about itself, or null before one is opened.</summary>
    RadioDeviceInfo? DeviceInfo { get; }

    /// <summary>Start a sweep. Throws <see cref="InvalidOperationException"/> if something is already running.</summary>
    void StartSweep(SweepPlan plan);

    /// <summary>
    /// M5 locate: tune to <paramref name="centerHz"/> and stream repeated narrowband IQ
    /// windows of <paramref name="windowSamples"/> samples at <paramref name="sampleRateHz"/>,
    /// raising <see cref="LocateSampleAvailable"/> per window until <see cref="StopStream"/>.
    /// Throws <see cref="InvalidOperationException"/> if a sweep or capture is already running,
    /// and <see cref="NotSupportedException"/> for sources that cannot stream continuously
    /// (a recorded file, for now).
    /// </summary>
    void StartLocate(long centerHz, int sampleRateHz, int windowSamples);

    /// <summary>
    /// Start a narrowband capture of <paramref name="duration"/> at <paramref name="centerHz"/>.
    /// Throws <see cref="InvalidOperationException"/> if a sweep or capture is already running.
    /// </summary>
    void StartCapture(long centerHz, int sampleRateHz, TimeSpan duration);

    /// <summary>
    /// Change LNA and baseband VGA gains. Both are live controls, so a running sweep picks them
    /// up on the next block. Throws <see cref="ArgumentOutOfRangeException"/> for values the
    /// radio rejects (see <see cref="RadioGains"/>).
    /// </summary>
    void SetGains(int lnaGainDb, int vgaGainDb);

    /// <summary>Switch the 14 dB RF amplifier on or off. Off is the default and the safe state.</summary>
    void SetRfAmplifier(bool enabled);

    /// <summary>Switch antenna (bias-tee) power on or off. Off by default; the UI confirms first.</summary>
    void SetAntennaPower(bool enabled);

    /// <summary>
    /// Stop the running sweep or capture without releasing the device, so the next
    /// <see cref="StartSweep"/> or <see cref="StartCapture"/> can begin on the same open handle.
    /// Idempotent: stopping an already-stopped source does nothing.
    /// </summary>
    void StopStream();
}

/// <summary>Carries one <see cref="Core.SpectrumFrame"/>.</summary>
public sealed class SpectrumFrameEventArgs(SpectrumFrame frame) : EventArgs
{
    public SpectrumFrame Frame { get; } = frame;
}

/// <summary>Carries one <see cref="Core.IqCapture"/>.</summary>
public sealed class IqCaptureEventArgs(IqCapture capture) : EventArgs
{
    public IqCapture Capture { get; } = capture;
}

/// <summary>Why a source stopped; <see cref="Reason"/> is safe to show in the UI banner.</summary>
public sealed class RadioStoppedEventArgs(string reason, bool deviceLost, Exception? error = null) : EventArgs
{
    public string Reason { get; } = reason;
    public bool DeviceLost { get; } = deviceLost;
    public Exception? Error { get; } = error;
}
