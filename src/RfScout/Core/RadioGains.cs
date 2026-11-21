namespace RfScout.Core;

/// <summary>
/// Gain ranges and steps, taken from libhackrf's own checks: the LNA accepts 0-40 dB in 8 dB
/// steps, the baseband VGA 0-62 dB in 2 dB steps, and the RF amplifier is a flat 14 dB on or off.
/// Anything else the radio rejects with <c>HACKRF_ERROR_INVALID_PARAM</c>, so the app validates
/// before calling and the UI only ever offers real values.
/// </summary>
public static class RadioGains
{
    public const int LnaMinDb = 0;
    public const int LnaMaxDb = 40;
    public const int LnaStepDb = 8;

    public const int VgaMinDb = 0;
    public const int VgaMaxDb = 62;
    public const int VgaStepDb = 2;

    /// <summary>Fixed gain of <c>hackrf_set_amp_enable(1)</c>, shown so the dB scale stays honest.</summary>
    public const int RfAmpGainDb = 14;

    /// <summary>Defaults: moderate gains, amp and antenna power off as the brief requires.</summary>
    public const int DefaultLnaDb = 24;
    public const int DefaultVgaDb = 34;

    public static bool IsValidLna(int gainDb) =>
        gainDb is >= LnaMinDb and <= LnaMaxDb && gainDb % LnaStepDb == 0;

    public static bool IsValidVga(int gainDb) =>
        gainDb is >= VgaMinDb and <= VgaMaxDb && gainDb % VgaStepDb == 0;

    /// <summary>Every LNA setting the UI can offer: 0, 8, 16, 24, 32, 40.</summary>
    public static IReadOnlyList<int> LnaSteps { get; } =
        [LnaMinDb, 8, 16, 24, 32, LnaMaxDb];

    /// <summary>
    /// VGA settings, 0 through 62 in 2 dB steps. Long, so the UI shows it as a slider and snaps
    /// the value with <see cref="ClampVga"/>.
    /// </summary>
    public static IReadOnlyList<int> VgaSteps { get; } =
        [.. Enumerable.Range(VgaMinDb / VgaStepDb, (VgaMaxDb / VgaStepDb) + 1).Select(i => i * VgaStepDb)];

    /// <summary>Round to the nearest real LNA step, clamped into range.</summary>
    public static int ClampLna(int gainDb) =>
        (int)Math.Clamp(Math.Round(gainDb / (double)LnaStepDb) * LnaStepDb, LnaMinDb, LnaMaxDb);

    /// <summary>Round to the nearest real VGA step, clamped into range.</summary>
    public static int ClampVga(int gainDb) =>
        (int)Math.Clamp(Math.Round(gainDb / (double)VgaStepDb) * VgaStepDb, VgaMinDb, VgaMaxDb);

    /// <summary>
    /// Total receive gain in dB, which is what the dBFS calibration is relative to. Raising it
    /// moves weak signals up off the floor and strong ones towards the top of the scale; the
    /// noise floor rises with it, so SNR does not improve past the front-end limit.
    /// </summary>
    public static int TotalDb(int lnaDb, int vgaDb, bool rfAmplifier)
    {
        if (!IsValidLna(lnaDb))
        {
            throw new ArgumentOutOfRangeException(nameof(lnaDb), lnaDb, $"LNA must be 0-40 dB in {LnaStepDb} dB steps.");
        }

        if (!IsValidVga(vgaDb))
        {
            throw new ArgumentOutOfRangeException(nameof(vgaDb), vgaDb, $"VGA must be 0-62 dB in {VgaStepDb} dB steps.");
        }

        return lnaDb + vgaDb + (rfAmplifier ? RfAmpGainDb : 0);
    }
}
