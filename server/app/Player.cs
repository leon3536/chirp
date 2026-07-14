// rgas-booth WASAPI playout — SPEC S-6: device pinned by name substring.
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RgasReceiver;

public sealed class Player : IDisposable
{
    private readonly WasapiOut _out;
    public string DeviceName { get; }

    public Player(JitterBuffer buffer, string deviceMatch)
    {
        var device = Pick(deviceMatch);
        DeviceName = device.FriendlyName;
        Log.Info($"output device: {DeviceName} (S-6)");
        // 30 ms WASAPI latency; the tunable end-to-end knob is the jitter buffer.
        _out = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 30);
        _out.Init(buffer);
        _out.Play(); // JitterBuffer renders silence until a source is playing
    }

    private static MMDevice Pick(string match)
    {
        var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(match))
        {
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                if (dev.FriendlyName.Contains(match, StringComparison.OrdinalIgnoreCase))
                    return dev;
            Log.Warn($"no active output device matching '{match}'; falling back to default (S-6)");
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public void Dispose() => _out.Dispose();
}
