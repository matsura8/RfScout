namespace RfScout.Core;

/// <summary>
/// A remembered span: what the toolbar shows and what <c>Ctrl+1..9</c> recall. Frequencies are
/// in Hz because everything downstream keeps Hz until the last moment, to avoid the rounding
/// drift a MHz-based design would pick up on a 1-6000 MHz span.
/// </summary>
public sealed record SweepPreset(string Name, long StartHz, long StopHz, int BinWidthHz)
{
    /// <summary>Bin widths offered in the toolbar, all realisable: 20 MHz divided by a power of
    /// two is exactly what the radio gives (see <see cref="Spectrum.SweepMath.FftLengthForBinWidth"/>).</summary>
    public static readonly int[] CommonBinWidthsHz = [78_125, 156_250, 312_500, 625_000, 1_250_000];

    /// <summary>Smallest span the radio can sweep: one block covers 15 MHz of usable spectrum.</summary>
    public const long MinSpanHz = 1_000_000;

    /// <summary>Lowest and highest frequency the HackRF One tunes (1 MHz to 6 GHz).</summary>
    public const long MinTunableHz = 1_000_000;
    public const long MaxTunableHz = 6_000_000_000;

    public bool IsValid =>
        StartHz >= MinTunableHz &&
        StopHz <= MaxTunableHz &&
        StopHz - StartHz >= MinSpanHz &&
        BinWidthHz is >= Spectrum.SweepMath.MinBinWidthHz and <= Spectrum.SweepMath.MaxBinWidthHz;

    /// <summary>The one range this preset hands the radio.</summary>
    public FrequencyRange Range => new(StartHz, StopHz);

    /// <summary>Build a plan from this preset with the given gains.</summary>
    public SweepPlan ToPlan(int lnaGainDb = 24, int vgaGainDb = 34, bool rfAmp = false, bool antennaPower = false) =>
        new([Range], BinWidthHz, lnaGainDb, vgaGainDb, rfAmp, antennaPower);

    public override string ToString() => $"{Name} ({StartHz / 1e6:F1}-{StopHz / 1e6:F1} MHz)";
}

/// <summary>
/// The preset list from the brief: full sweep plus the bands an operator actually chases. Each
/// bin width is one the hardware really produces, so the number in the toolbar is the number on
/// the axis.
/// </summary>
public static class SweepPresetLibrary
{
    /// <summary>Toolbar order, and the order <c>Ctrl+1..9</c> map onto.</summary>
    public static IReadOnlyList<SweepPreset> All { get; } =
    [
        new("Full 1-6000 MHz", 1_000_000, 6_000_000_000, 625_000),
        new("FM broadcast 88-108", 88_000_000, 108_000_000, 78_125),
        new("Airband 108-137", 108_000_000, 137_000_000, 156_250),
        new("Marine VHF / AIS 156-163", 156_000_000, 163_000_000, 156_250),
        new("NOAA WX 162.4-162.6", 162_400_000, 162_600_000, 312_500),
        new("Amateur HF 1.8-30", 1_800_000, 30_000_000, 312_500),
        new("VHF 30-300", 30_000_000, 300_000_000, 625_000),
        new("433 ISM 432-435", 432_000_000, 435_000_000, 78_125),
        new("868 ISM 863-876", 863_000_000, 876_000_000, 78_125),
        new("915 ISM 902-928", 902_000_000, 928_000_000, 156_250),
        new("1090 ADS-B 1088-1092", 1_088_000_000, 1_092_000_000, 78_125),
        new("2.4 GHz 2400-2483.5", 2_400_000_000, 2_483_500_000, 312_500),
    ];

    /// <summary>Preset by name, case-insensitive; null when the name is unknown.</summary>
    public static SweepPreset? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>Ctrl+1..9</c> recall the first nine presets, which are the ones worth a keystroke; the
    /// rest are reached from the drop-down.
    /// </summary>
    public static SweepPreset? ForShortcut(int digit) =>
        digit is >= 1 and <= 9 && digit <= All.Count ? All[digit - 1] : null;
}
