namespace RfScout.Core.Display;

/// <summary>The colour ramps the waterfall and spectrum can be drawn with.</summary>
public enum ColorMapKind
{
    /// <summary>Perceptually uniform, colour-blind safe, the default.</summary>
    Viridis = 0,

    /// <summary>Monotone luminance: for projectors, prints, and anyone who finds hue ambiguous.</summary>
    Grayscale = 1,
}

/// <summary>
/// 256-entry colour lookup tables, as raw RGB bytes (index <c>3*i, 3*i+1, 3*i+2</c> for r, g, b
/// of entry <i>i</i>).
///
/// The viridis table is the matplotlib ramp, transcribed verbatim from the d3-scale-chromatic
/// source, which carries the same table matplotlib ships. It is embedded rather than generated
/// because it has no closed form, and tests check it: the endpoints and the monotone-luminance
/// property are what make a waterfall readable, so a transcription slip should fail a test rather
/// than quietly change the display.
/// </summary>
public static class ColorMap
{
    public const int Entries = 256;

    /// <summary>Viridis as 256 six-digit hex colours concatenated. Starts #440154, ends #FDE725.</summary>
    private const string ViridisHex =
        "44015444025645045745055946075a46085c460a5d460b5e470d60470e6147106347116447136548146748166" +
        "848176948186a481a6c481b6d481c6e481d6f481f7048207148217348237448247548257648267748287848297" +
        "9472a7a472c7a472d7b472e7c472f7d46307e46327e46337f463480453581453781453882443983443a83443b8" +
        "4433d84433e85423f854240864241864142874144874045884046883f47883f48893e49893e4a893e4c8a3d4d8" +
        "a3d4e8a3c4f8a3c508b3b518b3b528b3a538b3a548c39558c39568c38588c38598c375a8c375b8d365c8d365d8" +
        "d355e8d355f8d34608d34618d33628d33638d32648e32658e31668e31678e31688e30698e306a8e2f6b8e2f6c8" +
        "e2e6d8e2e6e8e2e6f8e2d708e2d718e2c718e2c728e2c738e2b748e2b758e2a768e2a778e2a788e29798e297a8" +
        "e297b8e287c8e287d8e277e8e277f8e27808e26818e26828e26828e25838e25848e25858e24868e24878e23888" +
        "e23898e238a8d228b8d228c8d228d8d218e8d218f8d21908d21918c20928c20928c20938c1f948c1f958b1f968" +
        "b1f978b1f988b1f998a1f9a8a1e9b8a1e9c891e9d891f9e891f9f881fa0881fa1881fa1871fa28720a38620a486" +
        "21a58521a68522a78522a88423a98324aa8325ab8225ac8226ad8127ad8128ae8029af7f2ab07f2cb17e2db27d2" +
        "eb37c2fb47c31b57b32b67a34b67935b77937b87838b9773aba763bbb753dbc743fbc7340bd7242be7144bf7046" +
        "c06f48c16e4ac16d4cc26c4ec36b50c46a52c56954c56856c66758c7655ac8645cc8635ec96260ca6063cb5f65" +
        "cb5e67cc5c69cd5b6ccd5a6ece5870cf5773d05675d05477d1537ad1517cd2507fd34e81d34d84d44b86d54989" +
        "d5488bd6468ed64590d74393d74195d84098d83e9bd93c9dd93ba0da39a2da37a5db36a8db34aadc32addc30b0" +
        "dd2fb2dd2db5de2bb8de29bade28bddf26c0df25c2df23c5e021c8e020cae11fcde11dd0e11cd2e21bd5e21ad8" +
        "e219dae319dde318dfe318e2e418e5e419e7e419eae51aece51befe51cf1e51df4e61ef6e620f8e621fbe723fd" +
        "e725";

    private static readonly byte[] ViridisRgb = BuildViridis();
    private static readonly byte[] GrayscaleRgb = BuildGrayscale();

    /// <summary>The table for a map, as <see cref="Entries"/> RGB triples.</summary>
    public static byte[] Lookup(ColorMapKind kind) => kind switch
    {
        ColorMapKind.Grayscale => GrayscaleRgb,
        _ => ViridisRgb,
    };

    /// <summary>
    /// Colour at <paramref name="t"/> (0 = bottom of the ramp, 1 = top) written into a pixel as
    /// BGRA, the byte order a WPF <c>WriteableBitmap</c> in <c>Bgra32</c> wants. Values outside
    /// 0..1 are clamped, because the waterfall hands over whatever the auto-range produced.
    /// </summary>
    public static void WritePixel(Span<byte> bgra, double t, ColorMapKind kind)
    {
        byte[] table = Lookup(kind);
        int index = IndexOf(t);
        bgra[0] = table[(3 * index) + 2];
        bgra[1] = table[(3 * index) + 1];
        bgra[2] = table[3 * index];
        bgra[3] = 255;
    }

    /// <summary>LUT index for a 0..1 position, clamped. NaN becomes 0 rather than an out-of-range read.</summary>
    public static int IndexOf(double t)
    {
        if (!(t > 0))
        {
            return 0;
        }

        if (t >= 1)
        {
            return Entries - 1;
        }

        return (int)(t * (Entries - 1));
    }

    /// <summary>RGB triple of one entry, for tests and for the colour-scale legend.</summary>
    public static (byte R, byte G, byte B) Entry(int index, ColorMapKind kind)
    {
        byte[] table = Lookup(kind);
        int i = index & (Entries - 1);
        return (table[(3 * i)], table[(3 * i) + 1], table[(3 * i) + 2]);
    }

    private static byte[] BuildViridis()
    {
        // Checked before decoding so a bad paste says what went wrong instead of painting grey.
        if (ViridisHex.Length != Entries * 6)
        {
            throw new InvalidOperationException(
                $"Viridis table must be {Entries} six-digit colours ({Entries * 6} characters), but is {ViridisHex.Length}.");
        }

        byte[] rgb = new byte[Entries * 3];
        for (int i = 0; i < Entries; i++)
        {
            int at = i * 6;
            rgb[(3 * i)] = ParseHexPair(ViridisHex, at);
            rgb[(3 * i) + 1] = ParseHexPair(ViridisHex, at + 2);
            rgb[(3 * i) + 2] = ParseHexPair(ViridisHex, at + 4);
        }

        return rgb;
    }

    private static byte[] BuildGrayscale()
    {
        byte[] rgb = new byte[Entries * 3];
        for (int i = 0; i < Entries; i++)
        {
            rgb[(3 * i)] = (byte)i;
            rgb[(3 * i) + 1] = (byte)i;
            rgb[(3 * i) + 2] = (byte)i;
        }

        return rgb;
    }

    private static byte ParseHexPair(string hex, int at) => (byte)((Digit(hex[at]) * 16) + Digit(hex[at + 1]));

    private static int Digit(char c) =>
        c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => throw new InvalidOperationException($"Not a hex digit: '{c}'"),
        };
}

/// <summary>
/// Maps dBFS to a position on the colour ramp, and back again for the scale labels. Two ways to
/// set it: explicit dB min/max from the toolbar, or <see cref="AutoRange"/>, which finds the span
/// the energy actually occupies so that a weak signal still shows colour instead of a flat floor.
/// </summary>
public sealed class DbRange
{
    /// <summary>Narrowest span the auto-ranger accepts, so a dead-flat floor does not blow up.</summary>
    public const double MinSpanDb = 20;

    /// <summary>Widest span it accepts, so one huge spike cannot hide the rest of the band.</summary>
    public const double MaxSpanDb = 140;

    private const double LowPercentile = 20;
    private const double HighPercentile = 99.5;

    public DbRange(double minDb = -140, double maxDb = -20) => Set(minDb, maxDb);

    /// <summary>Bottom of the scale, dBFS.</summary>
    public double MinDb { get; private set; }

    /// <summary>Top of the scale, dBFS.</summary>
    public double MaxDb { get; private set; }

    public double SpanDb => MaxDb - MinDb;

    /// <summary>
    /// Set both ends explicitly. A reversed pair is swapped rather than rejected, and a pair too
    /// close together is widened to a 1 dB span: the toolbar can hand over min &gt; max or two
    /// equal values while the operator is mid-typing, and a zero or negative span makes the plot
    /// draw nothing at all, which looks like a dead radio rather than a half-entered number.
    /// </summary>
    public void Set(double minDb, double maxDb)
    {
        double low = Math.Min(minDb, maxDb);
        double high = Math.Max(minDb, maxDb);
        if (double.IsNaN(low) || double.IsNaN(high) || high - low < 1)
        {
            high = low + 1;
        }

        MinDb = low;
        MaxDb = high;
    }

    /// <summary>
    /// Choose the range from the bins that hold data: 20th percentile up to 99.5th, widened to at
    /// least <see cref="MinSpanDb"/>. Empty bins are skipped, which matters on the first sweep of
    /// a span when most of the grid has not been filled yet.
    /// </summary>
    /// <returns>false when there was too little to judge, leaving the previous range in force.</returns>
    public bool AutoRange(ReadOnlySpan<float> powerDb, int filledBins)
    {
        int count = Math.Min(filledBins, powerDb.Length);
        if (count == 0)
        {
            return false;
        }

        float[] copy = new float[count];
        int used = 0;
        for (int i = 0; i < count; i++)
        {
            float value = powerDb[i];
            if (value != SpectrumFrame.EmptyBin && !float.IsNaN(value))
            {
                copy[used++] = value;
            }
        }

        if (used < 8)
        {
            return false;
        }

        Span<float> filled = copy.AsSpan(0, used);
        filled.Sort();
        double low = PercentileOf(filled, LowPercentile);
        double high = PercentileOf(filled, HighPercentile);

        // Widening symmetrically about the middle keeps a peak where it is instead of sliding the
        // whole ramp the moment one strong signal appears.
        double span = Math.Clamp(high - low, MinSpanDb, MaxSpanDb);
        double centre = (high + low) / 2;
        Set(centre - (span / 2), centre + (span / 2));
        return true;
    }

    /// <summary>Position on the ramp: 0 at <see cref="MinDb"/>, 1 at <see cref="MaxDb"/>.</summary>
    public double Normalize(double db) =>
        double.IsNaN(db) ? 0 : Math.Clamp((db - MinDb) / SpanDb, 0, 1);

    /// <summary>LUT index for a dB value, ready for <see cref="ColorMap.WritePixel"/>.</summary>
    public int IndexOf(double db) => ColorMap.IndexOf(Normalize(db));

    /// <summary>dB value at a ramp position, for labelling the colour scale.</summary>
    public double DbAt(double t) => MinDb + (Math.Clamp(t, 0, 1) * SpanDb);

    private static double PercentileOf(ReadOnlySpan<float> sorted, double percentile)
    {
        double index = (percentile / 100.0) * (sorted.Length - 1);
        int lo = (int)Math.Floor(index);
        int hi = (int)Math.Ceiling(index);
        return lo == hi ? sorted[lo] : (sorted[lo] * (hi - index)) + (sorted[hi] * (index - lo));
    }
}
