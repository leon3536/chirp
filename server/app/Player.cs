// rgas-booth WASAPI playout — SPEC S-6 (pin by name substring / default) and
// S-6a (follow the system default at runtime, reclaim a returning pinned
// device, and rebuild a dead render path automatically).
//
// Structure: OS endpoint notifications and WasapiOut.PlaybackStopped only
// *signal* a supervisor thread; all device work happens there. EnsurePlaying()
// is idempotent — it no-ops when the right device is already playing, so
// spurious signals are free.
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace RgasReceiver;

public sealed class Player : IDisposable
{
    private readonly JitterBuffer _buffer;
    private readonly string _match;
    private readonly int? _volumePercent;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly EndpointWatcher _watcher;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private WasapiOut? _out;
    private string _currentId = "";
    private bool _warnedPinMissing;
    private volatile bool _disposed;

    /// <summary>Currently playing device; shown in the window footer and /status.</summary>
    public string DeviceName { get; private set; } = "(no output)";

    public Player(JitterBuffer buffer, string deviceMatch, int? volumePercent = null)
    {
        _buffer = buffer;
        _match = deviceMatch ?? "";
        _volumePercent = volumePercent;
        LogRenderDevices(); // S-6: make the right output_device_match easy to find
        _watcher = new EndpointWatcher(_wake);
        _enumerator.RegisterEndpointNotificationCallback(_watcher);
        new Thread(SupervisorLoop) { IsBackground = true, Name = "rgas-audio" }.Start();
    }

    private void LogRenderDevices()
    {
        try
        {
            var names = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => d.FriendlyName);
            Log.Info($"render devices: {string.Join("  |  ", names)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"device enumeration failed: {ex.Message}");
        }
    }

    // --- supervisor (S-6a): the only thread that touches WasapiOut ------------------

    private void SupervisorLoop()
    {
        while (!_disposed)
        {
            try { EnsurePlaying(); }
            catch (Exception ex) { Log.Warn($"audio output unavailable: {ex.Message}; retrying"); }
            // notifications wake us immediately; the timeout is a backstop poll
            _wake.WaitOne(TimeSpan.FromSeconds(5));
        }
    }

    private void EnsurePlaying()
    {
        var device = Pick();
        lock (_gate)
        {
            if (_disposed) return;
            bool alive = _out is not null && _out.PlaybackState == PlaybackState.Playing;
            if (alive && device.ID == _currentId) return; // right device, still playing

            _out?.Dispose();
            _out = null;
            // 30 ms WASAPI latency; the tunable end-to-end knob is the jitter buffer.
            var wasapi = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 30);
            wasapi.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null)
                    Log.Warn($"playback stopped: {e.Exception.Message} (S-6a: rebuilding)");
                _wake.Set();
            };
            wasapi.Init(_buffer); // JitterBuffer renders silence until a source plays
            wasapi.Play();
            _out = wasapi;
            _currentId = device.ID;
            DeviceName = device.FriendlyName;
            Log.Info($"output device: {device.FriendlyName} (S-6)");

            // S-6b: self-heal a muted/nudged endpoint on every device claim
            if (_volumePercent is int pct)
            {
                try
                {
                    device.AudioEndpointVolume.Mute = false;
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = pct / 100f;
                    Log.Info($"endpoint volume pinned to {pct}%, unmuted (S-6b)");
                }
                catch (Exception ex)
                {
                    Log.Warn($"volume pin failed: {ex.Message} (S-6b)");
                }
            }
        }
    }

    private MMDevice Pick()
    {
        if (!string.IsNullOrWhiteSpace(_match))
        {
            foreach (var dev in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (dev.FriendlyName.Contains(_match, StringComparison.OrdinalIgnoreCase))
                {
                    if (_warnedPinMissing)
                    {
                        _warnedPinMissing = false;
                        Log.Info($"pinned device is back: {dev.FriendlyName} (S-6a)");
                    }
                    return dev;
                }
            }
            if (!_warnedPinMissing)
            {
                _warnedPinMissing = true;
                Log.Warn($"no active output matching '{_match}'; using system default until it returns (S-6a)");
            }
        }
        // No pin (or pin absent): the system default — and S-6a follows it live.
        return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public void Dispose()
    {
        _disposed = true;
        try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
        _wake.Set();
        lock (_gate) { _out?.Dispose(); _out = null; }
    }

    /// <summary>OS endpoint events -> wake the supervisor. Callbacks arrive on a COM
    /// thread; do nothing here but signal.</summary>
    private sealed class EndpointWatcher : IMMNotificationClient
    {
        private readonly AutoResetEvent _wake;
        public EndpointWatcher(AutoResetEvent wake) => _wake = wake;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // fires once per role; Multimedia is the playback default we follow
            if (flow == DataFlow.Render && role == Role.Multimedia) _wake.Set();
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _wake.Set();
        public void OnDeviceAdded(string pwstrDeviceId) => _wake.Set();
        public void OnDeviceRemoved(string deviceId) => _wake.Set();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
