// rgas-source clip conditioning — SPEC C-25 (loudness normalize + encode),
// shared decode/resample helpers for import (C-23) and loopback capture (C-22).
//
// Loudness is ITU-R BS.1770-4 integrated (the measurement behind "LUFS"):
// K-weighting (shelf + high-pass biquads), 400 ms blocks with 75 % overlap,
// -70 LUFS absolute gate then -10 LU relative gate. Coefficients below are the
// canonical 48 kHz set from the recommendation.
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RgasSoundboard.Audio;

public static class Normalizer
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    // --- decode / standardize --------------------------------------------------

    /// <summary>Decode any supported file (mp3/wav/flac/m4a via Media Foundation,
    /// C-23) to interleaved float stereo 48 kHz.</summary>
    public static float[] DecodeFile(string path)
    {
        WaveStream reader = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new WaveFileReader(path)
            : new MediaFoundationReader(path);
        using (reader)
        {
            var samples = ReadAll(reader.ToSampleProvider(), out var format);
            return Standardize(samples, format.SampleRate, format.Channels);
        }
    }

    /// <summary>Resample/rechannel raw interleaved floats to stereo 48 kHz.</summary>
    public static float[] Standardize(float[] interleaved, int sourceRate, int sourceChannels)
    {
        ISampleProvider provider = new FloatArrayProvider(interleaved, sourceRate, sourceChannels);
        if (sourceChannels == 1) provider = new MonoToStereoSampleProvider(provider);
        else if (sourceChannels > 2) provider = new TakeFirstTwoChannels(provider);
        if (provider.WaveFormat.SampleRate != SampleRate)
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        return ReadAll(provider, out _);
    }

    private static float[] ReadAll(ISampleProvider provider, out WaveFormat format)
    {
        format = provider.WaveFormat;
        var all = new List<float>(1 << 20);
        var buf = new float[format.SampleRate * format.Channels]; // 1 s chunks
        int n;
        while ((n = provider.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++) all.Add(buf[i]);
        return all.ToArray();
    }

    // --- loudness (BS.1770-4) ----------------------------------------------------

    /// <summary>Integrated loudness in LUFS of interleaved stereo 48 kHz.
    /// Returns -70 for effective silence.</summary>
    public static double MeasureLufs(float[] stereo)
    {
        int frames = stereo.Length / Channels;
        if (frames < SampleRate * 2 / 5) return -70; // shorter than one 400 ms block

        // Per-channel K-weighting
        var weighted = new float[2][];
        for (int ch = 0; ch < 2; ch++)
        {
            var shelf = new Biquad(1.53512485958697, -2.69169618940638, 1.19839281085285,
                                   -1.69065929318241, 0.73248077421585);
            var highPass = new Biquad(1.0, -2.0, 1.0,
                                      -1.99004745483398, 0.99007225036621);
            var w = new float[frames];
            for (int i = 0; i < frames; i++)
                w[i] = (float)highPass.Process(shelf.Process(stereo[i * Channels + ch]));
            weighted[ch] = w;
        }

        // 400 ms blocks, 100 ms hop; per-block mean square summed over channels
        int blockLen = SampleRate * 2 / 5, hop = SampleRate / 10;
        var blockPower = new List<double>();
        for (int start = 0; start + blockLen <= frames; start += hop)
        {
            double sum = 0;
            for (int ch = 0; ch < 2; ch++)
            {
                var w = weighted[ch];
                double s = 0;
                for (int i = start; i < start + blockLen; i++) s += (double)w[i] * w[i];
                sum += s / blockLen;
            }
            blockPower.Add(sum);
        }
        if (blockPower.Count == 0) return -70;

        static double Loudness(double power) => -0.691 + 10 * Math.Log10(Math.Max(power, 1e-12));

        // Absolute gate at -70 LUFS
        var gated = blockPower.Where(p => Loudness(p) > -70).ToList();
        if (gated.Count == 0) return -70;
        // Relative gate: -10 LU under the absolute-gated mean
        double relThreshold = Loudness(gated.Average()) - 10;
        var final = gated.Where(p => Loudness(p) > relThreshold).ToList();
        if (final.Count == 0) return -70;
        return Loudness(final.Average());
    }

    /// <summary>Gain to the target LUFS, peak-guarded to -1 dBFS (C-25). The guard
    /// can leave very dynamic material under target — preferable to clipping.</summary>
    public static void NormalizeInPlace(float[] stereo, double targetLufs)
    {
        double measured = MeasureLufs(stereo);
        double gain = Math.Pow(10, (targetLufs - measured) / 20.0);
        float peak = 1e-6f;
        foreach (var s in stereo) peak = Math.Max(peak, Math.Abs(s));
        double maxGain = Math.Pow(10, -1 / 20.0) / peak; // true-peak-ish ceiling at -1 dBFS
        gain = Math.Min(gain, maxGain);
        for (int i = 0; i < stereo.Length; i++) stereo[i] = (float)(stereo[i] * gain);
    }

    // --- encode / load -----------------------------------------------------------

    /// <summary>48 kHz 16-bit stereo WAV — instant, gapless starts (C-25).</summary>
    public static void EncodeWav16(string path, float[] stereo)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, Channels));
        writer.WriteSamples(stereo, 0, stereo.Length);
    }

    /// <summary>Load a library WAV into the engine's in-memory cache format (C-14).</summary>
    public static float[] LoadWav(string path)
    {
        using var reader = new WaveFileReader(path);
        var samples = ReadAll(reader.ToSampleProvider(), out var format);
        return format.SampleRate == SampleRate && format.Channels == Channels
            ? samples
            : Standardize(samples, format.SampleRate, format.Channels);
    }

    public static float[] LoadWav(Stream stream)
    {
        using var reader = new WaveFileReader(stream);
        var samples = ReadAll(reader.ToSampleProvider(), out var format);
        return format.SampleRate == SampleRate && format.Channels == Channels
            ? samples
            : Standardize(samples, format.SampleRate, format.Channels);
    }

    // --- helpers -------------------------------------------------------------------

    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        public Biquad(double b0, double b1, double b2, double a1, double a2)
        { _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2; }

        public double Process(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }
    }

    public sealed class FloatArrayProvider : ISampleProvider
    {
        private readonly float[] _data;
        private int _pos;
        public WaveFormat WaveFormat { get; }

        public FloatArrayProvider(float[] data, int rate, int channels)
        {
            _data = data;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

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
            int framesWanted = count / 2;
            int needed = framesWanted * _sourceChannels;
            if (_scratch.Length < needed) _scratch = new float[needed];
            int got = _source.Read(_scratch, 0, needed);
            int frames = got / _sourceChannels;
            for (int f = 0; f < frames; f++)
            {
                buffer[offset + f * 2] = _scratch[f * _sourceChannels];
                buffer[offset + f * 2 + 1] = _scratch[f * _sourceChannels + 1];
            }
            return frames * 2;
        }
    }
}
