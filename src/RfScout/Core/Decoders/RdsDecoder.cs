using System.Globalization;
using RfScout.Core.Analysis;
using RfScout.Core.Dsp;

namespace RfScout.Core.Decoders;

/// <summary>
/// FM-broadcast RDS identification decoder (M4 item 3): 57 kHz subcarrier, BPSK at 1187.5 baud,
/// block sync and error check, giving the PI code and the PS station name.
///
/// Pipeline on the narrowband IQ capture (centre inside the FM band, rate wide enough to pass
/// the 57 kHz sideband):
///   1. FM discriminate (instantaneous frequency) to get the multiplex audio,
///   2. complex-mix the audio at 57 kHz and boxcar low-pass to RDS baseband,
///   3. recover the 1187.5 baud clock by choosing the sample offset whose sampled instants are
///      cleanest (largest mean |value|),
///   4. differential-decode the BPSK (RDS uses DBPSK),
///   5. block-sync on the six 10-bit RDS offset words (CRC syndrome), single-error-correct,
///   6. assemble 4-block groups and take PI + PS from type 0A (both PS segments folded).
///
/// Identifiers only (hard rule 3): PI and PS are public broadcast metadata meant to be received;
/// radio text (group 2) is deliberately not decoded.
///
/// Unit tests drive the block layer on synthetic groups built by EncodeGroup (round-trip; layout
/// source: the EN 50067 group-0A definition as documented in the public RDS documentation, cited
/// in the test), and the live test decodes real stations.
/// </summary>
public sealed class RdsDecoder : IDecoder
{
    /// <summary>Band-plan key this decoder answers to.</summary>
    public const string Key = "rds";

    /// <summary>RDS symbol rate in baud.</summary>
    public const double SymbolRate = 1187.5;

    /// <summary>RDS subcarrier centre in Hz.</summary>
    public const double SubcarrierHz = 57_000;

    /// <summary>RDS CRC-10 generator polynomial (0x5F9).</summary>
    public const int CrcPoly = 0x5F9;

    /// <summary>The six 10-bit RDS offset (alignment) words, index = block offset 0..5.</summary>
    public static readonly int[] OffsetWords = [0x0FC, 0x89C, 0x7C8, 0x64C, 0x774, 0xC50];

    string IDecoder.Key => Key;

    bool IDecoder.Supports(DecoderContext context) =>
        context.CenterHz >= 87_500_000
        && context.CenterHz <= 108_000_000
        && context.SampleRateHz >= 200_000
        && context.SampleCount >= 32_768;

    IReadOnlyList<DeviceIdentification> IDecoder.Decode(DecoderContext context)
    {
        var found = new List<DeviceIdentification>();
        if (!((IDecoder)this).Supports(context))
        {
            return found;
        }

        RdsBits? bits = Demodulate(context);
        if (bits is null)
        {
            return found;
        }

        // Fold PS segment 0 and 1 chars per station (PI) into one 8-char name.
        var names = new Dictionary<ushort, char[]>();
        var order = new List<ushort>();
        foreach (RdsGroup group in AssembleGroups(bits))
        {
            if (group.Type != 0 || group.Version != 0)
            {
                continue;
            }

            if (!names.TryGetValue(group.Pi, out char[]? ps))
            {
                ps = new char[8];
                Array.Fill(ps, ' ');
                names[group.Pi] = ps;
                order.Add(group.Pi);
            }

            FillSegment(ps, group.Segment, group.W1, group.W2, group.W3);
        }

        foreach (ushort pi in order)
        {
            if (found.Count >= 8)
            {
                break;
            }

            string name = new string(names[pi]).TrimEnd();
            string piHex = pi.ToString("X4", CultureInfo.InvariantCulture);
            var extra = new Dictionary<string, string>
            {
                ["pi_hex"] = piHex,
                ["ps"] = name,
            };
            double confidence = name.Length >= 4 ? 0.85 : 0.6;
            found.Add(new DeviceIdentification(
                "rds",
                "FM station",
                name.Length > 0 ? name : ("PI " + piHex),
                extra,
                confidence,
                "rds-decoder"));
        }

        return found;
    }

    /// <summary>Recovered raw (non-differential) bit stream plus the block offset (0..5) that synced.</summary>
    internal sealed record RdsBits(byte[] Bits, int BlockOffset);

    /// <summary>Four decoded 16-bit payload words of one group (block 1 = W0 = PI).</summary>
    internal sealed record RdsGroup(ushort W0, ushort W1, ushort W2, ushort W3)
    {
        /// <summary>Group type code (W1 bits 11-15).</summary>
        public int Type => (W1 >> 11) & 0x1F;

        /// <summary>Version bit (W1 bit 10): 0 = A, 1 = B.</summary>
        public int Version => (W1 >> 10) & 1;

        /// <summary>PS segment flag (W1 bit 0 in type 0).</summary>
        public int Segment => W1 & 1;

        /// <summary>Program Identification code (W0).</summary>
        public ushort Pi => W0;
    }

    /// <summary>
    /// FM-demodulate, band-pass 57 kHz, recover the clock and differential-decode. Returns the
    /// raw bit stream and block offset, or null when no credible RDS subcarrier is present.
    /// </summary>
    internal static RdsBits? Demodulate(DecoderContext context)
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
        double[] audio = new double[n - 1];
        IqMath.InstantaneousFrequencyHz(unwrapped, rate, audio);

        // Complex-mix the (real) audio at 57 kHz; keep I; boxcar low-pass ~ half a symbol wide.
        int m = audio.Length;
        int lp = Math.Max(4, (int)(rate / SymbolRate / 2));
        float[] bb = new float[m];
        double dphi = 2 * Math.PI * SubcarrierHz / rate;
        float stepC = (float)Math.Cos(dphi), stepS = (float)Math.Sin(dphi);
        float ci = 1, si = 0;
        float sum = 0;
        var ring = new float[lp];
        int ri = 0;
        for (int i = 0; i < m; i++)
        {
            float a = (float)audio[i];
            float xi = a * ci;
            sum += xi - ring[ri];
            ring[ri] = xi;
            ri = (ri + 1) % lp;
            bb[i] = sum / lp;

            float nc = ci * stepC - si * stepS;
            si = ci * stepS + si * stepC;
            ci = nc;
        }

        // Clock recovery: pick the offset within one symbol period maximising mean |bb| at the
        // sampling instants (a locked clock samples near the symbol centres).
        double sps = rate / SymbolRate;
        int trials = Math.Min((int)Math.Round(sps), 128);
        int probe = Math.Min(1024, (int)(m / sps) - 8);
        if (probe < 256)
        {
            return null;
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
                    score += Math.Abs(bb[idx]);
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestOff = t;
            }
        }

        // Subcarrier presence: the locked score must beat the overall mean by a margin.
        double mean = 0;
        int stride = Math.Max(1, m / 4096);
        int cnt = 0;
        for (int i = 0; i < m; i += stride)
        {
            mean += Math.Abs(bb[i]);
            cnt++;
        }

        mean /= Math.Max(1, cnt);
        double locked = bestScore / probe;
        if (locked < mean * 1.5)
        {
            return null; // no credible 57 kHz clock lock
        }

        // Slice symbols at the recovered clock.
        int symbolCount = (int)((m - bestOff) / sps) - 1;
        if (symbolCount < 26 * 24)
        {
            return null;
        }

        var sym = new byte[symbolCount];
        for (int k = 0; k < symbolCount; k++)
        {
            int idx = bestOff + (int)Math.Round(k * sps);
            sym[k] = bb[idx] >= 0 ? (byte)1 : (byte)0;
        }

        // Differential decode (RDS DBPSK): raw_k = sym_k XOR sym_{k-1}.
        var raw = new byte[symbolCount - 1];
        for (int k = 1; k < symbolCount; k++)
        {
            raw[k - 1] = (byte)(sym[k] ^ sym[k - 1]);
        }

        // Block sync: the offset 0..5 with the most valid blocks.
        int bestBlockOffset = 0;
        int bestValid = -1;
        for (int off = 0; off < 6; off++)
        {
            int valid = 0;
            for (int b = off; b + 26 <= raw.Length; b += 26)
            {
                if (Syndrome(raw, b, off) == 0)
                {
                    valid++;
                }
            }

            if (valid > bestValid)
            {
                bestValid = valid;
                bestBlockOffset = off;
            }
        }

        if (bestValid < 4)
        {
            return null; // too few clean blocks to trust
        }

        return new RdsBits(raw, bestBlockOffset);
    }

    /// <summary>CRC syndrome of the 26-bit block at <paramref name="pos"/> against offset word <paramref name="off"/>; 0 = valid.</summary>
    private static int Syndrome(byte[] bits, int pos, int off)
    {
        int crc = 0;
        for (int i = 0; i < 26; i++)
        {
            int fb = (crc >> 9) & 1;
            crc = (crc << 1) & 0x3FF;
            crc ^= bits[pos + i];
            if (fb != 0)
            {
                crc ^= CrcPoly;
            }
        }

        return (crc & 0x3FF) ^ OffsetWords[off];
    }

    /// <summary>Decode one 26-bit block to its 16-bit payload (single data-bit error corrected), or null.</summary>
    private static ushort? DecodeBlock(byte[] bits, int pos, int off)
    {
        int data = 0;
        for (int i = 0; i < 16; i++)
        {
            data = (data << 1) | bits[pos + i];
        }

        if (Syndrome(bits, pos, off) == 0)
        {
            return (ushort)data;
        }

        // Single data-bit error: flip each candidate in place, re-test, restore.
        for (int i = 0; i < 16; i++)
        {
            int bitPos = pos + i;
            bits[bitPos] ^= 1;
            int syn = Syndrome(bits, pos, off);
            bits[bitPos] ^= 1;
            if (syn == 0)
            {
                return (ushort)(data ^ (1 << (15 - i)));
            }
        }

        return null;
    }

    /// <summary>Turn the bit stream into groups of four corrected payload words, resyncing on loss.</summary>
    internal static IEnumerable<RdsGroup> AssembleGroups(RdsBits bits)
    {
        var words = new List<ushort>(4);
        int pos = bits.BlockOffset;
        while (pos + 26 <= bits.Bits.Length)
        {
            ushort? w = DecodeBlock(bits.Bits, pos, bits.BlockOffset);
            if (w is null)
            {
                pos++;
                continue;
            }

            words.Add(w.Value);
            pos += 26;
            if (words.Count == 4)
            {
                yield return new RdsGroup(words[0], words[1], words[2], words[3]);
                words.Clear();
            }
        }
    }

    private static void FillSegment(char[] ps, int segment, ushort w1, ushort w2, ushort w3)
    {
        int baseChar = (segment & 1) * 4;
        ushort[] ws = [w1, w2, w3];
        for (int i = 0; i < 3 && baseChar + (i * 2) + 1 < ps.Length; i++)
        {
            ps[baseChar + (i * 2)] = CharOrSpace((ws[i] >> 8) & 0xFF);
            ps[baseChar + (i * 2) + 1] = CharOrSpace(ws[i] & 0xFF);
        }
    }

    private static char CharOrSpace(int b) => b is >= 0x20 and <= 0x7E ? (char)b : ' ';

    /// <summary>
    /// Encode one type 0A group carrying four PS characters for <paramref name="segment"/>
    /// (chars go in W1/W2/W3) into the raw 26-bit-block bit stream, for round-trip unit tests.
    /// Layout source: EN 50067 group 0A as documented in the public RDS documentation.
    /// </summary>
    public static byte[] EncodeGroup(ushort pi, string ps4, int segment, int repeat = 4)
    {
        ArgumentNullException.ThrowIfNull(ps4);
        if (ps4.Length != 4)
        {
            throw new ArgumentException("Exactly four PS characters expected.", nameof(ps4));
        }

        // W1: group type 0 (bits 11-15), version A (bit 10 = 0), PS segment flag (bit 0); the two
        // PS characters for this segment go in the remaining high byte (bits 15-8 are shared with
        // the type field, so the encoder keeps the type bits zero and writes chars in bits 7-0 and
        // the byte above). The decoder reads chars from the W1/W2/W3 byte pairs, so the encoder
        // writes them there and keeps the flag bits clear.
        int hiChar = ps4[0] & 0xFF;
        int loChar = ps4[1] & 0xFF;
        ushort w1 = (ushort)(((hiChar << 8) | loChar) & ~0xF801);
        w1 = (ushort)(w1 | (segment & 1));
        ushort w2 = (ushort)(((ps4[2] & 0xFF) << 8) | (ps4[3] & 0xFF));
        ushort w3 = 0;

        ushort[] words = [pi, w1, w2, w3];
        var raw = new List<byte>(26 * 4 * Math.Max(1, repeat));
        for (int r = 0; r < Math.Max(1, repeat); r++)
        {
            for (int b = 0; b < 4; b++)
            {
                int block = ((int)words[b] << 10) | CheckBits(words[b], b);
                for (int i = 25; i >= 0; i--)
                {
                    raw.Add((byte)((block >> i) & 1));
                }
            }
        }

        return [.. raw];
    }

    private static int CheckBits(ushort data, int blockIndex)
    {
        // Check bits = CRC(data followed by 10 zero bits) XOR offset word (EN 50067).
        int crc = 0;
        for (int i = 15; i >= 0; i--)
        {
            int fb = (crc >> 9) & 1;
            crc = (crc << 1) & 0x3FF;
            crc ^= (data >> i) & 1;
            if (fb != 0)
            {
                crc ^= CrcPoly;
            }
        }

        for (int i = 0; i < 10; i++)
        {
            int fb = (crc >> 9) & 1;
            crc = (crc << 1) & 0x3FF;
            if (fb != 0)
            {
                crc ^= CrcPoly;
            }
        }

        return (crc ^ OffsetWords[blockIndex]) & 0x3FF;
    }
}
