// Builds CRC-valid Mode S / ADS-B frames and their 2 Msps PPM envelope IQ for the M4 tests.
namespace RfScout.Tests;

internal static class ModeSFrameBuilder
{
    /// <summary>Mode S CRC-24 generator (0xFFF400), same polynomial the decoder checks.</summary>
    private const uint CrcPoly = 0xFFF400;

    /// <summary>
    /// Append the 24-bit CRC to a 112-bit payload (byte-per-bit). Mode S checks
    /// CRC(payload) == the received 24 bits, so the CRC field is the running register after the
    /// payload, and the decoder's plain running CRC over all 136 bits then lands on zero.
    /// </summary>
    internal static byte[] WithCrc(byte[] payloadBits112)
    {
        uint crc = 0;
        for (int i = 0; i < 112; i++)
        {
            uint bit = ((crc >> 23) & 1) ^ payloadBits112[i];
            crc = (crc << 1) & 0xFFFFFF;
            if (bit != 0)
            {
                crc ^= CrcPoly;
            }
        }

        byte[] frame = new byte[136];
        Array.Copy(payloadBits112, frame, 112);
        for (int i = 0; i < 24; i++)
        {
            frame[112 + i] = (byte)((crc >> (23 - i)) & 1);
        }

        return frame;
    }

    /// <summary>DF17 ME with TC=2 (callsign); the AFTN-packed callsign sits at bits 56-103.</summary>
    internal static byte[] BuildDf17Callsign(uint icao, string callsign)
    {
        byte[] bits = new byte[112];
        Write(bits, 0, 5, 17); // DF17
        Write(bits, 8, 24, (int)(icao & 0xFFFFFF));
        Write(bits, 32, 5, 2); // TC2 = identification
        Write(bits, 37, 3, 0); // surveillance ID
        Write(bits, 40, 8, 0); // watch/communications status
        Write(bits, 48, 8, 0); // mode code
        int pack = 0;
        foreach (char c in callsign.PadRight(8))
        {
            pack = (pack << 6) | AftnCode(c);
        }

        Write(bits, 56, 48, pack); // 8 x 6-bit AFTN characters
        return WithCrc(bits);
    }

    /// <summary>DF17 ME with TC=12 (airborne position, CPR even); lat/lon raw at bits 45..74.</summary>
    internal static byte[] BuildDf17Position(uint icao, double lat, double lon)
    {
        byte[] bits = new byte[112];
        Write(bits, 0, 5, 17);
        Write(bits, 8, 24, (int)(icao & 0xFFFFFF));
        Write(bits, 32, 5, 12); // TC12 airborne position (CPR)
        int latRaw = (int)Math.Round((lat + 90.0) * 32767.0 / 180.0) & 0x7FFF;
        int lonRaw = (int)Math.Round((lon + 180.0) * 65535.0 / 360.0) & 0x7FFF;
        Write(bits, 45, 15, latRaw);
        Write(bits, 60, 15, lonRaw);
        return WithCrc(bits);
    }

    /// <summary>
    /// Render a frame (byte-per-bit, 136 bits incl. CRC) as 2 Msps PPM envelope IQ bytes.
    /// The decoder times rising edges: preamble edges at 0..7 µs, then one edge per bit at
    /// +0.5 µs (bit 0) or +1.0 µs (bit 1) into each 1 µs bit period.
    /// </summary>
    internal static byte[] ToIq(byte[] frameBits, int rateHz)
    {
        // Upsample so pulses are several samples wide: at 2 samples/us a 0.25 us pulse is a
        // single sample, and the decoder's envelope threshold (30 % of the 99.9th percentile)
        // clips it away, so edges land on the wrong samples and no frame is ever found.
        rateHz = Math.Max(rateHz, 8_000_000);
        int us = rateHz / 1_000_000; // samples per µs (8 at 8 Msps)
        int pulseLen = Math.Max(1, us / 4); // 0.25 µs pulse
        int totalUs = 8 + frameBits.Length + 2;
        int samples = totalUs * us;
        // The decoder requires a capture of at least 4096 samples (a real 1090 MHz capture is
        // far longer); pad the rendered frame with trailing silence so a single-frame fixture
        // clears that floor.
        int padSamples = Math.Max(0, 4_096 - samples);
        samples += padSamples;
        byte[] iq = new byte[2 * samples];

        var edges = new List<double>();
        for (int k = 0; k < 8; k++)
        {
            edges.Add(k * 0.5); // PPM preamble: pulses at 0.5 µs spacing (0, 0.5, ... 3.5 µs)
        }

        double bitStart = 8.0; // each bit occupies [bitStart, bitStart+1) µs
        foreach (byte b in frameBits)
        {
            edges.Add(b == 1 ? bitStart + 1.0 : bitStart + 0.5);
            bitStart += 1.0;
        }

        foreach (double edgeUs in edges)
        {
            int start = (int)Math.Round(edgeUs * us);
            for (int k = 0; k < pulseLen && start + k < samples; k++)
            {
                iq[2 * (start + k)] = 100;
                iq[(2 * (start + k)) + 1] = 0;
            }
        }

        return iq;
    }

    private static void Write(byte[] bits, int offset, int len, int value)
    {
        for (int i = 0; i < len; i++)
        {
            bits[offset + i] = (byte)((value >> (len - 1 - i)) & 1);
        }
    }

    private static int AftnCode(char c) => c switch
    {
        ' ' => 0,
        >= '0' and <= '9' => c - '0' + 1,
        >= 'A' and <= 'Z' => c - 'A' + 11,
        _ => 0,
    };
}
