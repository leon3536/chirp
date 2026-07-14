// rgas-source headless selftest — `RgasSoundboard.exe --selftest`.
//
// Exercises the audio pipeline without a GUI or audio device: BS.1770
// loudness math (C-25), WAV round-trip (C-19), the mixer + space/fade
// semantics (C-8/C-12), the hold-to-sound horn state machine (C-9), advance
// behavior (C-11), and the booth TCP push against an in-process fake
// snapserver (C-29/C-31). Results go to selftest.log next to the exe and to
// stdout when redirected. Exit code 0 = all pass.
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RgasSoundboard.Audio;
using RgasSoundboard.Library;

namespace RgasSoundboard;

public static class SelfTest
{
    private static readonly StringBuilder Log = new();
    private static int _failures;

    public static int Run(string logPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rgas-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            TestLoudness();
            TestWavRoundTrip(tempDir);
            TestEngine(tempDir);
            TestPusher();
        }
        catch (Exception ex)
        {
            Fail($"unhandled: {ex}");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        Line(_failures == 0 ? "ALL PASS" : $"{_failures} FAILURE(S)");
        try { File.WriteAllText(logPath, Log.ToString()); } catch { }
        return _failures == 0 ? 0 : 1;
    }

    // --- C-25: BS.1770 integrated loudness --------------------------------------

    private static void TestLoudness()
    {
        var sine = Sine(997, amplitude: 0.1f, seconds: 5); // -20 dBFS per channel
        double lufs = Normalizer.MeasureLufs(sine);
        Check(Math.Abs(lufs - (-20.7)) < 1.5, $"sine loudness ≈ -20.7 LUFS (got {lufs:0.00})");

        Normalizer.NormalizeInPlace(sine, -16.0);
        double after = Normalizer.MeasureLufs(sine);
        Check(Math.Abs(after - (-16.0)) < 0.5, $"normalize to -16 LUFS (got {after:0.00})");

        var silence = new float[Normalizer.SampleRate * 2 * 2];
        Check(Normalizer.MeasureLufs(silence) <= -70, "silence gates to -70 LUFS");
    }

    // --- C-19: WAV encode/load round-trip ------------------------------------------

    private static void TestWavRoundTrip(string tempDir)
    {
        var original = Sine(440, 0.5f, 1.5);
        var path = Path.Combine(tempDir, "roundtrip.wav");
        Normalizer.EncodeWav16(path, original);
        var loaded = Normalizer.LoadWav(path);
        Check(Math.Abs(loaded.Length - original.Length) <= 4,
            $"wav round-trip length ({original.Length} -> {loaded.Length})");
        float worst = 0;
        int n = Math.Min(loaded.Length, original.Length);
        for (int i = 0; i < n; i++) worst = Math.Max(worst, Math.Abs(loaded[i] - original[i]));
        Check(worst < 2f / 32768, $"wav round-trip fidelity (worst {worst:0.000000})");
    }

    // --- engine: space, fades, advance, horn ------------------------------------------

    private static void TestEngine(string tempDir)
    {
        var cfg = new Config(); // booth unset: pusher idles (C-35)
        var lib = new LibraryStore(tempDir);
        var col = lib.AddCollection("test", 1);
        var clipWav = "clip-test.wav";
        Normalizer.EncodeWav16(Path.Combine(lib.AudioDir, clipWav), Sine(330, 0.4f, 3.0));
        lib.AddClip("test tone", clipWav, 3.0, new[] { col.Id });

        using var engine = new AudioEngine(cfg, lib, startRenderThread: false);
        var mix = new float[480 * 2];
        var pcm = new byte[480 * 2 * 2];
        double Render(int blocks)
        {
            double energy = 0;
            for (int b = 0; b < blocks; b++)
            {
                engine.RenderOneBlockForTest(mix, pcm);
                foreach (var s in mix) energy += Math.Abs(s);
            }
            return energy / blocks;
        }

        // Mode 4 (launch state): Space no-ops (C-8)
        engine.ToggleSpace();
        Check(Render(10) < 1e-6, "mode 4: Space does nothing, output is silence");

        // Mode 1: Space starts, output has energy
        engine.SetMode(1);
        engine.ToggleSpace();
        Check(Render(50) > 0.01, "space starts playback");
        Check(engine.Snapshot().IsPlaying, "snapshot reports playing");

        // Space again: 2 s fade-out, silent by 3 s (C-12)
        engine.ToggleSpace();
        Check(!engine.Snapshot().IsPlaying, "snapshot reports fading/stopped after space");
        Render(250); // 2.5 s
        Check(Render(10) < 1e-6, "fade-out completes to silence");

        // Impromptu play (C-37): a specific clip starts on demand
        var clipId = lib.Clips[0].Id;
        engine.PlayClip(clipId);
        Check(Render(50) > 0.01 && engine.Snapshot().NowPlayingLabel == "test tone",
            "impromptu PlayClip starts the chosen clip");
        engine.Stop();
        Render(260);
        Check(Render(10) < 1e-6, "Stop() fades to silence");

        // Continuous advance (C-11): 3 s clip ends -> next starts automatically
        engine.ToggleSpace();
        Render(320); // 3.2 s: past the natural end
        Check(engine.Snapshot().IsPlaying && Render(10) > 0.01, "continuous mode auto-advances at track end");
        engine.ToggleSpace();
        Render(260);

        // Horn (C-9): held -> sounding well past attack+tail lengths; released -> tail then silence
        engine.HornDown();
        Check(Render(50) > 0.01, "horn sounds on key-down");
        Render(500); // 5 s held: attack (1 s) long over — must still be looping
        Check(engine.Snapshot().HornActive && Render(10) > 0.01, "horn loops while held");
        engine.HornUp();
        Render(200); // 2 s ≥ cross (50 ms) + tail (1.27 s)
        Check(!engine.Snapshot().HornActive, "horn reaches idle after release");
        Check(Render(10) < 1e-6, "horn tail decays to silence");

        // Tap (C-9): brief press still produces a full blast (attack + tail)
        engine.HornDown();
        engine.RenderOneBlockForTest(mix, pcm); // ~10 ms "tap"
        engine.HornUp();
        Check(Render(80) > 0.01, "tap yields a full-sounding blast");
        Render(300);
        Check(Render(10) < 1e-6, "tap blast finishes to silence");

        // C-17 guard: the last selected collection of a populated mode can't be deselected
        Check(!lib.SetSelected(col.Id, false), "last selected collection refuses deselection");

        // C-39: membership toggle + collection deletion semantics
        var colB = lib.AddCollection("test-b", 1);
        lib.ToggleMembership(clipId, colB.Id);
        Check(lib.Clips[0].Collections.Contains(colB.Id), "ToggleMembership adds a collection");
        lib.SetSelected(colB.Id, false); // col A remains the only selected one
        lib.DeleteCollection(col.Id);
        Check(lib.Collections.Count == 1 && lib.Collections[0].Id == colB.Id,
            "DeleteCollection removes the collection");
        Check(lib.Clips[0].Collections.SequenceEqual(new[] { colB.Id }),
            "DeleteCollection strips memberships, clip survives");
        Check(lib.Collections[0].Selected,
            "DeleteCollection auto-selects a remaining collection (C-17)");
        lib.ToggleMembership(clipId, colB.Id);
        Check(!lib.Clips[0].Collections.Contains(colB.Id), "ToggleMembership removes a collection");
    }

    // --- C-29/C-31: booth push against a fake snapserver -------------------------------

    private static void TestPusher()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var pusher = new BoothPusher("127.0.0.1", port);

        using var server = listener.AcceptTcpClient(); // pusher connects within ~1 s
        var block = new byte[1920];
        for (int i = 0; i < 20; i++) pusher.Enqueue((byte[])block.Clone());

        var stream = server.GetStream();
        stream.ReadTimeout = 5000;
        int total = 0;
        var buf = new byte[8192];
        try
        {
            while (total < 20 * 1920)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                total += n;
            }
        }
        catch (IOException) { }
        Check(total == 20 * 1920, $"pusher delivered all blocks ({total}/{20 * 1920} bytes)");
        listener.Stop();
    }

    // --- helpers ----------------------------------------------------------------------

    private static float[] Sine(double hz, float amplitude, double seconds)
    {
        int frames = (int)(seconds * Normalizer.SampleRate);
        var data = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = amplitude * (float)Math.Sin(2 * Math.PI * hz * f / Normalizer.SampleRate);
            data[f * 2] = v;
            data[f * 2 + 1] = v;
        }
        return data;
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) _failures++;
        Line($"{(ok ? "PASS" : "FAIL")}  {what}");
    }

    private static void Fail(string what)
    {
        _failures++;
        Line($"FAIL  {what}");
    }

    private static void Line(string text)
    {
        Log.AppendLine(text);
        Console.WriteLine(text);
    }
}
