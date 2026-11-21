using System.Runtime.InteropServices;
using System.Threading;

namespace RfScout.Services.HackRf;

/// <summary>
/// libhackrf is process-wide: <c>hackrf_init</c> and <c>hackrf_exit</c> wrap libusb's single
/// global context, so they must run once per process however many HackRFs are opened and closed -
/// and RfScout opens and closes one every time the device is unplugged and plugged back in.
/// Reference counted under a lock, so the last device out turns the library off.
/// </summary>
internal static class LibHackRf
{
    private static readonly Lock Gate = new();
    private static int _users;

    /// <summary>Open libhackrf for one more user. Throws if libusb cannot start.</summary>
    internal static void Initialize()
    {
        lock (Gate)
        {
            if (_users == 0)
            {
                NativeMethods.ThrowForError(NativeMethods.hackrf_init(), "hackrf_init");
            }

            _users++;
        }
    }

    /// <summary>Release one user; the last one shuts libhackrf down.</summary>
    internal static void Shutdown()
    {
        lock (Gate)
        {
            if (_users == 0)
            {
                return;
            }

            _users--;
            if (_users == 0)
            {
                // A non-zero result means a device was still open (HACKRF_ERROR_NOT_LAST_DEVICE).
                // The count above is the app's own bookkeeping, so that is a leak to fix in code,
                // not a reason to fail a close the user already asked for.
                _ = NativeMethods.hackrf_exit();
            }
        }
    }

    /// <summary>How many devices are currently open. Used by tests and the About box.</summary>
    internal static int Users
    {
        get
        {
            lock (Gate)
            {
                return _users;
            }
        }
    }
}

/// <summary>
/// Owns a <c>hackrf_device*</c>. <see cref="SafeHandle"/> guarantees the close runs on a finaliser
/// thread even if the caller forgets, which matters here because a leaked device stays claimed and
/// every later open fails with <c>HACKRF_ERROR_BUSY</c>.
/// </summary>
internal sealed class HackRfHandle : SafeHandle
{
    internal HackRfHandle(IntPtr handle)
        : base(IntPtr.Zero, ownsHandle: true) => SetHandle(handle);

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>
    /// <c>hackrf_close</c> stops any running stream before releasing the device, so no RX
    /// callback can fire after it returns.
    /// </summary>
    protected override bool ReleaseHandle() => NativeMethods.hackrf_close(handle) == NativeMethods.Success;
}
