// ---------------------------------------------------------------------------
// rfscout_cuda.cu
//
// CUDA kernels for RfScout's narrowband decode path: the "analyze the frequency I
// selected" flow, where a 0.5..2 s I/Q capture of one peak is measured, classified
// and decoded. RfScout's managed DSP (src/RfScout/Core/Dsp, Core/Analysis) stays the
// reference implementation; this library is an optional accelerator that reproduces
// the same measurement arrays:
//
//   1. interleaved s8 I/Q -> separate float I and Q, scaled to [-1, 1)
//   2. DC removal (the block mean of I and Q, subtracted)
//   3. mean and peak power of the DC-removed capture
//   4. the envelope |x| (also the AM detector)
//   5. the instantaneous-frequency discriminator
//   6. an averaged Hann-windowed 4096-point periodogram, fftshift order
//
// Stage 5 is the cross-product discriminator,
// atan2(Im(x[n] conj(x[n-1])), Re(x[n] conj(x[n-1]))). That is algebraically the same
// wrapped phase step the CPU path reaches through atan2 + unwrap + difference, so the
// two agree to float rounding and no sequential phase unwrap is needed on the GPU.
//
// The FFT is the same decimation-in-frequency, radix-2 transform as the managed
// FloatFft, and like it, leaves its output in bit-reversed order: the power scatter
// folds the reversal into the bin index, so there is no permutation pass here either.
//
// Rounding is matched to the managed path where it is cheap to do so: the s8 -> float
// conversion, the DC-mean subtraction and the Hann window are evaluated in double and
// rounded once, the butterflies and the power sums accumulate in float, and the
// twiddles come from a double sincos rounded to float (the managed path reads a float
// twiddle table). The residual differences are float rounding, which is what the
// parity tests pin.
//
// Nothing here transmits, and nothing here decides a modulation mode - the classifier
// stays managed code, so every label keeps the measurements that produced it.
//
// Build: native\cuda\build-cuda.ps1 (nvcc + MSVC, static CUDA runtime, so the DLL
// needs only nvcuda.dll from the NVIDIA driver).
// ---------------------------------------------------------------------------

#include <cuda_runtime.h>
#include <device_launch_parameters.h>

#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

// M_PI is not portable: MSVC hides it without _USE_MATH_DEFINES, and the CUDA math
// header does not always provide it either.
#define RS_PI 3.14159265358979323846

// ---------------------------------------------------------------------------
// Public surface. Plain C, int32_t/int64_t only, natural packing, no name mangling:
// the C# side declares the same layout with [StructLayout(LayoutKind.Sequential)].
// ---------------------------------------------------------------------------

#define RS_API extern "C" __declspec(dllexport)

#define RS_OK 0
#define RS_ERR_NO_DEVICE (-1)      // no CUDA device at all
#define RS_ERR_UNSUPPORTED (-2)    // a device exists but is below the minimum CC
#define RS_ERR_BAD_ARG (-3)        // bad pointer, length or FFT length
#define RS_ERR_CUDA (-4)           // a CUDA call failed; see rs_cuda_last_error
#define RS_ERR_MEMORY (-5)         // could not size the device scratch

#define RS_NAME_LEN 128
#define RS_MIN_MAJOR 5             // Maxwell and newer: enough shared memory for 4096-point FFTs
#define RS_THREADS 256             // block size used by every launch here
#define RS_MAX_FFT 8192            // largest FFT length the host-side staging arrays hold;
                                   // the managed app uses 4096 (FeatureExtractor.AnalysisFftLength)

// Device description returned by rs_cuda_probe. Field order puts the 64-bit values
// first so neither side needs padding games.
typedef struct {
    int64_t total_bytes;
    int64_t free_bytes;
    int64_t max_threads_per_block;
    int64_t max_shared_per_block;
    int32_t struct_size;           // caller fills in with sizeof(RS_GPU_INFO)
    int32_t device_count;
    int32_t device;                // index of the device chosen
    int32_t major;
    int32_t minor;                 // compute capability major.minor
    int32_t multiprocessor_count;
    int32_t rt_version;            // CUDA runtime version
    int32_t drv_version;           // driver's CUDA version, e.g. 12090
    int32_t warp_size;
    int32_t l2_cache_bytes;
    int32_t concurrent_kernels;
    int32_t reserved;
    char name[RS_NAME_LEN];
} RS_GPU_INFO;

// What one analyze produced. The arrays are sized by the caller; stats holds the two
// block reductions the managed path otherwise takes itself.
typedef struct {
    int64_t sample_count;
    int64_t power_count;
    double mean_re;                // of the raw capture, before subtraction
    double mean_im;
    double mean_power;             // of the DC-removed capture; 0.5 = full-scale complex sine
    double peak_power;             // likewise
    double sample_rate_hz;
    double reserved0;
} RS_STATS;

// ---------------------------------------------------------------------------
// Internals: one cached device scratch arena, resized on demand.
// ---------------------------------------------------------------------------

namespace {

struct Arena {
    int8_t* iq = nullptr;   // the capture as libhackrf delivered it, copied up to the device:
                            // the kernels read this and never the caller's buffer, which is
                            // host memory the GPU cannot address on its own
    float* re = nullptr;    // the DC-removed capture
    float* im = nullptr;
    float* env = nullptr;
    float* freq = nullptr;
    float* power = nullptr;  // accumulated periodogram, fftshift order
    float* window = nullptr; // Hann taps
    double* sums = nullptr;  // {sumRe, sumIm} of the raw capture
    float* twiddles = nullptr;   // n/2 float2, the concatenated per-stage twiddle table
    int* tw_offsets = nullptr;   // where each stage starts in that table
    double* stats = nullptr; // {sumP2, maxP} of the DC-removed capture
    float* naive = nullptr;  // second periodogram accumulator, fft floats: the slow
                             // double-sincos reference in rs_cuda_fft_debug writes it so
                             // k_spectra's fast path can be compared against it
    int64_t capacity = 0;    // samples the capture-sized buffers hold
    int32_t fft = 0;         // FFT length the power/window buffers hold
    bool ready = false;
};

Arena g_arena;
cudaStream_t g_stream = nullptr;
char g_error[512] = { 0 };
int g_last_status = RS_OK;
int g_device = -1;

void set_error(const char* what, cudaError_t err)
{
    snprintf(g_error, sizeof(g_error), "%s: %s (%d)", what,
             cudaGetErrorString(err), static_cast<int>(err));
}

void arena_release()
{
    cudaFree(g_arena.iq);
    cudaFree(g_arena.re);
    cudaFree(g_arena.im);
    cudaFree(g_arena.env);
    cudaFree(g_arena.freq);
    cudaFree(g_arena.power);
    cudaFree(g_arena.naive);
    cudaFree(g_arena.window);
    cudaFree(g_arena.sums);
    cudaFree(g_arena.stats);
    cudaFree(g_arena.twiddles);
    cudaFree(g_arena.tw_offsets);
    g_arena = Arena();
}

// Size the scratch buffers. capacity_samples must be a multiple of fft so the
// periodogram's window grid is exact and needs no tail handling.
int arena_reserve(int64_t capacity_samples, int32_t fft)
{
    if (capacity_samples <= 0 || fft < 2 || (fft & (fft - 1)) != 0) {
        g_last_status = RS_ERR_BAD_ARG;
        snprintf(g_error, sizeof(g_error), "bad arena request: %lld samples, fft %d",
                 static_cast<long long>(capacity_samples), fft);
        return RS_ERR_BAD_ARG;
    }

    if (g_arena.ready && g_arena.capacity >= capacity_samples && g_arena.fft >= fft) {
        return RS_OK;
    }

    if (g_arena.ready) {
        arena_release();
    }

    const size_t bytes = static_cast<size_t>(capacity_samples) * sizeof(float);
    cudaError_t e = cudaMalloc(&g_arena.iq, static_cast<size_t>(capacity_samples) * 2);
    if (e == cudaSuccess) e = cudaMalloc(&g_arena.re, bytes);
    if (e == cudaSuccess) e = cudaMalloc(&g_arena.im, bytes);
    if (e == cudaSuccess) e = cudaMalloc(&g_arena.env, bytes);
    if (e == cudaSuccess) e = cudaMalloc(&g_arena.freq, bytes);
    if (e == cudaSuccess) e = cudaMalloc(&g_arena.power, sizeof(float) * fft);
    if (e == cudaSuccess) e = cudaMalloc(&g_arena.naive, sizeof(float) * fft);
    if (e == cudaSuccess) e = cudaMalloc(&g_arena.window, sizeof(float) * fft);
    if (e == cudaSuccess) e = cudaMalloc(&g_arena.sums, 2 * sizeof(double));
    if (e == cudaSuccess) e = cudaMalloc(&g_arena.stats, 2 * sizeof(double));
    // The table is fft/2 float2 entries (n/2 complex twiddles), so it needs 8 bytes per
    // entry: sizeof(float2) * (fft/2) == sizeof(float) * fft. Allocating sizeof(float) * fft
    // (16 KB at n=4096) instead of 32 KB makes k_spectra's upper stages read 16 KB past the
    // buffer: compute-sanitizer reports an invalid 8-byte __global__ read just past the
    // allocation and the launch dies with CUDA error 700 (illegal memory access).
    if (e == cudaSuccess) e = cudaMalloc(&g_arena.twiddles, sizeof(float2) * static_cast<size_t>(fft > 1 ? fft - 1 : 1));
    // 32 stage offsets: enough for any power-of-two length up to 2^31, and cheaper to
    // over-allocate than to compute log2 on the host (the MSVC host compiler has no
    // __builtin_clz, and nvcc here builds host code with MSVC).
    if (e == cudaSuccess) e = cudaMalloc(&g_arena.tw_offsets, sizeof(int) * 32);

    if (e != cudaSuccess) {
        set_error("cudaMalloc scratch", e);
        arena_release();
        g_last_status = RS_ERR_MEMORY;
        return RS_ERR_MEMORY;
    }

    g_arena.capacity = capacity_samples;
    g_arena.fft = fft;
    g_arena.ready = true;
    return RS_OK;
}

int ensure_stream()
{
    if (g_stream != nullptr) {
        return RS_OK;
    }

    const cudaError_t e = cudaStreamCreate(&g_stream);
    if (e != cudaSuccess) {
        set_error("cudaStreamCreate", e);
        g_last_status = RS_ERR_CUDA;
        return RS_ERR_CUDA;
    }

    return RS_OK;
}

// A grid-stride loop never needs more blocks than this.
inline int grid_for(int64_t items)
{
    const int64_t blocks = (items + RS_THREADS - 1) / RS_THREADS;
    return static_cast<int>(blocks < 1 ? 1 : (blocks > 65535 ? 65535 : blocks));
}

// Maxwell (sm_52) has no double atomicAdd - that arrived with sm_60 - so the block
// reductions fold into the global slots through a compare-and-swap loop instead.
// atomicCAS on 64 bits is available on every architecture CUDA still builds for.
__device__ __forceinline__ void atomic_add_double(double* address, double value)
{
    unsigned long long* as_bits = reinterpret_cast<unsigned long long*>(address);
    unsigned long long old = *as_bits;
    unsigned long long assumed;
    do {
        assumed = old;
        old = atomicCAS(as_bits, assumed, __double_as_longlong(__longlong_as_double(assumed) + value));
    } while (assumed != old);
}

// Running maximum of a non-negative quantity (the slots are memset to 0 before each
// launch, so a capture that never exceeds 0.0 keeps the 0 it started with).
__device__ __forceinline__ void atomic_max_double(double* address, double value)
{
    unsigned long long* as_bits = reinterpret_cast<unsigned long long*>(address);
    unsigned long long old = *as_bits;
    unsigned long long assumed;
    do {
        assumed = old;
        if (__longlong_as_double(assumed) >= value) {
            return;
        }

        old = atomicCAS(as_bits, assumed, __double_as_longlong(value));
    } while (assumed != old);
}

// ---------------------------------------------------------------------------
// Pass 1: the DC estimate. sum(I) and sum(Q) of the raw capture, in float, because
// the managed path adds the same floats into a double accumulator and the difference
// between the two orders is well inside the parity tolerance.
// ---------------------------------------------------------------------------
__global__ void k_dc_partials(const int8_t* __restrict__ iq,
                              int64_t samples,
                              double* __restrict__ out)
{
    const int64_t stride = static_cast<int64_t>(gridDim.x) * RS_THREADS;

    float sre = 0.0f, sim = 0.0f;
    for (int64_t i = static_cast<int64_t>(blockIdx.x) * RS_THREADS + threadIdx.x;
         i < samples;
         i += stride) {
        sre += static_cast<float>(iq[2 * i]) * (1.0f / 128.0f);
        sim += static_cast<float>(iq[2 * i + 1]) * (1.0f / 128.0f);
    }

    // Dynamic for the same reason as k_convert: a static array here would share the pool
    // with another kernel's dynamic request.
    extern __shared__ float smf[];  // two rows of RS_THREADS, sized by the launcher
    const int t = threadIdx.x;
    smf[t] = sre;
    smf[RS_THREADS + t] = sim;
    __syncthreads();

    for (int off = RS_THREADS >> 1; off > 0; off >>= 1) {
        if (t < off) {
            smf[t] += smf[t + off];
            smf[RS_THREADS + t] += smf[RS_THREADS + t + off];
        }
        __syncthreads();
    }

    if (t == 0) {
        atomic_add_double(&out[0], static_cast<double>(smf[0]));
        atomic_add_double(&out[1], static_cast<double>(smf[RS_THREADS]));
    }
}

// ---------------------------------------------------------------------------
// Pass 2: convert, DC removal, envelope, discriminator, and the power reductions.
//
// The DC mean is rounded to float before it is subtracted, exactly as the managed
// path does (float meanRe = (float)Mean(re); re[i] -= meanRe), so a capture with a
// large centre spike loses the same amount in both implementations.
// ---------------------------------------------------------------------------
__global__ void k_convert(const int8_t* __restrict__ iq,
                          int64_t samples,
                          const double* __restrict__ sums,
                          float* __restrict__ re,
                          float* __restrict__ im,
                          float* __restrict__ env,
                          float* __restrict__ freq,
                          double* __restrict__ stats,
                          double hz_per_rad)
{
    const int64_t stride = static_cast<int64_t>(gridDim.x) * RS_THREADS;
    const double n = static_cast<double>(samples);
    const float mean_re = static_cast<float>(sums[0] / n);
    const float mean_im = static_cast<float>(sums[1] / n);

    float sp = 0.0f, mp = 0.0f;
    for (int64_t i = static_cast<int64_t>(blockIdx.x) * RS_THREADS + threadIdx.x;
         i < samples;
         i += stride) {
        const float r = static_cast<float>(static_cast<double>(static_cast<signed char>(iq[2 * i])) *
                                           (1.0 / 128.0) - static_cast<double>(mean_re));
        const float q = static_cast<float>(static_cast<double>(static_cast<signed char>(iq[2 * i + 1])) *
                                               (1.0 / 128.0) - static_cast<double>(mean_im));
        re[i] = r;
        im[i] = q;

        const double pr = r * r + q * q;
        sp += static_cast<float>(pr);
        if (pr > static_cast<double>(mp)) {
            mp = static_cast<float>(pr);
        }

        env[i] = static_cast<float>(sqrt(static_cast<double>(r) * static_cast<double>(r) +
                                         static_cast<double>(q) * static_cast<double>(q)));

        if (i == 0) {
            // The managed discriminator copies sample 1 into sample 0 so the array has
            // no hole at the front; this kernel cannot read what it has not written, so
            // the host patches sample 0 after the launch.
            freq[0] = 0.0f;
            continue;
        }

        const float pfr = static_cast<float>(static_cast<double>(static_cast<signed char>(iq[2 * i - 2])) *
                                                 (1.0 / 128.0) - static_cast<double>(mean_re));
        const float pfq = static_cast<float>(static_cast<double>(static_cast<signed char>(iq[2 * i - 1])) *
                                                 (1.0 / 128.0) - static_cast<double>(mean_im));
        const double cross = static_cast<double>(q) * static_cast<double>(pfr) -
                             static_cast<double>(r) * static_cast<double>(pfq);
        const double dot = static_cast<double>(r) * static_cast<double>(pfr) +
                           static_cast<double>(q) * static_cast<double>(pfq);
        freq[i] = static_cast<float>(atan2(cross, dot) * hz_per_rad);
    }

    // Block reduction of the two power statistics, then one atomic each. The scratch is
    // dynamic, not static, and the launcher sizes it: a kernel's static shared and a peer
    // kernel's dynamic request share one pool on this driver, and a kernel that also
    // declares static shared ends up with the neighbour's block landing on top of it -
    // compute-sanitizer saw exactly that, as out-of-range __shared__ reads 512 bytes past
    // a 32 KB allocation. Keeping every block reduction on the dynamic side of that line
    // removes the question.
    extern __shared__ double smd[];  // two rows of RS_THREADS, sized by the launcher
    const int t = threadIdx.x;
    smd[t] = static_cast<double>(sp);
    smd[RS_THREADS + t] = static_cast<double>(mp);
    __syncthreads();

    // The off == 1 step reads smd[t + off] with t == off - 1, i.e. index RS_THREADS:
    // the last butterfly pair straddles the row boundary, so the launcher must hand out
    // the full 2 * sizeof(double) * RS_THREADS (4 KB). It asked for half that
    // (sizeof(double) * RS_THREADS), and every block's final step read 8 bytes past the
    // allocation - compute-sanitizer attributed the fault to the next kernel launch
    // (k_spectra) and the launch died with CUDA error 700.
    for (int off = RS_THREADS >> 1; off > 0; off >>= 1) {
        if (t < off) {
            smd[t] += smd[t + off];
            if (smd[RS_THREADS + t + off] > smd[RS_THREADS + t]) {
                smd[RS_THREADS + t] = smd[RS_THREADS + t + off];
            }
        }
        __syncthreads();
    }

    if (t == 0) {
        atomic_add_double(&stats[0], smd[0]);
        atomic_max_double(&stats[1], smd[RS_THREADS]);
    }
}

// ---------------------------------------------------------------------------
// Pass 3: the averaged periodogram.
// ---------------------------------------------------------------------------

__device__ __forceinline__ int rs_reverse_bits(int v, int levels)
{
    unsigned int x = static_cast<unsigned int>(v);
    x = ((x >> 1) & 0x55555555u) | ((x & 0x55555555u) << 1);
    x = ((x >> 2) & 0x33333333u) | ((x & 0x33333333u) << 2);
    x = ((x >> 4) & 0x0f0f0f0fu) | ((x & 0x0f0f0f0fu) << 4);
    x = ((x >> 8) & 0x00ff00ffu) | ((x & 0x00ff00ffu) << 8);
    x = (x >> 16) | (x << 16);
    return static_cast<int>(x >> (32 - levels));
}

// Divide the accumulated Welch periodogram into the mean spectrum the managed contract asks
// for (CaptureMeasurements.Power: the mean, with PowerWindows counting the windows folded
// into it). The CPU reference accumulates every window into one accumulator and divides
// once at the end (CaptureMeasurements.Cpu); the GPU path reaches the same mean here,
// because the per-window atomics cannot be divided mid-flight.
__global__ void k_scale_power(float* __restrict__ power, int n, float scale)
{
    const int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) {
        power[i] *= scale;
    }
}

// Diagnostic-only reference periodogram: one block per Welch window, same windowed inputs
// as k_spectra, but the transform is the slow obvious shape - double sincos per butterfly,
// no twiddle table, no shared-memory staging tricks. Whatever k_spectra's fast path gets
// wrong (pair index, twiddle slice, scatter order) shows up as a difference between the two
// power accumulators. Not a production path; rs_cuda_fft_debug launches it.
__global__ void k_naive_periodogram(const float* __restrict__ re,
                                    const float* __restrict__ im,
                                    const float* __restrict__ window,
                                    float* __restrict__ power_acc,
                                    int64_t samples,
                                    int n,
                                    int step)
{
    extern __shared__ float2 sdata[];

    const int64_t base = static_cast<int64_t>(blockIdx.x) * step;
    if (base + n > samples) {
        return;  // same tail rule as k_spectra, and for the same reason
    }

    const int tid = threadIdx.x;
    const int levels = __ffs(n) - 1;
    const int half_n = n >> 1;

    for (int i = tid; i < n; i += RS_THREADS) {
        const float w = window[i];
        sdata[i].x = re[base + i] * w;
        sdata[i].y = im[base + i] * w;
    }
    __syncthreads();

    for (int level = 0; level < levels; level++) {
        __syncthreads();
        const int half = n >> (level + 1);
        for (int k = tid; k < half_n; k += RS_THREADS) {
            const int p = (k / half) * (half << 1) + (k % half);
            const int q = p + half;
            const float2 a = sdata[p];
            const float2 b = sdata[q];
            const float dr = a.x - b.x;
            const float di = a.y - b.y;
            const double angle = -2.0 * RS_PI * static_cast<double>(k % half) /
                                 static_cast<double>(half << 1);
            const float wr = static_cast<float>(cos(angle));
            const float wi = static_cast<float>(sin(angle));
            sdata[p].x = a.x + b.x;
            sdata[p].y = a.y + b.y;
            sdata[q].x = dr * wr - di * wi;
            sdata[q].y = dr * wi + di * wr;
        }
    }
    __syncthreads();

    const int quarter = n >> 2;
    for (int j = tid; j < half_n; j += RS_THREADS) {
        const int r = rs_reverse_bits((j + 3 * quarter) & (n - 1), levels);
        const float sr = sdata[r].x;
        const float si = sdata[r].y;
        atomicAdd(&power_acc[j], sr * sr + si * si);
    }
}

// Hann taps: symmetric, evaluated in double and rounded once, as WindowFunctions.Hann.
__global__ void k_hann(float* __restrict__ window, int n)
{
    const int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) {
        const double a = 2.0 * RS_PI * static_cast<double>(i) / static_cast<double>(n - 1);
        window[i] = static_cast<float>(0.5 - 0.5 * cos(a));
    }
}

// The twiddle table, in the same concatenated per-stage order the managed FloatFft builds:
// stage `level` needs half = n >> (level + 1) entries, the stages run one after the other,
// and so the whole table is n/2 float2. offsets[level] says where a stage starts and
// offsets[levels] is the table length.
//
// It exists so the butterfly loop is a table read instead of a double sincos. Double math
// there costs 1/32 rate on Maxwell, and it mattered for a second reason too: it pushed this
// kernel past 48 registers per thread, and a kernel that spills to local memory while also
// holding a 32 KB dynamic shared block faults on this driver.
//
// One block, and it writes the offset table itself before reading it: with the table spread
// across blocks, a block that read an offset another block had not yet written would index
// the table with uninitialised memory, and the kernel after it would read at a wild offset.
__global__ void k_twiddles(float2* __restrict__ twiddles,
                           int* __restrict__ offsets,
                           int n)
{
    const int levels = __ffs(n) - 1;

    if (threadIdx.x == 0) {
        int pos = 0;
        for (int level = 0; level < levels; level++) {
            offsets[level] = pos;
            pos += n >> (level + 1);
        }
        offsets[levels] = pos;
    }
    __syncthreads();

    for (int k = threadIdx.x; k < (n - 1); k += RS_THREADS) {
        // Which stage does this entry belong to: stage s covers [offsets[s], offsets[s+1]).
        int level = 0;
        while (level < levels && offsets[level + 1] <= k) {
            level++;
        }

        const int within = k - offsets[level];
        const int half = n >> (level + 1);
        const double angle = -2.0 * RS_PI * static_cast<double>(within) /
                             static_cast<double>(2 * half);
        twiddles[k].x = static_cast<float>(cos(angle));
        twiddles[k].y = static_cast<float>(sin(angle));
    }
}

// One block per FFT window: radix-2 decimation-in-frequency in shared memory, then a
// scatter of the middle half into the fftshift-ordered accumulator. Bit reversal is
// folded into the scatter, exactly like FloatFft.PowerShifted.
//
// Shared memory holds n float2, so this needs 32 KB at n = 4096; the caller refuses the
// launch outright when the device cannot hold it. The launch must hand the kernel the
// full 32 KB: the old launcher asked for sizeof(float) * fft_size (16 KB), so every
// read of sdata[2048..4095] - the upper half of the butterflies and the whole scatter -
// fell outside the dynamic block. compute-sanitizer reports it as "Invalid __shared__
// read of size 8 bytes ... out of bounds" at 0xa000 and up (0xa000 is exactly
// 4096 * 8, one float2 past a 4096-entry array), and the launch dies with CUDA error
// 700 (illegal memory access), which is why the periodogram came back empty (OBW 0 kHz,
// flatness -Infinity) while every other measurement stayed correct.
//
// The butterflies run straight through the shared array, exactly like the managed
// FloatFft: stage s splits every block of 2*half (half = n >> (s+1)) into p and q = p +
// half, and the twiddle for butterfly k of that block is table entry offsets[s] + (k &
// (half - 1)), because the per-stage table repeats once per block. The earlier shape -
// one block per (window, stage) with a permuted p - looked right against the CPU's
// bit-reversed output but was not: it read past each stage's half-sized slice of the
// twiddle table (compute-sanitizer: invalid 8-byte __global__ read just past the arena,
// CUDA error 700) and left the spectrum in the wrong order (the periodogram came back
// flat, OBW 991 kHz against the CPU's 188 kHz).
__global__ void k_spectra(const float* __restrict__ re,
                          const float* __restrict__ im,
                          const float* __restrict__ window,
                          const float2* __restrict__ twiddles,
                          const int* __restrict__ tw_offsets,
                          float* __restrict__ power_acc,
                          int64_t samples,
                          int n,
                          int step)
{
    extern __shared__ float2 sdata[];

    const int64_t base = static_cast<int64_t>(blockIdx.x) * step;
    if (base + n > samples) {
        // Nothing to do: this block's window runs off the end of the capture. A plain
        // return is safe here because no __syncthreads() follows - every barrier in this
        // kernel sits inside a loop every thread of a live block runs to completion. The
        // doubled barrier this replaces was left over from an earlier shape and, once the
        // launch was narrowed so the grid never overruns the capture, it was dead code;
        // a barrier a diverged thread skips while peers wait on it would hang, so this
        // early exit must never carry one.
        return;
    }

    const int tid = threadIdx.x;
    const int levels = __ffs(n) - 1;
    const int half_n = n >> 1;

    for (int i = tid; i < n; i += RS_THREADS) {
        const float w = window[i];
        sdata[i].x = re[base + i] * w;
        sdata[i].y = im[base + i] * w;
    }
    __syncthreads();

    // Stage barrier at the TOP of each stage, not only at the bottom: the butterflies of
    // one stage write the elements the next stage reads, so a thread that finishes its
    // share early must not read a partner element another thread has not written yet.
    // The earlier shape had the barrier only at the end of the loop body, so stage 1 read
    // stage 0's half-written shared array. compute-sanitizer reports it as "Invalid
    // __shared__ read of size 8 bytes ... out of bounds" in k_spectra - the race also
    // lets a thread run ahead and recompute a later stage's index, which is why the fault
    // address lands past the 32 KB allocation - and the periodogram came back as garbage
    // (OBW 0 kHz, flatness -Infinity) while every other measurement stayed correct.
    for (int level = 0; level < levels; level++) {
        __syncthreads();
        const int half = n >> (level + 1);
        const int tw = n - (half << 1);
        // The per-stage table is contiguous and stage s starts at offsets[s] with
        // exactly half = n >> (s+1) entries, so the stage base is the running sum of the
        // halves, 0 + n/2 + n/4 + ... = n - 2*half. tw_offsets[level] holds exactly that,
        // and the copy above stages the slice in shared memory.
        // half_n butterflies per transform, one block of the FFT per butterfly pair.
        // p enumerates the butterflies the same way the managed FloatFft does: k walks
        // the blocks of 2*half in order, and within a block the low half of the pair.
        // That is p = (k / half) * (2*half) + (k % half), so p and q = p + half are both
        // in [0, n) for every stage. The shift form this replaces, ((k >> level) <<
        // (level + 1)) | (k & mask), only agrees with it at one stage: at the others it
        // pairs the wrong elements and pushes q past n, which is the "Invalid __shared__
        // read of size 8 bytes ... out of bounds" at 0xa000 and up that compute-sanitizer
        // reported, and it left the spectrum scrambled (OBW 0 kHz, flatness -Infinity).
        for (int k = tid; k < half_n; k += RS_THREADS) {
            const int p = (k / half) * (half << 1) + (k % half);
            const int q = p + half;
            const float2 a = sdata[p];
            const float2 b = sdata[q];
            const float dr = a.x - b.x;
            const float di = a.y - b.y;
            // The table repeats once per block of the FFT, so butterfly k takes entry
            // k % half of the stage's slice - the within-block offset the managed
            // FloatFft reads with its contiguous per-stage table.
            const float2 w = twiddles[tw + (k % half)];
            sdata[p].x = a.x + b.x;
            sdata[p].y = a.y + b.y;
            sdata[q].x = dr * w.x - di * w.y;
            sdata[q].y = dr * w.y + di * w.x;
        }
        __syncthreads();
    }

    const int quarter = n >> 2;
    for (int j = tid; j < half_n; j += RS_THREADS) {
        // The shift is masked to n-1, so the source index r is always in [0, n): the
        // scatter can never read past sdata[n-1] as long as the launch hands this kernel
        // the full n * sizeof(float2) dynamic block. The fault compute-sanitizer reported
        // here (an 8-byte __shared__ read at 0xa000 and up, one float2 past a 4096-entry
        // array, stride 8 * RS_THREADS per loop step) was that block being half the size
        // the kernel indexes, not an index bug in this loop.
        const int r = rs_reverse_bits((j + 3 * quarter) & (n - 1), levels);
        const float sr = sdata[r].x;
        const float si = sdata[r].y;
        atomicAdd(&power_acc[j], sr * sr + si * si);
    }
}

}  // namespace

// ---------------------------------------------------------------------------
// Exported entry points.
// ---------------------------------------------------------------------------

RS_API int32_t rs_cuda_probe(RS_GPU_INFO* info)
{
    if (info == nullptr || info->struct_size != static_cast<int32_t>(sizeof(RS_GPU_INFO))) {
        g_last_status = RS_ERR_BAD_ARG;
        snprintf(g_error, sizeof(g_error), "bad RS_GPU_INFO pointer or struct_size");
        return RS_ERR_BAD_ARG;
    }

    const int32_t wanted_size = info->struct_size;
    memset(info, 0, sizeof(RS_GPU_INFO));
    info->struct_size = wanted_size;

    int count = 0;
    const cudaError_t e = cudaGetDeviceCount(&count);
    if (e != cudaSuccess || count <= 0) {
        if (e != cudaSuccess) {
            set_error("cudaGetDeviceCount", e);
        } else {
            snprintf(g_error, sizeof(g_error), "no CUDA device found");
        }

        info->device_count = count;
        g_last_status = RS_ERR_NO_DEVICE;
        return RS_ERR_NO_DEVICE;
    }

    info->device_count = count;

    // Prefer the most capable device; break ties toward the discrete one.
    int best = -1;
    cudaDeviceProp best_prop{};
    int best_score = -1;
    for (int i = 0; i < count; i++) {
        cudaDeviceProp prop{};
        if (cudaGetDeviceProperties(&prop, i) != cudaSuccess) {
            continue;
        }

        const int score = prop.major * 100 + prop.minor * 10 + (prop.integrated ? 0 : 5);
        if (score > best_score) {
            best = i;
            best_prop = prop;
            best_score = score;
        }
    }

    if (best < 0) {
        g_last_status = RS_ERR_NO_DEVICE;
        snprintf(g_error, sizeof(g_error), "cudaGetDeviceProperties failed for every device");
        return RS_ERR_NO_DEVICE;
    }

    if (best_prop.major < RS_MIN_MAJOR) {
        snprintf(g_error, sizeof(g_error),
                 "%s is compute capability %d.%d; RfScout needs %d.0 or newer",
                 best_prop.name, best_prop.major, best_prop.minor, RS_MIN_MAJOR);
        g_last_status = RS_ERR_UNSUPPORTED;
        return RS_ERR_UNSUPPORTED;
    }

    if (cudaSetDevice(best) != cudaSuccess) {
        set_error("cudaSetDevice", cudaGetLastError());
        g_last_status = RS_ERR_CUDA;
        return RS_ERR_CUDA;
    }

    g_device = best;

    info->device = best;
    info->major = best_prop.major;
    info->minor = best_prop.minor;
    info->multiprocessor_count = best_prop.multiProcessorCount;
    info->max_threads_per_block = best_prop.maxThreadsPerBlock;
    info->max_shared_per_block = best_prop.sharedMemPerBlock;
    info->warp_size = best_prop.warpSize;
    info->l2_cache_bytes = best_prop.l2CacheSize;
    info->concurrent_kernels = best_prop.concurrentKernels;
    strncpy(info->name, best_prop.name, RS_NAME_LEN - 1);

    size_t free_bytes = 0, total_bytes = 0;
    if (cudaMemGetInfo(&free_bytes, &total_bytes) == cudaSuccess) {
        info->free_bytes = static_cast<int64_t>(free_bytes);
        info->total_bytes = static_cast<int64_t>(total_bytes);
    }

    int rt = 0, drv = 0;
    cudaRuntimeGetVersion(&rt);
    cudaDriverGetVersion(&drv);
    info->rt_version = rt;
    info->drv_version = drv;

    g_error[0] = '\0';
    g_last_status = RS_OK;
    return RS_OK;
}

RS_API void rs_cuda_last_error(char* buffer, int32_t buffer_length)
{
    if (buffer == nullptr || buffer_length <= 0) {
        return;
    }

    buffer[0] = '\0';
    strncat(buffer, g_error, static_cast<size_t>(buffer_length) - 1);
}

RS_API int32_t rs_cuda_last_status(void)
{
    return g_last_status;
}

// Release the scratch arena and the stream. Safe to call any number of times; the
// next analyze rebuilds them.
RS_API void rs_cuda_destroy(void)
{
    if (g_arena.ready) {
        arena_release();
    }

    if (g_stream != nullptr) {
        cudaStreamDestroy(g_stream);
        g_stream = nullptr;
    }

    cudaDeviceReset();
    g_device = -1;
}

// Diagnostic-only, for the GPU self-test: run the production periodogram pipeline and a
// plain double-precision reference transform of the same windowed capture, and report the
// largest relative difference between the two power spectra (Welch-averaged, fftshift
// order). A wrong butterfly, twiddle or scatter shows up here long before the managed
// feature tolerances notice. Not a production path.
RS_API int32_t rs_cuda_fft_debug(const int8_t* iq,
                                 int64_t sample_count,
                                 double sample_rate_hz,
                                 int32_t fft_size,
                                 double* out_worst_rel_power)
{
    if (iq == nullptr || out_worst_rel_power == nullptr) {
        g_last_status = RS_ERR_BAD_ARG;
        return RS_ERR_BAD_ARG;
    }
    if (sample_count < 64 || sample_rate_hz <= 0 ||
        fft_size < 2 || fft_size > RS_MAX_FFT || (fft_size & (fft_size - 1)) != 0 ||
        (sample_count % fft_size) != 0) {
        g_last_status = RS_ERR_BAD_ARG;
        snprintf(g_error, sizeof(g_error), "rs_cuda_fft_debug: bad capture or fft length");
        return RS_ERR_BAD_ARG;
    }

    if (g_device >= 0 && cudaSetDevice(g_device) != cudaSuccess) {
        set_error("cudaSetDevice", cudaGetLastError());
        g_last_status = RS_ERR_CUDA;
        return RS_ERR_CUDA;
    }
    if (g_device < 0) {
        RS_GPU_INFO probe;
        memset(&probe, 0, sizeof(probe));
        probe.struct_size = static_cast<int32_t>(sizeof(probe));
        if (rs_cuda_probe(&probe) != RS_OK) {
            return g_last_status;
        }
    }

    int rc = ensure_stream();
    if (rc != RS_OK) {
        return rc;
    }
    rc = arena_reserve(sample_count, fft_size);
    if (rc != RS_OK) {
        return rc;
    }

    cudaDeviceProp prop{};
    if (cudaGetDeviceProperties(&prop, g_device) != cudaSuccess) {
        set_error("cudaGetDeviceProperties", cudaGetLastError());
        g_last_status = RS_ERR_CUDA;
        return RS_ERR_CUDA;
    }

    const size_t shared = sizeof(float2) * static_cast<size_t>(fft_size);
    const size_t convert_shared = 2 * sizeof(double) * RS_THREADS;
    const size_t dc_shared = 2 * sizeof(float) * RS_THREADS;
    if (shared > static_cast<size_t>(prop.sharedMemPerBlock) ||
        convert_shared > static_cast<size_t>(prop.sharedMemPerBlock) ||
        dc_shared > static_cast<size_t>(prop.sharedMemPerBlock)) {
        g_last_status = RS_ERR_UNSUPPORTED;
        snprintf(g_error, sizeof(g_error), "rs_cuda_fft_debug: shared memory too small");
        return RS_ERR_UNSUPPORTED;
    }

    const double hz_per_rad = sample_rate_hz / (2.0 * RS_PI);
    const int64_t windows = sample_count / (fft_size / 2) - 1;

    cudaMemcpyAsync(g_arena.iq, iq, sizeof(int8_t) * 2 * sample_count,
                    cudaMemcpyHostToDevice, g_stream);
    cudaMemsetAsync(g_arena.sums, 0, 2 * sizeof(double), g_stream);
    cudaMemsetAsync(g_arena.stats, 0, 2 * sizeof(double), g_stream);
    cudaMemsetAsync(g_arena.power, 0, sizeof(float) * fft_size, g_stream);

    k_dc_partials<<<grid_for(sample_count), RS_THREADS, dc_shared, g_stream>>>(
        g_arena.iq, sample_count, g_arena.sums);
    k_hann<<<(fft_size + 127) / 128, 128, 0, g_stream>>>(g_arena.window, fft_size);
    k_twiddles<<<1, RS_THREADS, 0, g_stream>>>(
        reinterpret_cast<float2*>(g_arena.twiddles), g_arena.tw_offsets, fft_size);
    k_convert<<<grid_for(sample_count), RS_THREADS, convert_shared, g_stream>>>(
        g_arena.iq, sample_count, g_arena.sums, g_arena.re, g_arena.im, g_arena.env,
        g_arena.freq, g_arena.stats, hz_per_rad);
    k_spectra<<<static_cast<unsigned int>(windows), RS_THREADS, shared, g_stream>>>(
        g_arena.re, g_arena.im, g_arena.window,
        reinterpret_cast<const float2*>(g_arena.twiddles), g_arena.tw_offsets,
        g_arena.power, sample_count, fft_size, fft_size / 2);

    // The reference: window every Welch window on the device and transform it with double
    // sincos, accumulating the middle-half power into g_arena.naive the same way k_spectra
    // accumulates. Same windows, same order, no shared-memory fast path.
    cudaMemsetAsync(g_arena.naive, 0, sizeof(float) * fft_size, g_stream);
    k_naive_periodogram<<<static_cast<unsigned int>(windows), RS_THREADS, shared, g_stream>>>(
        g_arena.re, g_arena.im, g_arena.window, g_arena.naive,
        sample_count, fft_size, fft_size / 2);

    // Fixed-size host staging: this translation unit includes only C headers (no <vector>),
    // and fft_size is a power of two bounded by the arena, so stack arrays are safe here.
    float host_power[RS_MAX_FFT];
    float host_naive[RS_MAX_FFT];
    cudaMemcpyAsync(host_power, g_arena.power, sizeof(float) * fft_size,
                    cudaMemcpyDeviceToHost, g_stream);
    cudaMemcpyAsync(host_naive, g_arena.naive, sizeof(float) * fft_size,
                    cudaMemcpyDeviceToHost, g_stream);
    const cudaError_t sync = cudaStreamSynchronize(g_stream);
    if (sync != cudaSuccess) {
        set_error("fft debug sync", sync);
        g_last_status = RS_ERR_CUDA;
        return RS_ERR_CUDA;
    }

    // Both sides are Welch sums over the same windows; compare them as-is.
    double worst = 0.0;
    for (int j = 0; j < fft_size; j++) {
        const double fast = static_cast<double>(host_power[j]);
        const double ref = static_cast<double>(host_naive[j]);
        const double scale = fabs(ref) > 1.0 ? fabs(ref) : 1.0;
        const double d = fabs(fast - ref) / scale;
        if (d > worst) {
            worst = d;
        }
    }
    *out_worst_rel_power = worst;

    g_error[0] = '\0';
    g_last_status = RS_OK;
    return RS_OK;
}

// One analyze: take a capture as libhackrf delivers it, produce the measurement arrays
// the managed feature extractor needs. Every host buffer comes from the caller and is
// pinned there, so each transfer here is a direct DMA copy.
//
//   iq     : interleaved s8, sample_count samples (2 * sample_count bytes)
//   re, im : sample_count floats each, DC removed
//   env    : sample_count floats, |x|
//   freq   : sample_count floats, instantaneous frequency in Hz
//   power  : fft_size floats, the averaged periodogram in fftshift order
//
// sample_count must be a positive multiple of fft_size (2..20 Msps for 0.5..2 s always
// is), which keeps the periodogram's 50 %-overlapped window grid exact with no tail.
RS_API int32_t rs_cuda_extract_features(const int8_t* iq,
                                        int64_t sample_count,
                                        double sample_rate_hz,
                                        int32_t fft_size,
                                        float* re,
                                        float* im,
                                        float* env,
                                        float* freq,
                                        float* power,
                                        RS_STATS* stats)
{
    if (iq == nullptr || re == nullptr || im == nullptr || env == nullptr || freq == nullptr ||
        power == nullptr || stats == nullptr) {
        g_last_status = RS_ERR_BAD_ARG;
        snprintf(g_error, sizeof(g_error), "null buffer passed to rs_cuda_extract_features");
        return RS_ERR_BAD_ARG;
    }

    if (sample_count < 64 || sample_rate_hz <= 0) {
        g_last_status = RS_ERR_BAD_ARG;
        snprintf(g_error, sizeof(g_error), "capture too short or bad sample rate (%lld samples)",
                 static_cast<long long>(sample_count));
        return RS_ERR_BAD_ARG;
    }

    if (fft_size < 2 || (fft_size & (fft_size - 1)) != 0 || (sample_count % fft_size) != 0) {
        g_last_status = RS_ERR_BAD_ARG;
        snprintf(g_error, sizeof(g_error),
                 "sample count %lld must be a multiple of the FFT length %d",
                 static_cast<long long>(sample_count), fft_size);
        return RS_ERR_BAD_ARG;
    }

    // cudaSetDevice pins the chosen device to the calling thread. RfScout analyzes on a
    // worker thread, so re-select it on every call: it is a cheap no-op when the current
    // thread already has it, and without this a second analyze from a different thread
    // would land on device 0 and fail with a cross-context pointer.
    if (g_device >= 0 && cudaSetDevice(g_device) != cudaSuccess) {
        set_error("cudaSetDevice", cudaGetLastError());
        g_last_status = RS_ERR_CUDA;
        return RS_ERR_CUDA;
    }

    if (g_device < 0) {
        // First call in this process: pick a device and check it is usable.
        RS_GPU_INFO probe;
        memset(&probe, 0, sizeof(probe));
        probe.struct_size = static_cast<int32_t>(sizeof(probe));
        if (rs_cuda_probe(&probe) != RS_OK) {
            return g_last_status;  // rs_cuda_probe already recorded why
        }
    }

    int rc = ensure_stream();
    if (rc != RS_OK) {
        return rc;
    }

    rc = arena_reserve(sample_count, fft_size);
    if (rc != RS_OK) {
        return rc;
    }

    cudaDeviceProp prop{};
    if (cudaGetDeviceProperties(&prop, g_device) != cudaSuccess) {
        set_error("cudaGetDeviceProperties", cudaGetLastError());
        g_last_status = RS_ERR_CUDA;
        return RS_ERR_CUDA;
    }

    // k_spectra's working set is n float2 (32 KB at n = 4096), and the launch must hand
    // it exactly that. sizeof(float2) * fft_size is what the kernel's own comment asks
    // for; an earlier launcher passed sizeof(float) * fft_size here, which is the same
    // byte count, so this line is not the source of the 0xa000 faults - those were the
    // butterfly pair index (see k_spectra), now fixed.
    const size_t shared = sizeof(float2) * static_cast<size_t>(fft_size);
    // Two rows of RS_THREADS doubles: the reduction's last step reads across the row
    // boundary, so the full 4 KB, not half.
    const size_t convert_shared = 2 * sizeof(double) * RS_THREADS;
    const size_t dc_shared = 2 * sizeof(float) * RS_THREADS;

    // Dynamic shared above the default per-block carve-out (48 KB on every arch still
    // built) needs the kernel's own opt-in, and prop.sharedMemPerBlock reports exactly
    // that default cap - not the SM's full pool. So anything past it is only launchable
    // after cudaFuncSetAttribute raises this kernel's ceiling, up to
    // prop.sharedMemPerBlockOptin (sm_52: 48 KB default, 48 KB opt-in; sm_89: 48/100 KB).
    // At fft 4096 the FFT working set is 32 KB, which fits the default; larger transforms
    // take the attribute path where the device allows it.
    if (shared > static_cast<size_t>(prop.sharedMemPerBlock)) {
        if (shared > static_cast<size_t>(prop.sharedMemPerBlockOptin)) {
            g_last_status = RS_ERR_UNSUPPORTED;
            snprintf(g_error, sizeof(g_error),
                     "%s cannot hold a %d-point FFT in shared memory (%llu of %llu bytes opt-in max)",
                     prop.name, fft_size,
                     static_cast<unsigned long long>(shared),
                     static_cast<unsigned long long>(prop.sharedMemPerBlockOptin));
            return RS_ERR_UNSUPPORTED;
        }

        const cudaError_t optin = cudaFuncSetAttribute(
            k_spectra, cudaFuncAttributeMaxDynamicSharedMemorySize,
            static_cast<int>(shared));
        if (optin != cudaSuccess) {
            set_error("cudaFuncSetAttribute k_spectra", optin);
            g_last_status = RS_ERR_CUDA;
            return RS_ERR_CUDA;
        }
    }

    if (convert_shared > prop.sharedMemPerBlock || dc_shared > prop.sharedMemPerBlock) {
        g_last_status = RS_ERR_UNSUPPORTED;
        snprintf(g_error, sizeof(g_error),
                 "%s cannot hold a %d-point FFT in shared memory (%llu of %llu bytes needed)",
                 prop.name, fft_size,
                 static_cast<unsigned long long>(shared),
                 static_cast<unsigned long long>(prop.sharedMemPerBlock));
        return RS_ERR_UNSUPPORTED;
    }

    const double hz_per_rad = sample_rate_hz / (2.0 * RS_PI);
    const int64_t windows = sample_count / (fft_size / 2) - 1;

    // The capture goes up first, and the kernels then read only device memory. They cannot
    // read the caller's buffer directly: it is host memory, and on this driver a kernel
    // reading it faults even when the caller pinned it against the garbage collector, and
    // even when cudaHostRegister() reports the pages as registered. One DMA in per analyze
    // (2 MB for a 1 s capture at 2 Msps) is cheaper than arguing about it.
    cudaMemcpyAsync(g_arena.iq, iq, sizeof(int8_t) * 2 * sample_count,
                    cudaMemcpyHostToDevice, g_stream);

    // The periodogram is a Welch average: the kernel accumulates every window's power into
    // g_arena.power, and k_scale_power below divides by the window count, so the managed and
    // CUDA paths hand FeatureExtractor the same mean spectrum. The CPU reference builds the
    // same average the same way (accumulate every window, divide once at the end, see
    // CaptureMeasurements.Cpu); an earlier CPU revision cleared its accumulator per window
    // and kept only the last, which made the --gpu-test periodogram comparison fail by the
    // window-count factor while the scale-free features still agreed.
    cudaMemsetAsync(g_arena.sums, 0, 2 * sizeof(double), g_stream);
    cudaMemsetAsync(g_arena.stats, 0, 2 * sizeof(double), g_stream);

    k_dc_partials<<<grid_for(sample_count), RS_THREADS, dc_shared, g_stream>>>(
        g_arena.iq, sample_count, g_arena.sums);

    k_hann<<<(fft_size + 127) / 128, 128, 0, g_stream>>>(g_arena.window, fft_size);

    // One block: RS_THREADS threads fill n/2 entries, so this covers FFT lengths up to
    // 2 * RS_THREADS = 512 taps per thread; 4096 needs 16 each.
    k_twiddles<<<1, RS_THREADS, 0, g_stream>>>(
        reinterpret_cast<float2*>(g_arena.twiddles), g_arena.tw_offsets, fft_size);

    k_convert<<<grid_for(sample_count), RS_THREADS, convert_shared, g_stream>>>(
        g_arena.iq, sample_count, g_arena.sums, g_arena.re, g_arena.im, g_arena.env,
        g_arena.freq, g_arena.stats, hz_per_rad);

    // One block per FFT window; the block runs every butterfly stage itself.
    k_spectra<<<static_cast<unsigned int>(windows), RS_THREADS, shared, g_stream>>>(
        g_arena.re, g_arena.im, g_arena.window,
        reinterpret_cast<const float2*>(g_arena.twiddles), g_arena.tw_offsets,
        g_arena.power, sample_count, fft_size, fft_size / 2);

    cudaMemcpyAsync(re, g_arena.re, sizeof(float) * sample_count, cudaMemcpyDeviceToHost, g_stream);
    cudaMemcpyAsync(im, g_arena.im, sizeof(float) * sample_count, cudaMemcpyDeviceToHost, g_stream);
    cudaMemcpyAsync(env, g_arena.env, sizeof(float) * sample_count, cudaMemcpyDeviceToHost, g_stream);
    cudaMemcpyAsync(freq, g_arena.freq, sizeof(float) * sample_count, cudaMemcpyDeviceToHost, g_stream);
    // The Welch average: k_spectra summed every window's power, so scale the device
    // accumulator by 1/windows before it comes down. The window count matches the managed
    // loop's (start + fft <= samples; start += fft/2), which is sample_count / (fft/2) - 1
    // exactly when sample_count is a multiple of fft, as the argument check above ensures.
    if (windows > 0) {
        k_scale_power<<<(fft_size + 255) / 256, 256, 0, g_stream>>>(
            g_arena.power, fft_size, 1.0f / static_cast<float>(windows));
    }

    cudaMemcpyAsync(power, g_arena.power, sizeof(float) * fft_size, cudaMemcpyDeviceToHost, g_stream);

    double host_sums[4] = { 0, 0, 0, 0 };
    cudaMemcpyAsync(host_sums, g_arena.sums, 2 * sizeof(double), cudaMemcpyDeviceToHost, g_stream);
    cudaMemcpyAsync(host_sums + 2, g_arena.stats, 2 * sizeof(double), cudaMemcpyDeviceToHost, g_stream);

    const cudaError_t sync = cudaStreamSynchronize(g_stream);
    if (sync != cudaSuccess) {
        set_error("gpu analyze", sync);
        g_last_status = RS_ERR_CUDA;
        return RS_ERR_CUDA;
    }

    const cudaError_t pending = cudaGetLastError();
    if (pending != cudaSuccess) {
        set_error("gpu analyze launch", pending);
        g_last_status = RS_ERR_CUDA;
        return RS_ERR_CUDA;
    }

    // Sample 0 of the discriminator mirrors sample 1, as the managed path does.
    freq[0] = sample_count > 1 ? freq[1] : 0.0f;

    stats->sample_count = sample_count;
    stats->power_count = fft_size;
    stats->mean_re = host_sums[0] / static_cast<double>(sample_count);
    stats->mean_im = host_sums[1] / static_cast<double>(sample_count);
    stats->mean_power = host_sums[2] / static_cast<double>(sample_count);
    stats->peak_power = host_sums[3];
    stats->sample_rate_hz = sample_rate_hz;
    stats->reserved0 = 0.0;

    g_error[0] = '\0';
    g_last_status = RS_OK;
    return RS_OK;
}
