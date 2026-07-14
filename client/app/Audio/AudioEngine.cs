// rgas-source audio engine — SPEC C-8 (space semantics), C-9 (hold-to-sound
// horn over ducked music), C-11 (advance behavior), C-12 (fade-outs), C-13
// (mode-switch crossfade), C-14 (in-memory cache, low-latency mix, soft
// limiter), C-18 (pool = union of selected collections), C-28 (recording mutes
// local render), C-30 (continuous stream), C-32 (speaker modes).
//
// The engine is its own clock: a render thread produces 10 ms blocks paced by
// Stopwatch, mixes voices, soft-limits, converts to s16le, and hands every
// block — including pure silence — to the BoothPusher (C-30). The local
// default output device is only used when the Speaker mode says so (C-32).
using System.Diagnostics;
using System.IO;
using System.Reflection;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RgasSoundboard.Audio;

using RgasSoundboard.Library;

/// <summary>C-32: what the mixed output feeds.</summary>
public enum SpeakerMode { StreamOnly, PlaybackAndStream, PlaybackOnly }

public sealed record EngineSnapshot(
    int Mode, bool IsPlaying, string NowPlayingLabel, string NowPlayingClipId,
    double RemainingSeconds, double DurationSeconds, bool HornActive, bool PoolEmpty,
    SpeakerMode Speaker, bool BoothConnected, string BoothStatus);

public sealed class AudioEngine : IDisposable
{
    private const int BlockFrames = 480; // 10 ms @ 48 kHz
    private const double QuickFadeSeconds = 0.03; // click-free edge for engine-internal cuts

    private readonly Config _cfg;
    private readonly LibraryStore _library;
    private readonly BoothPusher _pusher;
    private readonly object _gate = new();

    private readonly Dictionary<string, float[]> _cache = new(); // C-14
    private readonly Horn _hornMachine; // C-9: attack / loop-while-held / tail

    private Voice? _music;
    private readonly List<Voice> _outgoing = new(); // crossfade/fade-out tails
    private double _duck = 1.0; // smoothed toward target each block (C-9)

    private int _mode = 4; // start silent
    private bool _userStopped = true; // Space semantics: don't auto-advance after a stop
    private readonly Dictionary<int, Queue<Clip>> _shuffleQueues = new();
    private readonly Dictionary<int, int> _sequentialIndex = new();

    // C-38: rolling mono window of the mixed output for the spectrum analyzer
    private const int SpectrumWindow = 2048;
    private readonly float[] _specRing = new float[SpectrumWindow];
    private readonly object _specLock = new();
    private int _specPos;

    private volatile bool _disposed;
    private volatile SpeakerMode _speaker = SpeakerMode.StreamOnly;
    private volatile bool _localMuted; // C-28: recording in progress
    private BufferedWaveProvider? _localBuffer;
    private WasapiOut? _localOut;
    private bool _localOutFailed;
    private readonly byte[] _silenceBlock = new byte[BlockFrames * 2 * 2];

    public event Action? StateChanged;
    public bool BoothConnected => _pusher.IsConnected;
    public string BoothStatus => _pusher.Status;
    public event Action<bool>? ConnectionChanged;

    public SpeakerMode Speaker
    {
        get => _speaker;
        set
        {
            _speaker = value;
            if (value != SpeakerMode.StreamOnly) EnsureLocalOut();
            StateChanged?.Invoke();
        }
    }

    /// <summary>C-28: while a loopback recording runs, the local render is muted
    /// so the soundboard can never record itself. The booth stream is unaffected.</summary>
    public bool LocalMuted { get => _localMuted; set => _localMuted = value; }

    public AudioEngine(Config cfg, LibraryStore library, bool startRenderThread = true)
    {
        _cfg = cfg;
        _library = library;
        _hornMachine = new Horn(
            LoadHornAsset("horn_attack.wav"),
            LoadHornAsset("horn_loop.wav"),
            LoadHornAsset("horn_tail.wav")); // C-20
        _pusher = new BoothPusher(cfg.BoothIp, cfg.BoothPort);
        _pusher.ConnectionChanged += up => ConnectionChanged?.Invoke(up);
        _library.Changed += () => Task.Run(PreloadPools);
        Task.Run(PreloadPools);
        if (startRenderThread)
            new Thread(RenderLoop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "rgas-render" }.Start();
    }

    /// <summary>C-34: retarget after a config edit or subnet scan; persists elsewhere.</summary>
    public void SetBoothTarget(string host, int port) => _pusher.SetTarget(host, port);

    private static float[] LoadHornAsset(string name)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"RgasSoundboard.assets.{name.Replace('\\', '.')}")
            ?? throw new InvalidOperationException($"embedded horn asset {name} missing (C-20)");
        using var mem = new MemoryStream();
        stream.CopyTo(mem);
        mem.Position = 0;
        return Normalizer.LoadWav(mem);
    }

    private void EnsureLocalOut()
    {
        lock (_gate)
        {
            if (_localOut is not null || _localOutFailed) return;
            try
            {
                _localBuffer = new BufferedWaveProvider(new WaveFormat(Normalizer.SampleRate, 16, 2))
                {
                    BufferDuration = TimeSpan.FromSeconds(1),
                    DiscardOnBufferOverflow = true,
                };
                _localOut = new WasapiOut(AudioClientShareMode.Shared, 60);
                _localOut.Init(_localBuffer);
                _localOut.Play();
            }
            catch
            {
                // No usable output device: playback modes silently degrade to
                // stream-only rather than crashing (C-35 spirit).
                _localBuffer = null;
                _localOut = null;
                _localOutFailed = true;
            }
        }
    }

    // --- operator actions -------------------------------------------------------

    /// <summary>Space (C-8): idle -> start next from the active pool; playing -> fade out.</summary>
    public void ToggleSpace()
    {
        lock (_gate)
        {
            if (_mode == 4) return; // SILENCE: Space does nothing
            if (_music is not null && !_music.FadingOut)
            {
                _userStopped = true;
                BeginFade(_music, _cfg.FadeOutSeconds); // C-12: graceful, never a cut
            }
            else
            {
                StartNextLocked(_mode, fadeInSeconds: 0);
            }
        }
        StateChanged?.Invoke();
    }

    /// <summary>Impromptu play of a specific clip (C-37): crossfades over whatever
    /// is playing, starts instantly when idle. Natural-end advance then follows
    /// the active mode (C-11).</summary>
    public void PlayClip(string clipId)
    {
        lock (_gate)
        {
            var clip = _library.Clips.FirstOrDefault(c => c.Id == clipId);
            if (clip is null) return;
            if (!_cache.TryGetValue(clip.Id, out var data))
            {
                try { data = Normalizer.LoadWav(_library.WavPath(clip)); }
                catch { return; }
                _cache[clip.Id] = data;
            }
            bool playing = _music is not null && !_music.FadingOut;
            if (_music is not null && !_music.Done && !_outgoing.Contains(_music))
            {
                if (!_music.FadingOut) BeginFade(_music, playing ? _cfg.CrossfadeSeconds : QuickFadeSeconds);
                else _outgoing.Add(_music);
            }
            _music = new Voice(clip.Id, clip.Label, data, fadeInSeconds: playing ? _cfg.CrossfadeSeconds : 0);
            _userStopped = false;
        }
        StateChanged?.Invoke();
    }

    /// <summary>Explicit stop (C-37 stop control): graceful fade-out, no toggle.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_music is not null && !_music.FadingOut)
            {
                _userStopped = true;
                BeginFade(_music, _cfg.FadeOutSeconds); // C-12
            }
        }
        StateChanged?.Invoke();
    }

    /// <summary>Mode switch (C-13): crossfade if playing; fade to silence for mode 4.</summary>
    public void SetMode(int mode)
    {
        if (mode is < 1 or > 4) return;
        lock (_gate)
        {
            if (mode == _mode) return;
            _mode = mode;
            bool playing = _music is not null && !_music.FadingOut;
            if (playing)
            {
                BeginFade(_music!, _cfg.CrossfadeSeconds);
                if (mode == 4) _userStopped = true;
                else StartNextLocked(mode, fadeInSeconds: _cfg.CrossfadeSeconds);
            }
            // idle: just re-arm the pool
        }
        StateChanged?.Invoke();
    }

    /// <summary>H down (C-9): horn starts instantly; sustain loops while held.
    /// Works in every mode — the goal horn is never gated.</summary>
    public void HornDown()
    {
        lock (_gate) _hornMachine.Down();
        StateChanged?.Invoke();
    }

    /// <summary>H up (C-9): finish with the natural decay tail; never cut, never faded.</summary>
    public void HornUp()
    {
        lock (_gate) _hornMachine.Up();
        StateChanged?.Invoke();
    }

    public EngineSnapshot Snapshot()
    {
        lock (_gate)
        {
            var current = _music;
            return new EngineSnapshot(
                Mode: _mode,
                IsPlaying: current is not null && !current.FadingOut,
                NowPlayingLabel: current?.Label ?? "",
                NowPlayingClipId: current?.ClipId ?? "",
                RemainingSeconds: current?.RemainingSeconds ?? 0,
                DurationSeconds: current?.DurationSeconds ?? 0,
                HornActive: _hornMachine.Sounding,
                PoolEmpty: _mode != 4 && _library.Pool(_mode).Count == 0,
                Speaker: _speaker,
                BoothConnected: _pusher.IsConnected,
                BoothStatus: _pusher.Status);
        }
    }

    // --- pool / advance ----------------------------------------------------------

    private void StartNextLocked(int mode, double fadeInSeconds)
    {
        var clip = NextClipLocked(mode);
        if (clip is null) return; // pool empty: UI shows the hint (C-17)
        if (!_cache.TryGetValue(clip.Id, out var data))
        {
            try { data = Normalizer.LoadWav(_library.WavPath(clip)); }
            catch { return; }
            _cache[clip.Id] = data;
        }
        if (_music is not null && !_music.Done && !_outgoing.Contains(_music))
        {
            if (!_music.FadingOut) BeginFade(_music, QuickFadeSeconds);
            else _outgoing.Add(_music);
        }
        _music = new Voice(clip.Id, clip.Label, data, fadeInSeconds);
        _userStopped = false;
    }

    private Clip? NextClipLocked(int mode)
    {
        var pool = _library.Pool(mode); // C-18
        if (pool.Count == 0) return null;

        if (_library.GetOrder(mode) == "sequential") // C-10
        {
            int idx = _sequentialIndex.TryGetValue(mode, out var i) ? i : 0;
            var clip = pool[idx % pool.Count];
            _sequentialIndex[mode] = (idx + 1) % pool.Count;
            return clip;
        }

        // Shuffle: no repeats until the pool is exhausted (C-10)
        if (!_shuffleQueues.TryGetValue(mode, out var queue)) _shuffleQueues[mode] = queue = new Queue<Clip>();
        // Rebuild if drained or the pool membership changed since the queue was built
        var poolIds = pool.Select(c => c.Id).ToHashSet();
        if (queue.Count == 0 || queue.Any(c => !poolIds.Contains(c.Id)))
        {
            var shuffled = pool.OrderBy(_ => Random.Shared.Next()).ToList();
            queue.Clear();
            foreach (var c in shuffled) queue.Enqueue(c);
        }
        return queue.Dequeue();
    }

    private void BeginFade(Voice voice, double seconds)
    {
        voice.FadeOut(seconds);
        if (!_outgoing.Contains(voice)) _outgoing.Add(voice);
        if (ReferenceEquals(_music, voice)) _music = voice; // stays visible until done
    }

    private void PreloadPools() // C-14: no decode on the trigger path
    {
        foreach (var clip in _library.Clips)
        {
            lock (_gate) { if (_cache.ContainsKey(clip.Id)) continue; }
            try
            {
                var data = Normalizer.LoadWav(_library.WavPath(clip));
                lock (_gate) _cache[clip.Id] = data;
            }
            catch { /* unreadable clip: skipped; playable ones still work */ }
        }
    }

    // --- render loop ---------------------------------------------------------------

    private void RenderLoop()
    {
        var mix = new float[BlockFrames * 2];
        var pcm = new byte[BlockFrames * 2 * 2];
        long ticksPerBlock = Stopwatch.Frequency / 100; // 10 ms
        var clock = Stopwatch.StartNew();
        long next = clock.ElapsedTicks;

        while (!_disposed)
        {
            next += ticksPerBlock;
            RenderBlock(mix);
            EmitBlock(mix, pcm);

            long wait = next - clock.ElapsedTicks;
            if (wait > 0)
            {
                int ms = (int)(wait * 1000 / Stopwatch.Frequency);
                if (ms > 1) Thread.Sleep(ms - 1);
                while (clock.ElapsedTicks < next) Thread.SpinWait(50);
            }
            else if (wait < -ticksPerBlock * 10)
            {
                next = clock.ElapsedTicks; // fell way behind (debugger, standby): resync
            }
        }
    }

    private void EmitBlock(float[] mix, byte[] pcm)
    {
        // float [-1,1] -> s16le
        for (int i = 0; i < mix.Length; i++)
        {
            int v = (int)(mix[i] * 32767f);
            v = Math.Clamp(v, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)v;
            pcm[i * 2 + 1] = (byte)(v >> 8);
        }

        // C-38: feed the spectrum window (mono sum of the mix)
        lock (_specLock)
        {
            for (int f = 0; f < BlockFrames; f++)
            {
                _specRing[_specPos] = (mix[f * 2] + mix[f * 2 + 1]) * 0.5f;
                _specPos = (_specPos + 1) % SpectrumWindow;
            }
        }

        var speaker = _speaker;
        if (speaker == SpeakerMode.PlaybackOnly)
        {
            _pusher.Enqueue(_silenceBlock); // C-32/C-30: keep the booth primed with silence
        }
        else
        {
            var block = new byte[pcm.Length];
            Buffer.BlockCopy(pcm, 0, block, 0, pcm.Length);
            _pusher.Enqueue(block); // C-29/C-30: every block, silence included
        }

        if (speaker != SpeakerMode.StreamOnly && !_localMuted)
            _localBuffer?.AddSamples(pcm, 0, pcm.Length); // C-32 (buffer copies internally)
    }

    /// <summary>Selftest access: render exactly one 10 ms block into s16le bytes.</summary>
    internal void RenderOneBlockForTest(float[] mix, byte[] pcm)
    {
        RenderBlock(mix);
        for (int i = 0; i < mix.Length; i++)
        {
            int v = Math.Clamp((int)(mix[i] * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)v;
            pcm[i * 2 + 1] = (byte)(v >> 8);
        }
    }

    private void RenderBlock(float[] mix)
    {
        Array.Clear(mix);
        bool needAdvance = false;
        lock (_gate)
        {
            // C-9: smooth duck toward target while the horn is live (held or tailing)
            bool hornLive = _hornMachine.Sounding;
            double duckTarget = hornLive ? Math.Pow(10, _cfg.HornDuckDb / 20.0) : 1.0;

            if (_music is not null)
            {
                _music.MixInto(mix, duckStart: _duck, duckEnd: Lerp(_duck, duckTarget));
                if (_music.Done)
                {
                    _outgoing.Remove(_music);
                    bool natural = !_music.FadingOut;
                    _music = null;
                    // C-11: natural end -> advance (continuous) or stop (single-shot)
                    if (natural && !_userStopped && _mode != 4 && _cfg.IsContinuous(_mode))
                        needAdvance = true;
                }
            }
            for (int i = _outgoing.Count - 1; i >= 0; i--)
            {
                var v = _outgoing[i];
                if (!ReferenceEquals(v, _music))
                    v.MixInto(mix, duckStart: _duck, duckEnd: Lerp(_duck, duckTarget));
                if (v.Done) _outgoing.RemoveAt(i);
            }
            _duck = Lerp(_duck, duckTarget);

            _hornMachine.MixInto(mix); // horn is never ducked or faded (C-9)

            if (needAdvance) StartNextLocked(_mode, fadeInSeconds: 0);
        }

        // C-14: soft limiter on the master bus (horn + music can sum > 1.0)
        for (int i = 0; i < mix.Length; i++)
        {
            float x = mix[i];
            float ax = Math.Abs(x);
            if (ax > 0.9f)
                mix[i] = MathF.Sign(x) * (0.9f + 0.1f * MathF.Tanh((ax - 0.9f) / 0.1f));
        }

        if (needAdvance) StateChanged?.Invoke();
    }

    private static double Lerp(double current, double target) =>
        current + (target - current) * 0.25; // ~10 ms blocks -> ~30 ms duck ramp

    /// <summary>C-38: log-spaced band magnitudes (0..1) of the current mixed
    /// output, 50 Hz–16 kHz. Purely visual; called from the UI timer.</summary>
    public float[] GetSpectrum(int bands)
    {
        var window = new float[SpectrumWindow];
        lock (_specLock)
        {
            int pos = _specPos;
            for (int i = 0; i < SpectrumWindow; i++)
                window[i] = _specRing[(pos + i) % SpectrumWindow];
        }

        var fft = new NAudio.Dsp.Complex[SpectrumWindow];
        for (int i = 0; i < SpectrumWindow; i++)
        {
            // Hann window
            float w = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (SpectrumWindow - 1)));
            fft[i].X = window[i] * w;
            fft[i].Y = 0;
        }
        NAudio.Dsp.FastFourierTransform.FFT(true, (int)Math.Log2(SpectrumWindow), fft);

        const double fMin = 50, fMax = 16000;
        double binHz = (double)Normalizer.SampleRate / SpectrumWindow;
        var result = new float[bands];
        for (int b = 0; b < bands; b++)
        {
            double lo = fMin * Math.Pow(fMax / fMin, (double)b / bands);
            double hi = fMin * Math.Pow(fMax / fMin, (double)(b + 1) / bands);
            int binLo = Math.Max(1, (int)(lo / binHz));
            int binHi = Math.Min(SpectrumWindow / 2 - 1, Math.Max(binLo, (int)(hi / binHz)));
            double sum = 0;
            for (int i = binLo; i <= binHi; i++)
                sum += Math.Sqrt(fft[i].X * fft[i].X + fft[i].Y * fft[i].Y);
            double mag = sum / (binHi - binLo + 1);
            double db = 20 * Math.Log10(Math.Max(mag, 1e-7));
            result[b] = (float)Math.Clamp((db + 60) / 55.0, 0, 1); // -60..-5 dB -> 0..1
        }
        return result;
    }

    public void Dispose()
    {
        _disposed = true;
        _pusher.Dispose();
        _localOut?.Dispose();
    }

    // --- horn (C-9) --------------------------------------------------------------
    //
    // Sampler-style state machine over the three assets (C-20): key-down plays
    // the attack then loops the sustain; key-up finishes with the natural decay
    // tail. Transitions that leave the recorded continuity (loop position ->
    // tail, retrigger) use a 50 ms linear crossfade; attack -> loop and the
    // loop wrap are contiguous/pre-baked in the assets and butt-join cleanly.

    private sealed class Horn
    {
        private const int CrossFrames = 2400; // 50 ms @ 48 kHz

        private enum St { Idle, Attack, Loop, Tail }

        private readonly float[] _attack, _loop, _tail;
        private St _state = St.Idle;
        private int _pos;              // frame position within the current part
        private bool _releaseQueued;   // released during attack: full blast, then tail

        // Outgoing material during a crossfade (old part fading while new fades in)
        private float[]? _xData;
        private int _xPos;
        private int _xLeft;

        public Horn(float[] attack, float[] loop, float[] tail)
        {
            _attack = attack;
            _loop = loop;
            _tail = tail;
        }

        public bool Sounding => _state != St.Idle || _xLeft > 0;

        public void Down()
        {
            _releaseQueued = false;
            if (_state != St.Idle) StartCross(Current(), _pos); // retrigger restarts (C-9)
            _state = St.Attack;
            _pos = 0;
        }

        public void Up()
        {
            switch (_state)
            {
                case St.Attack:
                    _releaseQueued = true; // tap: finish the attack, then tail (C-9)
                    break;
                case St.Loop:
                    StartCross(_loop, _pos);
                    _state = St.Tail;
                    _pos = 0;
                    break;
                    // Tail/Idle: nothing to do
            }
        }

        public void MixInto(float[] mix)
        {
            if (!Sounding) return;
            int frames = mix.Length / 2;
            for (int f = 0; f < frames; f++)
            {
                float l = 0, r = 0;
                if (_state != St.Idle)
                {
                    var d = Current();
                    float gIn = _xLeft > 0 ? 1f - (float)_xLeft / CrossFrames : 1f;
                    l += d[_pos * 2] * gIn;
                    r += d[_pos * 2 + 1] * gIn;
                    _pos++;
                    if (_pos * 2 >= d.Length) AdvancePart();
                }
                if (_xLeft > 0 && _xData is not null)
                {
                    float gOut = (float)_xLeft / CrossFrames;
                    if (_xPos * 2 + 1 < _xData.Length)
                    {
                        l += _xData[_xPos * 2] * gOut;
                        r += _xData[_xPos * 2 + 1] * gOut;
                        _xPos++;
                    }
                    _xLeft--;
                }
                mix[f * 2] += l;
                mix[f * 2 + 1] += r;
            }
        }

        private float[] Current() => _state switch
        {
            St.Attack => _attack,
            St.Loop => _loop,
            _ => _tail,
        };

        private void StartCross(float[] data, int pos)
        {
            _xData = data;
            _xPos = pos;
            _xLeft = CrossFrames;
        }

        private void AdvancePart()
        {
            switch (_state)
            {
                case St.Attack when _releaseQueued:
                    // Tap: the attack's natural continuation is the loop start; cross
                    // from there into the decay tail.
                    StartCross(_loop, 0);
                    _state = St.Tail;
                    _pos = 0;
                    break;
                case St.Attack:
                    _state = St.Loop; // contiguous in the source recording
                    _pos = 0;
                    break;
                case St.Loop:
                    _pos = 0; // seam pre-crossfaded in the asset (C-20)
                    break;
                case St.Tail:
                    _state = St.Idle;
                    _pos = 0;
                    break;
            }
        }
    }

    // --- voice -----------------------------------------------------------------------

    private sealed class Voice
    {
        private readonly float[] _data;
        private int _pos;
        private double _env;
        private double _slopePerFrame; // + fade-in, - fade-out, 0 hold

        public string ClipId { get; }
        public string Label { get; }
        public bool FadingOut { get; private set; }
        public bool Done { get; private set; }
        public double RemainingSeconds => Math.Max(0, (_data.Length / 2 - _pos) / (double)Normalizer.SampleRate);
        public double DurationSeconds => _data.Length / 2 / (double)Normalizer.SampleRate;

        public Voice(string clipId, string label, float[] data, double fadeInSeconds)
        {
            ClipId = clipId;
            Label = label;
            _data = data;
            _env = fadeInSeconds > 0 ? 0 : 1;
            _slopePerFrame = fadeInSeconds > 0 ? 1.0 / (fadeInSeconds * Normalizer.SampleRate) : 0;
        }

        public void FadeOut(double seconds)
        {
            if (FadingOut) return;
            FadingOut = true;
            _slopePerFrame = -_env / Math.Max(seconds * Normalizer.SampleRate, 1);
        }

        /// <summary>Adds this voice into the block, applying envelope and a duck
        /// ramp interpolated across the block.</summary>
        public void MixInto(float[] mix, double duckStart, double duckEnd)
        {
            int frames = mix.Length / 2;
            int availFrames = _data.Length / 2 - _pos;
            int n = Math.Min(frames, availFrames);
            for (int f = 0; f < n; f++)
            {
                double duck = duckStart + (duckEnd - duckStart) * f / frames;
                float g = (float)(_env * duck);
                mix[f * 2] += _data[(_pos + f) * 2] * g;
                mix[f * 2 + 1] += _data[(_pos + f) * 2 + 1] * g;
                _env += _slopePerFrame;
                if (_env >= 1) { _env = 1; if (!FadingOut) _slopePerFrame = 0; }
                else if (_env <= 0 && FadingOut) { _env = 0; Done = true; _pos += f + 1; return; }
            }
            _pos += n;
            if (_pos >= _data.Length / 2) Done = true;
        }
    }
}
