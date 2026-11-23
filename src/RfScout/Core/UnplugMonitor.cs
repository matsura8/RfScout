namespace RfScout.Core;

/// <summary>A snapshot of the USB bus: which declared device ids are present right now.</summary>
public delegate bool UsbPresent(ushort vid, ushort pid);

/// <summary>
/// Watches for the radio being unplugged and plugged back in, so the UI can show a banner,
/// stop cleanly, and offer to reconnect. The bus check is injected (a <see cref="UsbPresent"/>
/// delegate over the WMI enumerator in the app, a fake in unit tests), so this logic is fully
/// unit-testable without hardware and without hard-coded USB ids: the ids come from the
/// DevFlow device declaration, the same source the hardware tests use.
///
/// State machine: Watching -&gt; (device gone) Lost -&gt; (device back) ReconnectReady -&gt;
/// (caller reconnected) Watching. A Lost edge fires <see cref="DeviceLost"/> once; a return
/// edge fires <see cref="DeviceReturned"/> once. Repeat polls in the same state fire nothing.
/// </summary>
public sealed class UnplugMonitor : IDisposable
{
    private readonly UsbPresent _isPresent;
    private readonly ushort _vid;
    private readonly ushort _pid;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private bool _running;

    /// <summary>Current state; reads under the lock.</summary>
    public UnplugState State { get; private set; } = UnplugState.Watching;

    /// <summary>Raised once when the device disappears while a stream is expected.</summary>
    public event EventHandler? DeviceLost;

    /// <summary>Raised once when the device reappears after being lost.</summary>
    public event EventHandler? DeviceReturned;

    /// <summary>How often the bus is polled while running.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public UnplugMonitor(UsbPresent isPresent, ushort vid, ushort pid)
    {
        ArgumentNullException.ThrowIfNull(isPresent);
        _isPresent = isPresent;
        _vid = vid;
        _pid = pid;
    }

    /// <summary>Start polling. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _timer = new System.Threading.Timer(_ => Poll(), null, PollInterval, PollInterval);
        }
    }

    /// <summary>Stop polling. Idempotent; safe from any thread.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            System.Threading.Timer? timer = _timer;
            _timer = null;
            timer?.Dispose();
        }
    }

    /// <summary>
    /// One poll, exposed (and used by the timer) so tests can drive the machine without
    /// waiting on real timers.
    /// </summary>
    public void Poll()
    {
        bool present;
        try
        {
            present = _isPresent(_vid, _pid);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // A failed bus query says nothing about the device; keep the current state.
            return;
        }

        UnplugState from;
        UnplugState to;
        lock (_gate)
        {
            from = State;
            to = (present, from) switch
            {
                (false, UnplugState.Watching) => UnplugState.Lost,
                (true, UnplugState.Lost) => UnplugState.ReconnectReady,
                (_, UnplugState.ReconnectReady) when Volatile.Read(ref _reconnected) != 0 => UnplugState.Watching,
                _ => from,
            };
            State = to;
        }

        if (to == UnplugState.Lost && from != to)
        {
            DeviceLost?.Invoke(this, EventArgs.Empty);
        }
        else if (to == UnplugState.ReconnectReady && from != to)
        {
            DeviceReturned?.Invoke(this, EventArgs.Empty);
        }
    }

    private int _reconnected;

    /// <summary>
    /// Called by the app after it successfully re-opened the radio: returns the machine to
    /// Watching so a later unplug is reported again.
    /// </summary>
    public void NotifyReconnected()
    {
        lock (_gate)
        {
            Volatile.Write(ref _reconnected, 1);
            if (State == UnplugState.ReconnectReady)
            {
                State = UnplugState.Watching;
            }
        }

        Volatile.Write(ref _reconnected, 0);
    }

    /// <summary>True when the last poll saw the device missing.</summary>
    public bool IsDeviceMissing
    {
        get
        {
            lock (_gate)
            {
                return State is UnplugState.Lost or UnplugState.ReconnectReady;
            }
        }
    }

    public void Dispose() => Stop();
}

/// <summary>Where the unplug/replug machine currently is.</summary>
public enum UnplugState
{
    /// <summary>Device present (or not being watched yet).</summary>
    Watching = 0,

    /// <summary>Device was present and is now gone: the UI shows the banner.</summary>
    Lost = 1,

    /// <summary>Device came back; the app may re-open it.</summary>
    ReconnectReady = 2,
}
