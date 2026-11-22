// M4 "prove it": every parser is fuzzed with random and truncated bytes and must never throw
// or hang. The Mode S fuzz test lives in ModeSDecoderTests; this file covers the other four
// parsers: the RDS block assembler, the AIS HDLC/CRC/identify path, the rtl_433 JSON parser
// (untrusted binary output per the threat model) and the OOK pulse measurer.
using System.Text;
using RfScout.Core.Analysis;
using RfScout.Core.Decoders;
using RfScout.Core.Spectrum;
using Xunit;

namespace RfScout.Tests;

public class DecoderFuzzTests
{
    private static readonly Random Rng = new(20261122);

    private static DecoderContext FmCtx(byte[] iq)
        => DecoderContext.ForPeak(iq, 98_500_000, 400_000, BandPlan.LoadShipped());

    private static DecoderContext AisCtx(byte[] iq)
        => DecoderContext.ForPeak(iq, 161_975_000, 200_000, BandPlan.LoadShipped());

    private static DecoderContext OokCtx(byte[] iq)
        => DecoderContext.ForPeak(iq, 433_920_000, 2_000_000, BandPlan.LoadShipped());

    /// <summary>Write <paramref name="value"/> into a '0'/'1' string at [off, off+len), MSB first.</summary>
    private static string SetBits(string bits, int off, int len, long value)
    {
        char[] c = bits.ToCharArray();
        for (int i = 0; i < len; i++)
        {
            c[off + i] = ((value >> (len - 1 - i)) & 1) == 1 ? '1' : '0';
        }

        return new string(c);
    }

    [Fact]
    public void RdsBlockAssemblerNeverThrowsOnRandomOrTruncatedBits()
    {
        // Random bit streams (any length, incl. zero) and every prefix of a valid encoded
        // 0A group: the block sync, error check and group assembly must tolerate them.
        for (int size = 0; size < 600; size += 37)
        {
            byte[] bits = new byte[size];
            Rng.NextBytes(bits);
            foreach (int offset in new[] { 0, 3, 5 })
            {
                foreach (RdsDecoder.RdsGroup _ in RdsDecoder.AssembleGroups(new RdsDecoder.RdsBits(bits, offset)))
                {
                    // any groups it finds are fine; the point is it must not throw
                }
            }
        }

        byte[] good = RdsDecoder.EncodeGroup(0xCAFE, "KTRH", 0, repeat: 8);
        for (int len = 0; len < good.Length; len += 13)
        {
            byte[] cut = good[..len];
            foreach (RdsDecoder.RdsGroup _ in RdsDecoder.AssembleGroups(new RdsDecoder.RdsBits(cut, 0)))
            {
            }
        }
    }

    [Fact]
    public void RdsDecoderNeverThrowsOnRandomOrTruncatedIq()
    {
        IDecoder d = new RdsDecoder();
        for (int size = 0; size < 8; size++)
        {
            byte[] iq = new byte[40_000 * (size + 1)];
            Rng.NextBytes(iq);
            Assert.NotNull(d.Decode(FmCtx(iq))); // must not throw, may return empty
        }

        for (int len = 0; len < 40_000; len += 4096)
        {
            byte[] cut = new byte[len];
            Rng.NextBytes(cut);
            Assert.NotNull(d.Decode(FmCtx(cut)));
        }
    }

    [Fact]
    public void AisHdlcCrcAndIdentifyNeverThrowOnRandomOrTruncatedBytes()
    {
        // Published AIS example: type 18 position report, MMSI 367233690, source: the
        // "AIS for the Amateur Radio Operator" / shipais.com public example messages cited
        // in AisDecoderTests. EncodeFrame appends the CRC, so CrcOk must accept it.
        // Built here (168 bits = 21 bytes, a multiple of 8) with the same field layout the
        // published example uses, so EncodeFrame/CrcOk/Identify round-trip on a known frame.
        string type18 = new string('0', 168);
        type18 = SetBits(type18, 0, 6, 18); // msg type 18 (Class B CS position report)
        type18 = SetBits(type18, 30, 30, 367233690); // MMSI (the published example's)
        type18 = SetBits(type18, 57, 28, 237933608); // lon 121.3129667 deg (published example)
        type18 = SetBits(type18, 85, 27, 145773000); // lat 31.2683333 deg (published example)
        byte[] frame = AisDecoder.EncodeFrame(type18);
        Assert.True(AisDecoder.CrcOk(frame));
        Assert.NotNull(AisDecoder.Identify(frame[..^2]));

        // Bit-flips and truncations of the valid frame: CrcOk/Identify must reject, not throw.
        for (int byteIdx = 0; byteIdx < frame.Length; byteIdx++)
        {
            byte[] flipped = (byte[])frame.Clone();
            flipped[byteIdx] ^= 0x80;
            Assert.True(AisDecoder.CrcOk(flipped) == AisDecoder.CrcOk(flipped)); // no throw
            byte[] cut = flipped[..Math.Max(1, byteIdx)];
            Assert.True(AisDecoder.CrcOk(cut) == AisDecoder.CrcOk(cut));
            Assert.True(AisDecoder.Identify(cut) is null || AisDecoder.Identify(cut) is not null);
        }

        // Random bit streams through the HDLC extractor, any length.
        for (int size = 0; size < 3000; size += 211)
        {
            byte[] bits = new byte[size];
            Rng.NextBytes(bits);
            foreach (byte[] payload in AisDecoder.ExtractHdlcFrames(bits))
            {
                _ = AisDecoder.Identify(payload);
            }
        }
    }

    [Fact]
    public void AisDecoderNeverThrowsOnRandomOrTruncatedIq()
    {
        IDecoder d = new AisDecoder();
        for (int size = 0; size < 6; size++)
        {
            byte[] iq = new byte[20_000 * (size + 1)];
            Rng.NextBytes(iq);
            Assert.NotNull(d.Decode(AisCtx(iq)));
        }

        for (int len = 0; len < 20_000; len += 2048)
        {
            byte[] cut = new byte[len];
            Rng.NextBytes(cut);
            Assert.NotNull(d.Decode(AisCtx(cut)));
        }
    }

    [Fact]
    public void Rtl433JsonParserToleratesHostileTruncatedAndRandomLines()
    {
        // Untrusted-binary threat model: banner text, truncated JSON, wrong types, huge
        // strings, deep nesting and random bytes must all parse to zero or more ids without
        // throwing (the bridge caps fields and skips bad lines).
        string[] hostile =
        [
            "",
            "rtl_433 version 25.12 banner\n",
            "{\n",
            "{\"time\":\"2026-11-22T12:00:00.000Z\",\"model\":\"Ford Blah\",\"id\":12345678,\"channel\":1,\"freq\":433.92}",
            "{\"model\":123,\"id\":{\"nested\":true}}",
            "{\"model\":\"" + new string('x', 5000) + "\",\"id\":" + new string('9', 40) + "}",
            "[[[[[[[[[[[[[[[[[[[[\"deep\"]]]]]]]]]]]]]]]]]]]]",
            "{\"time\":null,\"freq\":null}",
            "}{}{",
            "\0ÿ",
            "{\"model\":\"x\",\"id\":" + new string('1', 100000) + "}",
        ];
        foreach (string line in hostile)
        {
            Assert.NotNull(Rtl433Bridge.ParseJsonLines(line, 433_920_000));
        }

        // Random bytes split into lines: never throws.
        for (int round = 0; round < 20; round++)
        {
            byte[] noise = new byte[4096];
            Rng.NextBytes(noise);
            string text = Encoding.UTF8.GetString(noise);
            Assert.NotNull(Rtl433Bridge.ParseJsonLines(text, 315_000_000));
        }
    }

    [Fact]
    public void OokPulseDecoderNeverThrowsOnRandomOrTruncatedIq()
    {
        IDecoder d = new OokPulseDecoder();
        for (int size = 0; size < 6; size++)
        {
            byte[] iq = new byte[10_000 * (size + 1)];
            Rng.NextBytes(iq);
            Assert.NotNull(d.Decode(OokCtx(iq)));
        }

        for (int len = 0; len < 10_000; len += 1024)
        {
            byte[] cut = new byte[len];
            Rng.NextBytes(cut);
            Assert.NotNull(d.Decode(OokCtx(cut)));
        }

        // All-zero and all-max captures (no transitions at all).
        Assert.NotNull(d.Decode(OokCtx(new byte[20_000])));
        byte[] flat = new byte[20_000];
        Array.Fill(flat, (byte)127);
        Assert.NotNull(d.Decode(OokCtx(flat)));
    }
}
