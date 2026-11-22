namespace RfScout.Core;

/// <summary>Inclusive tuning range in Hz, as used by a sweep plan.</summary>
public readonly record struct FrequencyRange(long StartHz, long StopHz)
{
    public bool IsValid => StartHz > 0 && StopHz > StartHz;

    public override string ToString() => $"{StartHz / 1e6:F3}..{StopHz / 1e6:F3} MHz";
}

/// <summary>
/// What to sweep. <paramref name="Ranges"/> are the start/stop pairs handed to the radio;
/// <paramref name="BinWidthHz"/> is the resolution the caller asked for, which the radio can
/// only approach - the width it really produces is
/// <see cref="Spectrum.SweepMath.FftLengthForBinWidth"/> away from 20 Msps, so read it back
/// from <see cref="ResolvedBinWidthHz"/>.
/// </summary>
public sealed record SweepPlan(
    IReadOnlyList<FrequencyRange> Ranges,
    int BinWidthHz,
    int LnaGainDb = 24,
    int VgaGainDb = 34,
    bool RfAmpEnabled = false,
    bool AntennaPower = false)
{
    /// <summary>Sweep mode always runs the radio at 20 Msps.</summary>
    public const int SampleRateHz = Spectrum.SweepMath.SampleRateHz;

    /// <summary>FFT length the radio's bin width is derived from.</summary>
    public int FftLength => Spectrum.SweepMath.FftLengthForBinWidth(BinWidthHz);

    /// <summary>Bin width the sweep will really have.</summary>
    public double ResolvedBinWidthHz => Spectrum.SweepMath.BinWidthHz(FftLength);

    /// <summary>
    /// Bin width the display grid will have.
    ///
    /// Two things can widen it past <see cref="BinWidthHz"/>. A very wide span is one: 6 GHz at
    /// 100 kHz would be 60000 bins and 240 MB of float per frame, so
    /// <see cref="Spectrum.SpectrumGrid.BinWidthFor"/> keeps the frame to a size the UI can copy
    /// sixty times a second. The radio's own resolution is the other, and it is the one that
    /// matters: the hardware can only produce bins of <see cref="ResolvedBinWidthHz"/>, so a grid
    /// finer than that would have gaps between the block bins rather than measurements in every
    /// bin - the display would show holes, the peak detector would report a sawtooth noise floor,
    /// and a sweep would never report itself complete because the uncovered bins never fill. The
    /// grid therefore never goes below the resolution the radio actually delivers, and the number
    /// in the toolbar is the number on the axis.
    /// </summary>
    public double DisplayBinWidthHz => Math.Max(
        ResolvedBinWidthHz,
        Math.Max(BinWidthHz, Spectrum.SpectrumGrid.BinWidthFor(Ranges, ResolvedBinWidthHz)));

    public bool IsValid =>
        Ranges is { Count: > 0 } &&
        Ranges.All(r => r.IsValid) &&
        BinWidthHz is >= Spectrum.SweepMath.MinBinWidthHz and <= Spectrum.SweepMath.MaxBinWidthHz &&
        LnaGainDb is >= 0 and <= 40 &&
        VgaGainDb is >= 0 and <= 62;
}

/// <summary>
/// One tuned chunk of a sweep. libhackrf delivers sweep data as 16384-byte blocks: a 10-byte
/// header ("0x7F 0x7F" then a little-endian uint64 frequency in Hz) followed by interleaved
/// signed 8-bit I/Q. The header frequency is the low edge of the band the block covers
/// (see <see cref="Spectrum.SweepMath"/>), not the tuned centre.
/// </summary>
public readonly record struct SweepBlock(long HeaderFrequencyHz, ReadOnlyMemory<byte> IqSamples)
{
    public int SampleCount => IqSamples.Length / 2;
}

/// <summary>A completed narrowband IQ capture, used by the analyze, record and decode paths.</summary>
public sealed record IqCapture(
    long CenterHz,
    int SampleRateHz,
    int LnaGainDb,
    int VgaGainDb,
    bool RfAmpEnabled,
    bool AntennaPower,
    DateTimeOffset UtcStart,
    ReadOnlyMemory<byte> Samples)
{
    public int SampleCount => Samples.Length / 2;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)SampleCount / SampleRateHz);
}

/// <summary>
/// A full-span power spectrum, one value per <see cref="BinWidthHz"/> step.
/// <see cref="PowerDb"/> is owned by whoever receives the event, so the UI may keep it.
/// </summary>
public sealed class SpectrumFrame
{
    /// <summary>Bins that no block covered in this frame.</summary>
    public const float EmptyBin = float.MinValue;

    /// <summary>Frequency of bin 0's lower edge.</summary>
    public required long StartHz { get; init; }

    /// <summary>Exclusive upper edge of the grid.</summary>
    public required long StopHz { get; init; }

    public required double BinWidthHz { get; init; }

    /// <summary>
    /// Power per bin in dBFS, or <see cref="EmptyBin"/> where nothing was measured.
    /// The array length is <see cref="BinCount"/>.
    /// </summary>
    public required float[] PowerDb { get; init; }

    /// <summary>How many bins hold a measurement.</summary>
    public required int FilledBins { get; init; }

    /// <summary>
    /// True when this frame closes a complete pass over the span: every bin was measured since the
    /// grid was last cleared. The status bar counts sweeps per second from these frames, and the
    /// noise-floor estimator only takes statistics from complete passes, so a wide span that takes
    /// longer than the refresh interval still yields honest numbers.
    /// </summary>
    public bool SweepCompleted { get; init; }

    public required DateTimeOffset UtcTimestamp { get; init; }

    public int BinCount => PowerDb.Length;

    public bool IsEmpty(int index) => PowerDb[index] == EmptyBin;

    public double CenterFrequencyOfBin(int index) => StartHz + (index + 0.5) * BinWidthHz;

    public int BinIndexAt(double hz) =>
        (int)Math.Clamp(Math.Floor((hz - StartHz) / BinWidthHz), 0, BinCount - 1);

    /// <summary>Copy of the frame with the strongest value seen so far merged in.</summary>
    public SpectrumFrame MergePeakHold(SpectrumFrame? previous)
    {
        if (previous is null || previous.BinCount != BinCount)
        {
            return this;
        }

        float[] merged = new float[BinCount];
        int filled = 0;
        for (int i = 0; i < BinCount; i++)
        {
            float a = previous.PowerDb[i];
            float b = PowerDb[i];
            if (a == EmptyBin)
            {
                merged[i] = b;
            }
            else if (b == EmptyBin)
            {
                merged[i] = a;
            }
            else
            {
                merged[i] = Math.Max(a, b);
            }

            if (merged[i] != EmptyBin)
            {
                filled++;
            }
        }

        return new SpectrumFrame
        {
            StartHz = StartHz,
            StopHz = StopHz,
            BinWidthHz = BinWidthHz,
            PowerDb = merged,
            FilledBins = filled,
            UtcTimestamp = UtcTimestamp,
            SweepCompleted = SweepCompleted || previous.SweepCompleted,
        };
    }
}

/// <summary>RX pipeline counters, shown in the status bar.</summary>
public sealed record RadioStats(
    long BlocksReceived,
    long DroppedBlocks,
    double SweepsPerSecond,
    double SamplesPerSecond,
    long DroppedBytes = 0)
{
    public static RadioStats Empty { get; } = new(0, 0, 0, 0);

    /// <summary>True when the stream lost data, which is what the status bar flags in amber.</summary>
    public bool IsLosingData => DroppedBlocks > 0 || DroppedBytes > 0;
}

/// <summary>
/// What the radio reported about itself, read once when it is opened and shown in the status bar
/// and the About box. <paramref name="UsbApiVersion"/> is the raw firmware API number (0x0108 is
/// 1.08); sweep mode needs 0x0102 or newer.
/// </summary>
public sealed record RadioDeviceInfo(
    string SerialNumber,
    string BoardName,
    string FirmwareVersion,
    int UsbApiVersion,
    string LibraryVersion)
{
    /// <summary>Firmware API as text, the way hackrf_info prints it.</summary>
    public string UsbApiVersionText => $"1.{UsbApiVersion & 0xFF:D2}";

    /// <summary>Minimum firmware API that supports RX sweep mode.</summary>
    public const int MinSweepApiVersion = 0x0102;

    public bool SupportsSweep => UsbApiVersion >= MinSweepApiVersion;

    /// <summary>Serial shortened for the status bar: 0000...711B keeps the ends that differ.</summary>
    public string ShortSerial
    {
        get
        {
            string trimmed = SerialNumber.Trim();
            return trimmed.Length <= 12 ? trimmed : string.Concat(trimmed.AsSpan(0, 4), "...", trimmed.AsSpan(trimmed.Length - 4));
        }
    }
}
