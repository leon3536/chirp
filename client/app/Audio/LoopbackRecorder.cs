// rgas-source system-audio capture — SPEC C-22, C-28.
//
// WASAPI loopback of the DEFAULT OUTPUT device: records whatever the laptop is
// playing (browser, Spotify) with no virtual-cable driver. Independence
// (C-28): the soundboard's own mix renders to the booth socket, not the local
// default device, so it is not captured. (Exception: config local_monitor is a
// dev aid and WOULD be captured — don't record with it on.)
using NAudio.Wave;

namespace RgasSoundboard.Audio;

public sealed class LoopbackRecorder : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private List<float> _samples = new();
    private WaveFormat? _format;
    private DateTime _startedAt;

    public bool IsRecording => _capture is not null;
    public TimeSpan Elapsed => IsRecording ? DateTime.UtcNow - _startedAt : TimeSpan.Zero;

    public void Start()
    {
        if (IsRecording) return;
        _samples = new List<float>(1 << 20);
        _capture = new WasapiLoopbackCapture(); // default render endpoint (C-22)
        _format = _capture.WaveFormat;          // typically IEEE float at the device mix rate
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, _) => { };
        _startedAt = DateTime.UtcNow;
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _format!;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || fmt.Encoding == WaveFormatEncoding.Extensible)
        {
            for (int i = 0; i + 3 < e.BytesRecorded; i += 4)
                _samples.Add(BitConverter.ToSingle(e.Buffer, i));
        }
        else if (fmt.BitsPerSample == 16)
        {
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                _samples.Add(BitConverter.ToInt16(e.Buffer, i) / 32768f);
        }
    }

    /// <summary>Stop and return the take standardized to stereo 48 kHz.</summary>
    public float[] Stop()
    {
        if (_capture is null) return Array.Empty<float>();
        var fmt = _format!;
        _capture.StopRecording();
        _capture.DataAvailable -= OnData;
        _capture.Dispose();
        _capture = null;
        var raw = _samples.ToArray();
        _samples = new List<float>();
        return raw.Length == 0
            ? Array.Empty<float>()
            : Normalizer.Standardize(raw, fmt.SampleRate, fmt.Channels);
    }

    public void Dispose()
    {
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        _capture = null;
    }
}
