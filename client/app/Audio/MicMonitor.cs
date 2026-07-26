// rgas-source open mic — SPEC C-45.
//
// Live pass-through of the system default recording device, pulled by the
// render thread once per 10 ms block (never blocks: underruns are silence-
// padded by BufferedWaveProvider's ReadFully). Standardized to stereo 48 kHz
// via the same Mono/Wdl chain LoopbackRecorder/Normalizer use for offline
// capture — here it runs live instead of batched at Stop().
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RgasSoundboard.Audio;

public sealed class MicMonitor : IDisposable
{
    private WasapiCapture? _capture;
    private ISampleProvider? _stream;
    private float[]? _scratch;

    public bool IsOpen => _capture is not null;

    /// <summary>Starts capturing the default input device. Throws if none is
    /// available — caller (AudioEngine) treats that as a no-op open (C-35 spirit).</summary>
    public void Open()
    {
        if (IsOpen) return;
        var capture = new WasapiCapture(); // default recording device, shared mode
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(300),
            DiscardOnBufferOverflow = true,
        };
        capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        ISampleProvider stream = buffer.ToSampleProvider();
        if (stream.WaveFormat.Channels == 1) stream = new MonoToStereoSampleProvider(stream);
        else if (stream.WaveFormat.Channels > 2) stream = new TakeFirstTwoChannels(stream);
        if (stream.WaveFormat.SampleRate != Normalizer.SampleRate)
            stream = new WdlResamplingSampleProvider(stream, Normalizer.SampleRate);

        capture.StartRecording();
        _capture = capture;
        _stream = stream;
    }

    public void Close()
    {
        if (_capture is null) return;
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
        _capture = null;
        _stream = null;
    }

    /// <summary>Render-thread only: adds up to mix.Length live mic samples at
    /// the given gain. Silence-padded on underrun, never blocks.</summary>
    public void MixInto(float[] mix, float gain)
    {
        var stream = _stream;
        if (stream is null) return;
        var tmp = _scratch ??= new float[mix.Length];
        int read = stream.Read(tmp, 0, mix.Length);
        for (int i = 0; i < read; i++) mix[i] += tmp[i] * gain;
    }

    public void Dispose() => Close();

    private sealed class TakeFirstTwoChannels : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _sourceChannels;
        private float[] _scratch = Array.Empty<float>();
        public WaveFormat WaveFormat { get; }

        public TakeFirstTwoChannels(ISampleProvider source)
        {
            _source = source;
            _sourceChannels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int need = frames * _sourceChannels;
            if (_scratch.Length < need) _scratch = new float[need];
            int readFrames = _source.Read(_scratch, 0, need) / _sourceChannels;
            for (int f = 0; f < readFrames; f++)
            {
                buffer[offset + f * 2] = _scratch[f * _sourceChannels];
                buffer[offset + f * 2 + 1] = _scratch[f * _sourceChannels + 1];
            }
            return readFrames * 2;
        }
    }
}
