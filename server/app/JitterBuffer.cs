// rgas-booth jitter buffer — SPEC S-4 (prebuffer/underrun), S-5 (anchoring),
// S-16 (live level metering), S-17 (sound-bite detection).
//
// A byte ring between the network reader and the WASAPI render thread.
// State machine:
//   Filling  -> output silence until fill >= buffer_ms, then Playing
//   Playing  -> drain; if the ring empties, count an underrun and go Filling
// Latency anchor (S-5): if fill sustains above 2*buffer_ms + 200 ms, trim back
// to buffer_ms so a network hiccup can never permanently add latency.
//
// Telemetry rides the render path (what actually goes to the PA):
// - Peak levels per channel with decay, for the window's L/R meters (S-16).
// - Sound bites (S-17): the wire is one continuous stream, so a "bite" is a
//   non-silent segment — level above -40 dBFS sustained >= 0.3 s starts one,
//   >= 1 s back under the threshold ends it.
using NAudio.Wave;

namespace RgasReceiver;

public enum BufferState { Waiting, Filling, Playing }

public sealed class JitterBuffer : IWaveProvider
{
    private const int BytesPerMs = 48_000 * 2 * 2 / 1000; // s16le 48 kHz stereo = 192 B/ms
    private const int FramesPerSecond = 48_000;
    private const float BiteThreshold = 0.01f;             // ~ -40 dBFS (S-17)
    private const int BiteStartFrames = FramesPerSecond * 3 / 10; // 0.3 s of activity
    private const int BiteEndFrames = FramesPerSecond;             // 1 s of silence

    private readonly object _gate = new();
    private readonly byte[] _ring;
    private int _head, _tail, _count;
    private readonly int _targetBytes;
    private readonly int _trimThresholdBytes;
    private BufferState _state = BufferState.Waiting;
    private bool _sourceAttached;
    private long _underruns;

    // telemetry (render thread only; read via volatile/interlocked)
    private volatile float _peakL, _peakR;
    private long _soundBites;
    private int _activeRunFrames, _silenceRunFrames;
    private bool _inBite;

    public WaveFormat WaveFormat { get; } = new WaveFormat(48000, 16, 2);

    public JitterBuffer(int bufferMs)
    {
        _targetBytes = bufferMs * BytesPerMs;
        _trimThresholdBytes = 2 * _targetBytes + 200 * BytesPerMs;
        _ring = new byte[_trimThresholdBytes + _targetBytes]; // headroom above trim point
    }

    public BufferState State { get { lock (_gate) return _state; } }
    public long Underruns => Interlocked.Read(ref _underruns);
    public int FillMs { get { lock (_gate) return _count / BytesPerMs; } }
    public bool SourceConnected { get { lock (_gate) return _sourceAttached; } }

    // S-16/S-17 telemetry
    public float PeakL => _peakL;
    public float PeakR => _peakR;
    public long SoundBites => Interlocked.Read(ref _soundBites);
    public bool SignalActive => _inBite;

    /// <summary>Network side: source connected (start prebuffering).</summary>
    public void SourceAttached()
    {
        lock (_gate)
        {
            _head = _tail = _count = 0;
            _sourceAttached = true;
            _state = BufferState.Filling;
        }
    }

    /// <summary>Network side: source gone; drain what's left, then wait.</summary>
    public void SourceDetached()
    {
        lock (_gate)
        {
            _sourceAttached = false;
            if (_count == 0) _state = BufferState.Waiting;
        }
    }

    public void Write(byte[] data, int offset, int length)
    {
        lock (_gate)
        {
            // S-5: sustained overfill -> trim oldest audio back to target.
            if (_count + length > _trimThresholdBytes)
            {
                int drop = _count + length - _targetBytes;
                drop = Math.Min(drop, _count);
                _head = (_head + drop) % _ring.Length;
                _count -= drop;
                Log.Warn($"latency anchor: trimmed {drop / BytesPerMs} ms of backlog (S-5)");
            }
            for (int i = 0; i < length; i++)
            {
                _ring[_tail] = data[offset + i];
                _tail = (_tail + 1) % _ring.Length;
            }
            _count += length;
            if (_state == BufferState.Filling && _count >= _targetBytes)
                _state = BufferState.Playing;
        }
    }

    /// <summary>Render side: always fills the whole request (silence when not Playing).</summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            int given = 0;
            if (_state == BufferState.Playing)
            {
                given = Math.Min(count, _count);
                for (int i = 0; i < given; i++)
                {
                    buffer[offset + i] = _ring[_head];
                    _head = (_head + 1) % _ring.Length;
                }
                _count -= given;
                if (given < count)
                {
                    if (_sourceAttached) // ran dry mid-stream -> underrun (S-4)
                    {
                        Interlocked.Increment(ref _underruns);
                        _state = BufferState.Filling;
                        Log.Warn($"underrun #{Underruns}: refilling to target (S-4)");
                    }
                    else // source gone and drained -> back to waiting, not an underrun
                    {
                        _state = BufferState.Waiting;
                    }
                }
            }
            if (given < count)
                Array.Clear(buffer, offset + given, count - given); // silence
        }

        Meter(buffer, offset, count); // outside the lock: local data only
        return count;
    }

    /// <summary>Peak metering + sound-bite segmentation over the rendered block.</summary>
    private void Meter(byte[] buffer, int offset, int count)
    {
        int frames = count / 4;
        float peakL = 0, peakR = 0;
        for (int f = 0; f < frames; f++)
        {
            int i = offset + f * 4;
            float l = Math.Abs((short)(buffer[i] | (buffer[i + 1] << 8)) / 32768f);
            float r = Math.Abs((short)(buffer[i + 2] | (buffer[i + 3] << 8)) / 32768f);
            if (l > peakL) peakL = l;
            if (r > peakR) peakR = r;
        }
        // decay so the meters fall smoothly between loud blocks
        _peakL = Math.Max(peakL, _peakL * 0.82f);
        _peakR = Math.Max(peakR, _peakR * 0.82f);

        // S-17 bite segmentation
        if (Math.Max(peakL, peakR) > BiteThreshold)
        {
            _silenceRunFrames = 0;
            _activeRunFrames += frames;
            if (!_inBite && _activeRunFrames >= BiteStartFrames)
            {
                _inBite = true;
                long n = Interlocked.Increment(ref _soundBites);
                Log.Info($"sound bite #{n} (S-17)");
            }
        }
        else
        {
            _silenceRunFrames += frames;
            if (_inBite && _silenceRunFrames >= BiteEndFrames)
            {
                _inBite = false;
                _activeRunFrames = 0;
            }
            else if (!_inBite)
            {
                _activeRunFrames = 0;
            }
        }
    }
}
