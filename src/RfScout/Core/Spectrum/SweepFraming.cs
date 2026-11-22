using System.Buffers.Binary;
using System.Numerics;

namespace RfScout.Core.Spectrum;

/// <summary>
/// The arithmetic of HackRF sweep mode, read from the v2026.01.3 sources rather than guessed:
///
///  * <c>firmware/hackrf_usb/usb_api_sweep.c</c> writes the block header as
///    <c>0x7F 0x7F</c> then a little-endian uint64 <c>sweep_freq</c>, and tunes the radio to
///    <c>sweep_freq + offset</c>. So the header is a *band edge*, not the tuned centre; with
///    <c>offset = 7.5 MHz</c> (what <c>hackrf_sweep</c> passes) the block's complex baseband
///    spans <c>header - 2.5 MHz .. header + 17.5 MHz</c>.
///  * In <c>INTERLEAVED</c> style <c>sweep_freq</c> walks S, S+0.75 MHz, S+1.0 MHz,
///    S+1.75 MHz, S+2.0 MHz ... (a 3/4 and 1/4 step alternating), so consecutive blocks step the
///    header by 250 kHz and every 1 MHz of span is covered by two blocks.
///  * <c>hackrf_sweep</c> keeps only the two FFT quarters below the centre and above it and
///    prints them as <c>[header, header + fs/4]</c> and <c>[header + fs/2, header + 3fs/4]</c>.
///    Those two quarters are the parts of the 20 MHz span that neither fold onto each other nor
///    land on the tuned centre's DC and images, so RfScout keeps the same two quarters, i.e.
///    <c>[header, header + 5 MHz]</c> and <c>[header + 10 MHz, header + 15 MHz]</c>.
///  * A block is 16384 bytes: the 10-byte header then 8187 interleaved s8 I/Q samples, which is
///    also why upstream caps its FFT at 8180 bins.
///
/// RfScout uses power-of-two FFT lengths so its own FFT can be radix-2. That changes the exact
/// bin width the user gets (20 MHz divided by a power of two) and lets one block be cut into
/// several windows when the requested bin width is finer than one MHz.
/// </summary>
public static class SweepMath
{
    public const int SampleRateHz = 20_000_000;
    public const int BytesPerBlock = 16_384;
    public const int HeaderBytes = 10;

    /// <summary>hackrf_sweep's tuning offset: the radio tunes to header + 7.5 MHz.</summary>
    public const long SweepOffsetHz = 7_500_000;

    /// <summary>Highest frequency a block header may carry (HackRF firmware limit, 7250 MHz).</summary>
    public const long MaxTunableHz = 7_250_000_000;

    /// <summary>Complex samples in one block after the 10-byte header: 8187.</summary>
    public const int SamplesPerBlock = (BytesPerBlock - HeaderBytes) / 2;

    /// <summary>Smallest FFT. 8 is the minimum because the kept quarters start at n/8.</summary>
    public const int MinFftLength = 8;

    /// <summary>Largest power of two that fits in one block (8187 samples).</summary>
    public const int MaxFftLength = 4096;

    /// <summary>Finest bin width: 20 MHz / 4096, rounded up.</summary>
    public const int MinBinWidthHz = 4_883;

    /// <summary>Coarsest bin width: 20 MHz / 8.</summary>
    public const int MaxBinWidthHz = 2_500_000;

    /// <summary>Bin width a given FFT length produces.</summary>
    public static double BinWidthHz(int fftLength) => (double)SampleRateHz / fftLength;

    /// <summary>Bins kept per window: two quarters = half the FFT.</summary>
    public static int BinsPerWindow(int fftLength) => fftLength / 2;

    /// <summary>Whole FFT windows that fit in one block's samples.</summary>
    public static int WindowsPerBlock(int fftLength) => Math.Max(1, SamplesPerBlock / fftLength);

    /// <summary>Round to a power of two and clamp to <see cref="MinFftLength"/>..<see cref="MaxFftLength"/>.</summary>
    public static int ClampFftLength(int fftLength)
    {
        uint n = BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, fftLength));
        return (int)Math.Clamp(n, (uint)MinFftLength, (uint)MaxFftLength);
    }

    /// <summary>Smallest power-of-two FFT whose bin width is at or below the request.</summary>
    public static int FftLengthForBinWidth(int binWidthHz)
    {
        int clamped = Math.Clamp(binWidthHz, MinBinWidthHz, MaxBinWidthHz);
        int needed = (int)Math.Ceiling((double)SampleRateHz / clamped);
        return ClampFftLength(needed);
    }

    /// <summary>
    /// Frequency of the lower edge of the first kept band, relative to the block header. It is the
    /// header itself: <c>hackrf_sweep</c> writes the band edges <c>[header, header + fs/4]</c> next
    /// to the first kept quarter and <c>[header + fs/2, header + 3fs/4]</c> next to the second, and
    /// the interleaved headers step so those bands tile the span. The first kept bin's centre is
    /// then on that edge - see <see cref="BinOffsetHz"/>.
    /// </summary>
    public const double FirstBinEdgeHz = 0;

    /// <summary>
    /// Centre frequency of kept bin <paramref name="index"/> relative to the block header value.
    ///
    /// This is the one place the radio's sweep format has to be right, so it is taken from
    /// <c>hackrf_sweep.c</c> line by line rather than reasoned about. A block is sampled while the
    /// radio is tuned to <c>header + offset</c> but the block itself only carries the bare header,
    /// so its FFT is centred on <c>header + SweepOffsetHz</c>. The tool keeps two quarters of that
    /// FFT and throws away the half around DC, where the tuned centre's own spike and the image
    /// land:
    ///
    ///  * raw bins <c>5N/8 .. 7N/8</c>, the negative frequencies, which it prints under the band
    ///    edges <c>[header, header + fs/4]</c>;
    ///  * raw bins <c>N/8 .. 3N/8</c>, the positive frequencies, printed under
    ///    <c>[header + fs/2, header + 3fs/4]</c>.
    ///
    /// <see cref="Dsp.FloatFft.PowerQuarters"/> returns exactly those two groups in that order, so
    /// the mapping is piecewise with a 5 MHz gap in the middle: bins <c>0 .. N/4-1</c> sit at
    /// <c>header + i * binWidth</c> and bins <c>N/4 .. N/2-1</c> at
    /// <c>header + fs/2 + (i - N/4) * binWidth</c>. Both groups land on whole bin steps above the
    /// header at any FFT length: a kept quarter is 3N/8 bins wide, and raw bin N/8 - the high
    /// group's first - is fs/8 above the tuned centre, which is exactly the printed fs/2 edge.
    ///
    /// Those are bin *centres*, not edges: a tone exactly at a bin's centre frequency puts all its
    /// power in that bin, which is what <c>FloatFftTests</c> pins down bin for bin against a naive
    /// DFT. Writing the first group as <c>(i + 0.5) * binWidth</c> instead - a natural mistake when
    /// reading "bin 0 covers 0 .. binWidth" - shifts the entire spectrum up by half a bin, which on
    /// a 625 kHz display grid is 9765 Hz and invisible until a marker is placed on a known carrier.
    ///
    /// Upstream prints the same rows starting one bin higher (<c>1 + 5N/8</c> and <c>1 + N/8</c>)
    /// because the bin on the quarter edge is the one next to DC, and its skirt is the tuned
    /// centre's own spike. RfScout keeps that bin, so it measures half a bin wider at each band edge
    /// than the tool's listing - coverage the interleaved headers overlap anyway.
    ///
    /// Get this wrong and the display is wrong in a way that looks like radio: treat the two
    /// quarters as one continuous ramp and every tenth hole in a 1-6000 MHz sweep appears to be a
    /// real emission gap.
    /// </summary>
    public static double BinOffsetHz(int fftLength, int index)
    {
        int bins = BinsPerWindow(fftLength);
        if (index < 0 || index >= bins)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Expected 0..{bins - 1}.");
        }

        int quarter = bins / 2;
        double binWidth = BinWidthHz(fftLength);

        // Bin centres, on the whole-bin-step grid the extraction actually reads. The low group starts
        // on the header; the high group's first bin is raw N/8, which is fs/8 above the tuned centre,
        // i.e. exactly on the printed fs/2 band edge (bins/8 is fs/8 in bin steps, and quarter +
        // fs/8 = fs/2).
        return index < quarter
            ? index * binWidth
            : ((SampleRateHz / 2.0) + ((index - quarter) * binWidth));
    }

    /// <summary>Tuned RF centre for a block, from its header value.</summary>
    public static long TunedCenterHz(long headerFrequencyHz) => headerFrequencyHz + SweepOffsetHz;

    /// <summary>
    /// The tune step the firmware is programmed with: one sample rate in Hz, which is what
    /// <c>hackrf_sweep.c</c> passes as <c>TUNE_STEP * FREQ_ONE_MHZ</c>. This is not a rounding
    /// detail: the interleaved sub-steps are a quarter and three quarters of it, so programming
    /// half the step width leaves a 1.25 MHz hole every 5 MHz of the span.
    /// </summary>
    public const long TuneStepHz = SampleRateHz;

    /// <summary>
    /// Header of the <paramref name="blockIndex"/>th block of a range, following the firmware's
    /// interleaved advance (<c>usb_api_sweep.c</c> lines 194-209): one dwell steps by a quarter of
    /// the tune step, the next by three quarters, alternating. Headers therefore run start,
    /// start + fs/8, start + fs, start + fs + fs/8, and so on.
    ///
    /// Each block measures two fs/4 bands with fs/4 missing between them, and the quarter-step
    /// block measures exactly the band the block before it dropped. That is what makes a span tile:
    /// every pair of blocks advances one tune step and measures exactly one tune step, so a pass
    /// over 1-6000 MHz is about 600 blocks and every display bin is filled.
    /// </summary>
    public static long InterleavedHeaderHz(long startHz, int blockIndex)
    {
        if (blockIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockIndex), blockIndex, "Block index must be zero or more.");
        }

        long pairs = blockIndex / 2;
        long withinPair = (blockIndex % 2) == 0 ? 0 : (TuneStepHz / 4);
        return startHz + (pairs * TuneStepHz) + withinPair;
    }
    /// <summary>Reads a 10-byte block header; false when the magic or the range is wrong.</summary>
    public static bool TryReadBlockHeader(ReadOnlySpan<byte> block, out long headerFrequencyHz)
    {
        headerFrequencyHz = 0;
        if (block.Length < HeaderBytes || block[0] != 0x7F || block[1] != 0x7F)
        {
            return false;
        }

        headerFrequencyHz = (long)BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(2, 8));
        return headerFrequencyHz is > 0 and <= MaxTunableHz;
    }

    /// <summary>
    /// The MHz start/stop pairs <c>hackrf_init_sweep</c> wants. The list is in whole MHz, so the
    /// request is snapped outward: a 88-108 MHz span becomes one range (88, 108) and the caller
    /// masks the extra bins the radio produces outside the requested span.
    /// </summary>
    public static IReadOnlyList<(int StartMHz, int StopMHz)> BuildSweepRanges(FrequencyRange range)
    {
        if (!range.IsValid)
        {
            throw new ArgumentException("Range must have a positive start below its stop.", nameof(range));
        }

        long maxMHz = MaxTunableHz / 1_000_000;
        int start = (int)Math.Clamp(range.StartHz / 1_000_000, 1, maxMHz);
        int stop = (int)Math.Clamp((long)Math.Ceiling(range.StopHz / 1_000_000.0), start + 1, maxMHz);
        return [(start, stop)];
    }
}

/// <summary>
/// Turns the byte stream from the RX callback into sweep blocks. Pure, so framing is unit tested
/// without hardware: it reassembles blocks that straddle a transfer boundary and counts blocks
/// with a bad header instead of dropping the whole transfer.
/// </summary>
public sealed class SweepBlockParser
{
    private readonly byte[] _leftover = new byte[SweepMath.BytesPerBlock];
    private int _leftoverBytes;
    private long _headerErrors;
    private long _blocks;

    /// <summary>Blocks whose header magic or frequency range was rejected.</summary>
    public long HeaderErrors => Interlocked.Read(ref _headerErrors);

    /// <summary>Blocks framed since construction or the last <see cref="Reset"/>.</summary>
    public long Blocks => Interlocked.Read(ref _blocks);

    /// <summary>Feed one RX callback buffer; <paramref name="emit"/> runs once per complete block.</summary>
    public void Feed(ReadOnlySpan<byte> data, Action<SweepBlock> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);

        // Put the tail of the previous callback back on the front so block boundaries that do not
        // line up with transfer boundaries still frame correctly.
        if (_leftoverBytes > 0)
        {
            int take = Math.Min(SweepMath.BytesPerBlock - _leftoverBytes, data.Length);
            data[..take].CopyTo(_leftover.AsSpan(_leftoverBytes));
            _leftoverBytes += take;
            data = data[take..];
            if (_leftoverBytes == SweepMath.BytesPerBlock)
            {
                Emit(_leftover, emit);
                _leftoverBytes = 0;
            }
        }

        while (data.Length >= SweepMath.BytesPerBlock)
        {
            Emit(data[..SweepMath.BytesPerBlock], emit);
            data = data[SweepMath.BytesPerBlock..];
        }

        if (!data.IsEmpty)
        {
            data.CopyTo(_leftover.AsSpan(_leftoverBytes));
            _leftoverBytes = data.Length;
        }
    }

    /// <summary>Forget any partial block and zero the counters (call when restarting a stream).</summary>
    public void Reset()
    {
        _leftoverBytes = 0;
        Interlocked.Exchange(ref _headerErrors, 0);
        Interlocked.Exchange(ref _blocks, 0);
    }

    private void Emit(ReadOnlySpan<byte> block, Action<SweepBlock> emit)
    {
        if (!SweepMath.TryReadBlockHeader(block, out long headerFrequency))
        {
            // Upstream skips the block and carries on. Do the same, but count it so the status bar
            // can show that the stream lost sync.
            Interlocked.Increment(ref _headerErrors);
            return;
        }

        Interlocked.Increment(ref _blocks);
        emit(new SweepBlock(headerFrequency, block[SweepMath.HeaderBytes..].ToArray()));
    }
}

