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
            TestShuffleWeave(tempDir);
            TestTailFadeAndMasterVolume(tempDir);
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

        // C-40: device enumeration never throws; entries are well-formed
        var devices = AudioEngine.EnumerateOutputDevices();
        Check(devices.All(d => !string.IsNullOrEmpty(d.Id) && !string.IsNullOrEmpty(d.Name)),
            $"output device enumeration well-formed ({devices.Count} device(s))");

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

        // C-39: MoveClip = leave source, join target, in one operation
        var colC = lib.AddCollection("test-c", 1);
        lib.ToggleMembership(clipId, colB.Id); // clip in B
        lib.MoveClip(clipId, colB.Id, colC.Id);
        Check(!lib.Clips[0].Collections.Contains(colB.Id) && lib.Clips[0].Collections.Contains(colC.Id),
            "MoveClip leaves the source and joins the target collection");
    }

    // --- C-10: shuffle bag absorbs pool edits mid-cycle -----------------------------

    private static void TestShuffleWeave(string tempDir)
    {
        var dir = Path.Combine(tempDir, "weave");
        Directory.CreateDirectory(dir);
        var cfg = new Config();
        var lib = new LibraryStore(dir);
        var colA = lib.AddCollection("bag-a", 1);
        foreach (var name in new[] { "a1", "a2", "a3" })
        {
            Normalizer.EncodeWav16(Path.Combine(lib.AudioDir, $"{name}.wav"), Sine(220, 0.3f, 0.6));
            lib.AddClip(name, $"{name}.wav", 0.6, new[] { colA.Id });
        }
        using var engine = new AudioEngine(cfg, lib, startRenderThread: false);
        engine.SetMode(1);
        engine.ToggleSpace(); // bag built from {a1,a2,a3}; one dequeued

        // A new selected collection appears mid-cycle (the reported bug)
        var colB = lib.AddCollection("bag-b", 1);
        Normalizer.EncodeWav16(Path.Combine(lib.AudioDir, "fresh.wav"), Sine(440, 0.3f, 0.6));
        lib.AddClip("fresh", "fresh.wav", 0.6, new[] { colB.Id });

        engine.ToggleSpace(); // stop (fade)
        engine.ToggleSpace(); // start -> the pull that must weave "fresh" in
        var upcoming = engine.ShuffleQueueLabelsForTest(1)
            .Append(engine.Snapshot().NowPlayingLabel)
            .ToHashSet();
        Check(upcoming.Contains("fresh"),
            "newly checked collection joins the shuffle bag mid-cycle");
        Check(upcoming.Count == 3,
            $"weave keeps the no-repeat cycle intact ({upcoming.Count} of 3 unplayed left)");
    }

    // --- C-12 natural-end fade + C-41 master volume ---------------------------------

    private static void TestTailFadeAndMasterVolume(string tempDir)
    {
        var dir = Path.Combine(tempDir, "tail");
        Directory.CreateDirectory(dir);
        var cfg = new Config(); // fade_out_seconds default: 1.0
        var lib = new LibraryStore(dir);
        var col = lib.AddCollection("tail", 1);
        // Constant-amplitude tone that would end dead-abrupt without the tail fade
        Normalizer.EncodeWav16(Path.Combine(lib.AudioDir, "hot.wav"), Sine(330, 0.4f, 3.0));
        lib.AddClip("hot", "hot.wav", 3.0, new[] { col.Id });

        using var engine = new AudioEngine(cfg, lib, startRenderThread: false);
        var mix = new float[480 * 2];
        var pcm = new byte[480 * 2 * 2];
        double BlockLevel()
        {
            engine.RenderOneBlockForTest(mix, pcm);
            double sum = 0;
            foreach (var s in mix) sum += Math.Abs(s);
            return sum / mix.Length;
        }

        engine.SetMode(1);
        engine.ToggleSpace();
        double mid = 0, nearEnd = 0;
        for (int b = 0; b < 296; b++)
        {
            double level = BlockLevel();
            if (b == 150) mid = level;      // 1.5 s in: full level
            if (b == 293) nearEnd = level;  // ~60 ms before the end of the 3 s clip
        }
        Check(nearEnd < mid * 0.15, $"natural clip end fades out (end/mid = {nearEnd / mid:0.000})");

        // C-41: master volume scales the mix, smoothed; 0 = silence
        // (continuous mode advanced into the next playthrough at full level)
        for (int b = 0; b < 40; b++) BlockLevel(); // settle into the new track
        double full = BlockLevel();
        engine.MasterVolume = 0.5;
        for (int b = 0; b < 40; b++) BlockLevel(); // smoothing settles
        double half = BlockLevel();
        Check(Math.Abs(half / full - 0.5) < 0.1, $"master volume 50% halves output ({half / full:0.00})");
        engine.MasterVolume = 0;
        for (int b = 0; b < 40; b++) BlockLevel();
        Check(BlockLevel() < 1e-4, "master volume 0 silences output");
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
