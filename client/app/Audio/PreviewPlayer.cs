// rgas-source trim preview — SPEC C-24: preview plays on the LOCAL default
// device only, never pushed to the booth. One shot at a time; disabled while
// recording (C-28, enforced by the Capture view).
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RgasSoundboard.Audio;

public sealed class PreviewPlayer : IDisposable
{
    private WasapiOut? _out;

    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;

    /// <summary>Play a slice (frame indexes) of interleaved stereo 48 kHz floats.</summary>
    public void Play(float[] stereo, int startFrame, int endFrame)
    {
        Stop();
        startFrame = Math.Clamp(startFrame, 0, stereo.Length / 2);
        endFrame = Math.Clamp(endFrame, startFrame, stereo.Length / 2);
        int count = (endFrame - startFrame) * 2;
        if (count <= 0) return;
        var slice = new float[count];
        Array.Copy(stereo, startFrame * 2, slice, 0, count);
        try
        {
            var provider = new Normalizer.FloatArrayProvider(slice, Normalizer.SampleRate, 2);
            _out = new WasapiOut(AudioClientShareMode.Shared, 100);
            _out.Init(new SampleToWaveProvider16(provider));
            _out.PlaybackStopped += (_, _) => { };
            _out.Play();
        }
        catch
        {
            _out = null; // no local device: preview silently unavailable
        }
    }

    public void Stop()
    {
        try { _out?.Stop(); } catch { }
        _out?.Dispose();
        _out = null;
    }

    public void Dispose() => Stop();
}
