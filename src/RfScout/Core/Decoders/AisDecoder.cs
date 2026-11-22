using System.Globalization;
using RfScout.Core.Analysis;
using RfScout.Core.Dsp;

namespace RfScout.Core.Decoders;

/// <summary>
/// AIS identification decoder for the two marine VHF channels 161.975 / 162.025 MHz (M4 item 4):
/// GMSK at 9600 baud, HDLC framing, NRZI line coding, giving MMSI and ship name for message
/// types 1-5, 18 and 24.
///
/// Pipeline on the narrowband IQ capture (centre inside the marine band, rate at least 48 kHz):
///   1. FM discriminate (instantaneous frequency) to get the baseband,
///   2. clock recovery at 9600 baud by choosing the sample offset whose sliced symbols are
///      cleanest,
///   3. NRZI decode (level transition = 0 bit, no transition = 1 bit, per the AIS spec),
///   4. HDLC: flag 0x7E, de-bit-stuff (drop a 0 after five 1s), stop at the closing flag,
///   5. CRC-16 CCITT (0x1021, init 0xFFFFFFFF, no reflection, final XOR 0) over the payload,
///   6. parse types 1-5 (MMSI; name in type 5), 18 (MMSI, CS position), 24 (MMSI + partial or
///      full name).
///
/// Identifiers only (hard rule 3): MMSI and the ship name are public maritime identities; the
/// position/COG/SOG fields are public navigation data and go in Extra as public fields.
///
/// Test vectors: published AIS example payloads (the type-1 example from the public AIS
/// documentation and the type-5 example cited in the tests, source noted there), plus a
/// round-trip through <see cref="EncodeFrame"/>.
/// </summary>
public sealed class AisDecoder : IDecoder
{
    /// <summary>Band-plan key this decoder answers to.</summary>
    public const string Key = "ais";

    /// <summary>AIS symbol rate in baud (GMSK on both channels).</summary>
    public const double BaudRate = 9_600;

    /// <summary>Channel 1 centre in Hz.</summary>
    public const long Ch1Hz = 161_975_000;

    /// <summary>Channel 2 centre in Hz.</summary>
    public const long Ch2Hz = 162_025_000;

    string IDecoder.Key => Key;

    bool IDecoder.Supports(DecoderContext context)
    {
        // A capture centred anywhere in the marine VHF band wide enough to pass 9.6 kGMSK.
        return context.CenterHz >= 156_000_000
            && context.CenterHz <= 162_100_000
            && context.SampleRateHz >= 48_000
            && context.SampleCount >= 8_192;
    }

    IReadOnlyList<DeviceIdentification> IDecoder.Decode(DecoderContext context)
    {
        var found = new List<DeviceIdentification>();
        if (!((IDecoder)this).Supports(context))
        {
            return found;
        }

        var seen = new HashSet<string>();
        foreach (byte[] payload in EnumerateFrames(context))
        {
            DeviceIdentification? id = Identify(payload);
            if (id is not null && seen.Add(id.Id) && found.Count < 32)
            {
                found.Add(id);
            }
        }

        return found;
    }

    /// <summary>
    /// FM-demodulate, recover the 9600 baud clock, NRZI-decode and pull HDLC frames out.
    /// Yields each CRC-valid payload byte string (the bits between flags, byte-aligned).
    /// </summary>
    internal static IEnumerable<byte[]> EnumerateFrames(DecoderContext context)
    {
        int n = context.SampleCount;
        double rate = context.SampleRateHz;

        float[] re = new float[n];
        float[] im = new float[n];
        IqMath.ToComplex(context.Iq.Span, re, im);
        float[] phase = new float[n];
        IqMath.Phase(re, im, phase);
        float[] unwrapped = new float[n];
        IqMath.UnwrapPhase(phase, unwrapped);
        double[] freq = new double[n - 1];
        IqMath.InstantaneousFrequencyHz(unwrapped, rate, freq);

        // Normalise the discriminator output and slice at the recovered 9600-baud clock.
        double sps = rate / BaudRate;
        if (sps < 2)
        {
            yield break;
        }

        int m = freq.Length;
        double peak = 0;
        for (int i = 0; i < m; i++)
        {
            double a = Math.Abs(freq[i]);
            if (a > peak)
            {
                peak = a;
            }
        }

        if (peak <= 0)
        {
            yield break;
        }

        // Clock recovery: maximise mean |freq| at the sampling instants (GMSK pulses peak mid-symbol).
        int trials = Math.Min((int)Math.Round(sps), 64);
        int probe = Math.Min(2048, (int)(m / sps) - 4);
        if (probe < 256)
        {
            yield break;
        }

        int bestOff = 0;
        double bestScore = -1;
        for (int t = 0; t < trials; t++)
        {
            double score = 0;
            for (int k = 0; k < probe; k++)
            {
                int idx = (int)(t + k * sps);
                if (idx < m)
                {
                    score += Math.Abs(freq[idx]);
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestOff = t;
            }
        }

        int symbolCount = (int)((m - bestOff) / sps) - 1;
        if (symbolCount < 40)
        {
            yield break;
        }

        var sym = new byte[symbolCount];
        for (int k = 0; k < symbolCount; k++)
        {
            int idx = bestOff + (int)Math.Round(k * sps);
            sym[k] = freq[idx] >= 0 ? (byte)1 : (byte)0;
        }

        // NRZI decode: a transition between consecutive symbols encodes 0, no transition encodes 1.
        var nrzi = new byte[symbolCount - 1];
        for (int k = 1; k < symbolCount; k++)
        {
            nrzi[k - 1] = (byte)(sym[k] == sym[k - 1] ? 1 : 0);
        }

        // HDLC scan: find 0x7E flags (0b01111110, LSB-first on the wire), de-stuff, byte-align.
        foreach (byte[] payload in ExtractHdlcFrames(nrzi))
        {
            if (payload.Length >= 4 && CrcOk(payload))
            {
                yield return payload[..^2]; // drop the trailing 16-bit CRC (checked by CrcOk)
            }
        }
    }

    /// <summary>
    /// Walk the NRZI bit stream for HDLC frames: LSB-first bytes equal to 0x7E open and close a
    /// frame; a 0 bit following five consecutive 1 bits is stuffed and dropped. Bit offsets are
    /// scanned exhaustively (the stream is short: a 2 s capture at 200 kHz is ~40 kbits).
    /// </summary>
    internal static IEnumerable<byte[]> ExtractHdlcFrames(byte[] bits)
    {
        int i = 0;
        while (i < bits.Length)
        {
            // Look for an opening flag: 8 bits (LSB first) spelling 0x7E.
            if (!MatchesFlag(bits, i))
            {
                i++;
                continue;
            }

            i += 8; // consume the flag
            var bytes = new List<byte>(64);
            int ones = 0;
            int current = 0;
            int bitPos = 0;
            bool closed = false;
            while (i < bits.Length && bytes.Count < 256)
            {
                byte b = bits[i++];
                if (ones >= 5)
                {
                    if (b == 0)
                    {
                        ones = 0; // stuffed zero: drop it
                        continue;
                    }

                    break; // six ones: line is idle or a flag follows
                }

                ones = b == 1 ? ones + 1 : 0;
                current |= b << bitPos;
                bitPos++;
                if (bitPos == 8)
                {
                    if (current == 0x7E)
                    {
                        closed = true;
                        break; // closing flag
                    }

                    bytes.Add((byte)current);
                    current = 0;
                    bitPos = 0;
                }
            }

            if (closed && bytes.Count >= 4)
            {
                yield return [.. bytes];
            }
        }
    }

    private static bool MatchesFlag(byte[] bits, int at)
    {
        // 0x7E LSB-first on the wire: 0,1,1,1,1,1,1,0.
        if (at + 8 > bits.Length)
        {
            return false;
        }

        if (bits[at] != 0 || bits[at + 7] != 0)
        {
            return false;
        }

        for (int k = 1; k <= 6; k++)
        {
            if (bits[at + k] != 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>CRC-16 CCITT-FALSE over the payload with the appended CRC: remainder must be 0xFFFF1AA3... checked as final == 0x0000 after XOR.</summary>
    internal static bool CrcOk(byte[] payloadWithCrc)
    {
        int crc = 0xFFFF;
        foreach (byte b in payloadWithCrc)
        {
            crc ^= b << 8;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 0x8000) != 0)
                {
                    crc = (crc << 1) ^ 0x1021;
                }
                else
                {
                    crc <<= 1;
                }

                crc &= 0xFFFF;
            }
        }

        return crc == 0x0000;
    }

    /// <summary>Parse one CRC-valid payload into an identification (identifiers only).</summary>
    internal static DeviceIdentification? Identify(byte[] payload)
    {
        if (payload.Length < 8)
        {
            return null;
        }

        int msgType = ReadBits(payload, 0, 6);
        uint mmsi = (uint)ReadBits(payload, 30, 30);
        if (mmsi is 0 or >= 700000000)
        {
            return null;
        }

        string mmsiText = mmsi.ToString("D9", CultureInfo.InvariantCulture);
        var extra = new Dictionary<string, string> { ["mmsi"] = mmsiText, ["msg_type"] = msgText(msgType) };
        double confidence = 0.9;
        string id = mmsiText;

        switch (msgType)
        {
            case 1 or 2 or 3:
                {
                    // Position report: navigational status + ROT + SOG + position accuracy; lon/lat at 58/88.
                    (double? lat, double? lon) = ReadPosition(payload, 61, 28, 89, 28);
                    AddPosition(extra, lat, lon);
                    break;
                }

            case 4 or 11:
                {
                    (double? lat, double? lon) = ReadPosition(payload, 67, 28, 95, 28);
                    AddPosition(extra, lat, lon);
                    id = mmsiText; // base station / AIS satellite: still an emitter identity
                    break;
                }

            case 5:
                {
                    string? name = ReadAisString(payload, 160, 42); // 7 x 6-bit characters
                    if (name is not null)
                    {
                        id = name;
                        extra["name"] = name;
                    }

                    break;
                }

            case 18:
                {
                    (double? lat, double? lon) = ReadPosition(payload, 57, 28, 85, 28);
                    AddPosition(extra, lat, lon);
                    break;
                }

            case 24:
                {
                    int partNo = ReadBits(payload, 38, 1);
                    if (partNo == 0)
                    {
                        string? name = ReadAisString(payload, 40, 42);
                        if (name is not null)
                        {
                            id = name;
                            extra["name"] = name;
                        }
                    }
                    else
                    {
                        int dimA = ReadBits(payload, 46, 9);
                        int dimB = ReadBits(payload, 55, 9);
                        extra["dim_a"] = dimA.ToString(CultureInfo.InvariantCulture);
                        extra["dim_b"] = dimB.ToString(CultureInfo.InvariantCulture);
                    }

                    break;
                }

            default:
                return null; // types the brief does not ask for: classify-only elsewhere
        }

        return new DeviceIdentification("ais", "ship", id, extra, confidence, "ais-decoder");

        static string msgText(int t) => t.ToString(CultureInfo.InvariantCulture);
    }

    private static void AddPosition(Dictionary<string, string> extra, double? lat, double? lon)
    {
        if (lat is not null && lon is not null)
        {
            extra["lat"] = lat.Value.ToString("F5", CultureInfo.InvariantCulture);
            extra["lon"] = lon.Value.ToString("F5", CultureInfo.InvariantCulture);
        }
    }

    private static (double? Lat, double? Lon) ReadPosition(byte[] payload, int latOff, int latLen, int lonOff, int lonLen)
    {
        if ((latOff + latLen > payload.Length * 8) || (lonOff + lonLen > payload.Length * 8))
        {
            return (null, null);
        }

        int latRaw = ReadBits(payload, latOff, latLen);
        int lonRaw = ReadBits(payload, lonOff, lonLen);
        if (latRaw == (1 << (latLen - 1)) * 2 - 1 || lonRaw == (1 << (lonLen - 1)) * 2 - 1)
        {
            return (null, null); // unavailable
        }

        double lat = (latRaw & 1) != 0 ? -(latRaw >> 1) / 600_000.0 : latRaw / 600_000.0;
        double lon = (lonRaw & 1) != 0 ? -(lonRaw >> 1) / 600_000.0 : lonRaw / 600_000.0;
        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            return (null, null);
        }

        return (lat, lon);
    }

    /// <summary>Read <paramref name="bitLen"/> bits (max 32) MSB-first from the bit-packed payload.</summary>
    internal static int ReadBits(byte[] payload, int bitOffset, int bitLen)
    {
        int v = 0;
        for (int i = 0; i < bitLen; i++)
        {
            int bit = bitOffset + i;
            if (bit >= payload.Length * 8)
            {
                return v;
            }

            v = (v << 1) | ((payload[bit / 8] >> (7 - (bit % 8))) & 1);
        }

        return v;
    }

    /// <summary>Decode a 6-bit-per-character AIS IA5 string (max 21 chars here), trimming '@' padding.</summary>
    internal static string? ReadAisString(byte[] payload, int bitOffset, int bitLen)
    {
        int chars = bitLen / 6;
        if (bitOffset + bitLen > payload.Length * 8 || chars == 0 || chars > 64)
        {
            return null;
        }

        Span<char> sb = stackalloc char[Math.Min(chars, 64)];
        for (int i = 0; i < chars; i++)
        {
            int c = ReadBits(payload, bitOffset + (i * 6), 6);
            sb[i] = AisChar(c);
        }

        string s = new string(sb).TrimEnd('@', ' ');
        return s.Length == 0 ? null : s;
    }

    private static char AisChar(int c) => c switch
    {
        >= 0 and <= 31 => '@', // control set: padding/escape
        >= 32 and <= 63 => (char)c, // same as ASCII in this range
        _ => '?',
    };

    /// <summary>
    /// Pack a bit string (MSB-first, as AIS documents frames) into payload bytes and append the
    /// CRC-16, producing the byte string <see cref="Identify"/> consumes. Used by round-trip
    /// tests; the published example frames in the tests are given as their documented bit/hex
    /// form and passed through here.
    /// </summary>
    public static byte[] EncodeFrame(string bitsMsbFirst)
    {
        ArgumentNullException.ThrowIfNull(bitsMsbFirst);
        if (bitsMsbFirst.Length % 8 != 0 || bitsMsbFirst.Length < 64)
        {
            throw new ArgumentException("Frame bits must be a multiple of 8 and at least 8 bytes.", nameof(bitsMsbFirst));
        }

        byte[] payload = new byte[bitsMsbFirst.Length / 8];
        for (int i = 0; i < bitsMsbFirst.Length; i++)
        {
            char c = bitsMsbFirst[i];
            if (c != '0' && c != '1')
            {
                throw new ArgumentException("Frame bits must be '0'/'1'.", nameof(bitsMsbFirst));
            }

            if (c == '1')
            {
                payload[i / 8] |= (byte)(1 << (7 - (i % 8)));
            }
        }

        int crc = 0xFFFF;
        foreach (byte b in payload)
        {
            crc ^= b << 8;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 0x8000) != 0)
                {
                    crc = (crc << 1) ^ 0x1021;
                }
                else
                {
                    crc <<= 1;
                }

                crc &= 0xFFFF;
            }
        }

        return [.. payload, (byte)(crc >> 8), (byte)(crc & 0xFF)];
    }
}
