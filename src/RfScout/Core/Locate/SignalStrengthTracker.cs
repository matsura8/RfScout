namespace RfScout.Core.Locate;

/// <summary>
/// M5 locate: turns one narrowband IQ window into a single signal-strength number, in dBFS
/// on the same scale the sweep grid uses (full-scale complex sine = 0 dBFS). The mean power
/// over the window is the strength; a window with no samples is treated as silence at
/// <see cref="SilenceDb"/> rather than negative infinity, so the meter and the 60 s graph
/// always have a finite value to draw.
/// </summary>
public static class SignalStrengthTracker
{
    /// <summary>Reported strength of a window that holds no samples.</summary>
    public const double SilenceDb = -140d;

    /// <summary>
    /// Strength of one interleaved s8 IQ window in dBFS. A full-scale complex sine measures
    /// 0 dBFS; an empty window measures <see cref="SilenceDb"/>.
    /// </summary>
    public static double MeasureDb(ReadOnlyMemory<byte> iq)
    {
        if (iq.Length < 2)
        {
            return SilenceDb;
        }

        double power = Dsp.IqMath.MeanPower(iq.Span);
        return Dsp.IqMath.PowerToDb(power, SilenceDb);
    }
}
