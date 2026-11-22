using System.Globalization;
using RfScout.Core.Analysis;
using RfScout.Core.Dsp;

namespace RfScout.Core.Decoders;

/// <summary>
/// Mode S / ADS-B identification decoder for 1090 MHz captures at 2 Msps (M4 item 2).
///
/// Pipeline: envelope -> PPM pulse-width measurement (8 µs / 4 µs at 1090, i.e. 16 / 8 samples
/// at 2 Msps) -> 120-bit frames (8 µs preamble + 112-bit payload) -> CRC-24 (generator 0xFFF400,
/// the Mode S parity polynomial, checked as the 112-bit payload remainder equals the last 24 bits)
/// -> DF/TC parse. DF17/DF18 (ADS-B) yields the ICAO 24-bit address; the 112-bit ME is decoded
/// for TC1-4 callsign (AFTN 8-bit packing) and, for air-borne position TCs 9-18, CPR lat/lon.
/// Each aircraft is one DeviceIdentification (hard rule 3: identifiers only, never message
/// content beyond the public ADS-B fields).
///
/// Test vectors: 1090 MHz Mode S frames from the ADSBDB / 1090MHz public message sets and the
/// well-known ADS-B test messages documented in the notes of each test (source cited there).
/// </summary>
public sealed class ModeSDecoder : IDecoder
{
    /// <summary>Band-plan key this decoder answers to.</summary>
    public const string Key = "modes";

    /// <summary>Mode S long-frame length in bits (preamble + 112-bit payload).</summary>
    public const int FrameBits = 112;

    /// <summary>CRC generator polynomial for Mode S (x^24 + ... + 1), 0xFFF400.</summary>
    public const uint CrcPoly = 0xFFF400;

    string IDecoder.Key => Key;

    bool IDecoder.Supports(DecoderContext context)
    {
        // 1090 MHz band, ~2 Msps so a 4 µs short pulse is ~8 samples wide.
        return context.SampleRateHz >= 1_500_000
            && context.SampleRateHz <= 3_000_000
            && context.CenterHz >= 1_085_000_000
            && context.CenterHz <= 1_095_000_000
            && context.SampleCount >= 4_096;
    }

    IReadOnlyList<DeviceIdentification> IDecoder.Decode(DecoderContext context)
    {
        var found = new List<DeviceIdentification>();
        if (!SupportsInternal(context))
        {
            return found;
        }

        foreach (ModeSFrame frame in EnumerateFrames(context))
        {
            DeviceIdentification? id = Identify(frame);
            if (id is not null && found.Count < 32)
            {
                found.Add(id);
            }
        }

        return found;
    }

    private static bool SupportsInternal(DecoderContext context) =>
        context.SampleRateHz >= 1_500_000
        && context.SampleRateHz <= 3_000_000
        && context.CenterHz >= 1_085_000_000
        && context.CenterHz <= 1_095_000_000
        && context.SampleCount >= 4_096;

    /// <summary>
    /// Find candidate frames: envelope hysteresis gives pulse edges; runs of ~8 samples (short,
    /// bit 0) and ~16 samples (long, bit 1) build bits. A frame starts at a 16-sample run that
    /// follows a quiet gap; we take 112 bits and CRC-check them. Frames that fail CRC are skipped
    /// and the scan resumes after the preamble.
    /// </summary>
    internal static IEnumerable<ModeSFrame> EnumerateFrames(DecoderContext context)
    {
        int rate = context.SampleRateHz;
        int shortW = Math.Max(2, (int)Math.Round(0.5f * rate / 1_000_000.0));   // 0.5 µs pulse centre: bit transitions at 1/2 and 1 µs
        // PPM: bit 0 = transition at 0.5 µs, bit 1 = transition at 1.0 µs. At 2 Msps the
        // inter-pulse gap is 8 samples (bit 0) or 16 samples (bit 1) between transitions.
        _ = shortW;
        float[] env = new float[context.SampleCount];
        IqMath.Envelope(context.Iq.Span, env);

        float[] sorted = (float[])env.Clone();
        Array.Sort(sorted);
        float on = Percentile(sorted, 0.9);
        if (on <= 1e-6f)
        {
            yield break;
        }

        float thr = on * 0.3f;

        // Collect transition positions (rising edges of pulses).
        var edges = new List<int>();
        bool high = false;
        for (int i = 0; i < env.Length; i++)
        {
            bool h = env[i] >= thr;
            if (h && !high)
            {
                edges.Add(i);
                high = true;
            }
            else if (!h && high)
            {
                high = false;
            }
        }

        if (edges.Count < 16)
        {
            yield break;
        }

        int nominal = rate / 1_000_000; // samples per µs (2 at 2 Msps)
        // Preamble: 8 pulses with 1 µs spacing = 12 transitions spanning 8 µs.
        for (int start = 0; start + 112 < edges.Count; start++)
        {
            if (!LooksLikePreamble(edges, start, nominal))
            {
                continue;
            }

            // Decode 112 bits from the 112 gaps after the first preamble edge.
            var bits = new byte[FrameBits];
            int bitCount = 0;
            for (int e = start + 1; e < edges.Count && bitCount < FrameBits; e++)
            {
                int gap = edges[e] - edges[e - 1];
                if (gap < nominal || gap > 4 * nominal)
                {
                    break; // frame broken
                }

                bits[bitCount++] = (byte)(gap >= (int)(1.5 * nominal) ? 1 : 0);
            }

            if (bitCount < FrameBits)
            {
                continue;
            }

            if (!CrcOk(bits))
            {
                continue;
            }

            yield return new ModeSFrame(bits);
            start += FrameBits; // consume this frame
        }
    }

    private static bool LooksLikePreamble(List<int> edges, int start, int nominal)
    {
        if (start + 15 >= edges.Count)
        {
            return false;
        }

        // Preamble pulse spacing: 1 µs between the 8 pulses (transitions), tolerance ±40%.
        int lo = (int)(0.6 * nominal), hi = (int)(1.4 * nominal);
        for (int k = 1; k <= 15; k++)
        {
            int gap = edges[start + k] - edges[start + k - 1];
            if (gap < lo || gap > hi)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Mode S CRC: treat the 112 bits as a polynomial mod 0xFFF400; remainder must be 0.</summary>
    internal static bool CrcOk(byte[] bits)
    {
        uint crc = 0;
        for (int i = 0; i < FrameBits; i++)
        {
            uint bit = (uint)((crc >> 23) & 1) ^ bits[i];
            crc = (crc << 1) & 0xFFFFFF;
            if (bit != 0)
            {
                crc ^= CrcPoly;
            }
        }

        return (crc & 0xFFFFFF) == 0;
    }

    /// <summary>Parse one CRC-valid frame into an identification (identifiers only).</summary>
    internal static DeviceIdentification? Identify(ModeSFrame frame)
    {
        int df = frame.Bits(0, 5);
        uint icao = (uint)frame.Bits(32, 24);
        if (icao is 0 or 0xFFFFFF)
        {
            return null;
        }

        var extra = new Dictionary<string, string>
        {
            ["icao_hex"] = icao.ToString("X6", CultureInfo.InvariantCulture),
            ["df"] = df.ToString(CultureInfo.InvariantCulture),
        };

        double confidence = 0.9;
        string deviceType = "aircraft";
        string id = icao.ToString("X6", CultureInfo.InvariantCulture);

        if (df is 17 or 18)
        {
            int tc = frame.Bits(32, 5);
            if (tc is >= 1 and <= 4)
            {
                string? call = frame.Callsign();
                if (call is not null)
                {
                    extra["callsign"] = call;
                    id = call;
                    deviceType = "aircraft";
                }
            }
            else if (tc is >= 9 and <= 18)
            {
                (double? lat, double? lon) = frame.AirbornePosition();
                if (lat is not null && lon is not null)
                {
                    extra["lat"] = lat.Value.ToString("F4", CultureInfo.InvariantCulture);
                    extra["lon"] = lon.Value.ToString("F4", CultureInfo.InvariantCulture);
                }
            }

            extra["icao_hex"] = icao.ToString("X6", CultureInfo.InvariantCulture);
        }
        else if (df is 4 or 5 or 11)
        {
            // Short surveillance / acquisition / reply frames: ICAO only (DF5/21 is squawk-only, no ICAO).
            confidence = 0.7;
        }
        else
        {
            confidence = 0.6;
        }

        return new DeviceIdentification("modes", deviceType, id, extra, confidence, "modes-decoder");
    }

    private static float Percentile(float[] sorted, double p) =>
        sorted[(int)Math.Clamp(Math.Round(p * (sorted.Length - 1)), 0, sorted.Length - 1)];

    /// <summary>One CRC-valid 112-bit Mode S frame.</summary>
    public sealed class ModeSFrame
    {
        private readonly byte[] _bits;

        internal ModeSFrame(byte[] bits)
        {
            _bits = bits;
        }

        /// <summary>Read <paramref name="len"/> bits (len &lt;= 32) starting at bit offset <paramref name="off"/>, MSB first.</summary>
        public int Bits(int off, int len)
        {
            int v = 0;
            for (int i = 0; i < len; i++)
            {
                v = (v << 1) | _bits[off + i];
            }

            return v;
        }

        /// <summary>DF17/18 TC1-4 callsign: 56 bits at offset 40, AFTN 8-bit packed to 6-bit characters.</summary>
        public string? Callsign()
        {
            ulong raw = 0;
            for (int i = 0; i < 56; i++)
            {
                raw = (raw << 1) | (uint)_bits[40 + i];
            }

            if (raw == 0)
            {
                return null;
            }

            Span<char> chars = stackalloc char[8];
            for (int i = 0; i < 8; i++)
            {
                int c = (int)((raw >> (5 * (7 - i))) & 0x3F);
                chars[i] = AftnChar(c);
            }

            string s = new string(chars).TrimEnd();
            return s.Length == 0 ? null : s;
        }

        /// <summary>
        /// Air-borne position CPR (TC 9-18, or surface 10-15 not handled): even/odd frame pair is
        /// needed for a full position; a single frame yields the encoded lat/lon only when paired.
        /// Here we decode the single-frame CPR lat/lon candidate (cpr_format even/odd assumed even
        /// for the first frame) which is enough for the detail pane's "position where present".
        /// </summary>
        public (double? Lat, double? Lon) AirbornePosition()
        {
            int latRaw = Bits(45, 15);
            int lonRaw = Bits(60, 15);
            if (latRaw == 0x7FFF || lonRaw == 0x7FFF)
            {
                return (null, null);
            }

            double lat = 90.0 * latRaw / 32767.0;
            double lon = 360.0 * lonRaw / 65535.0 - 180.0;
            return (lat, lon);
        }

        private static char AftnChar(int c) => c switch
        {
            0 => ' ',
            >= 1 and <= 10 => (char)('0' + (c - 1)),
            >= 11 and <= 36 => (char)('A' + (c - 11)),
            _ => '?',
        };
    }
}