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
            TestSkip(tempDir);
            TestTailFadeAndMasterVolume(tempDir);
            TestAnnouncer(tempDir);
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
        Render(500); // 5 s ≥ cross (50 ms) + tail (Star's is 3.5 s, the longer horn)
        Check(!engine.Snapshot().HornActive, "horn reaches idle after release");
        Check(Render(10) < 1e-6, "horn tail decays to silence");

        // Tap (C-9): brief press still produces a full blast (attack + tail)
        engine.HornDown();
        engine.RenderOneBlockForTest(mix, pcm); // ~10 ms "tap"
        engine.HornUp();
        Check(Render(80) > 0.01, "tap yields a full-sounding blast");
        Render(500); // clear attack + the longer horn's tail
        Check(Render(10) < 1e-6, "tap blast finishes to silence");

        // Two named horns + Ctrl+H toggle (C-46)
        string horn0 = engine.Snapshot().ActiveHorn;
        string horn1 = engine.ToggleHorn();
        Check(horn1 != horn0 && engine.Snapshot().ActiveHorn == horn1, "Ctrl+H toggles the active horn");
        engine.HornDown(); Check(Render(30) > 0.01, "toggled-to horn sounds"); engine.HornUp(); Render(500);
        engine.SetActiveHorn(horn0);
        Check(engine.Snapshot().ActiveHorn == horn0, "active horn can be restored (persist path)");

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

    // --- C-44 skip: discards the next-up preview only, never touches playback -------

    private static void TestSkip(string tempDir)
    {
        var dir = Path.Combine(tempDir, "skip");
        Directory.CreateDirectory(dir);
        var cfg = new Config();
        var lib = new LibraryStore(dir);
        var col = lib.AddCollection("skip-test", 1);
        foreach (var name in new[] { "one", "two", "three" })
        {
            Normalizer.EncodeWav16(Path.Combine(lib.AudioDir, $"{name}.wav"), Sine(220, 0.3f, 3.0));
            lib.AddClip(name, $"{name}.wav", 3.0, new[] { col.Id });
        }
        lib.SetOrder(1, "sequential"); // deterministic pool order (C-10)

        using var engine = new AudioEngine(cfg, lib, startRenderThread: false);
        var mix = new float[480 * 2];
        var pcm = new byte[480 * 2 * 2];
        void Render(int blocks) { for (int b = 0; b < blocks; b++) engine.RenderOneBlockForTest(mix, pcm); }

        engine.SetMode(1);
        engine.ToggleSpace(); // "one" starts, "two" is next up
        Render(5);
        var before = engine.Snapshot();
        Check(before.NowPlayingLabel == "one" && before.NextLabel == "two",
            "skip setup: playing 'one', 'two' next up");

        engine.Skip(); // discard "two" from the queue
        var after = engine.Snapshot();
        Check(after.NowPlayingLabel == "one" && after.IsPlaying,
            "skip does not touch what's currently playing");
        Check(after.NextLabel == "three",
            "skip advances the preview past the discarded track");
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

    // --- C-42/C-43: announcer tone, ducking, key-at-rest (no network) ----------------

    private static void TestAnnouncer(string tempDir)
    {
        // Tone heuristic (C-42)
        (string text, bool dramatic)[] cases =
        {
            ("GOOOOAL!!!", true),
            ("HUGE SAVE BY THE GOALIE!!!", true),
            ("Let's gooooo", true),
            ("Zamboni break, five minutes", false),
            ("Next game starts at 8 PM!", false),
            ("Please clear the ice", false),
        };
        foreach (var (text, expected) in cases)
            Check(AnnouncerService.IsDramatic(text) == expected,
                $"tone: \"{text}\" -> {(expected ? "dramatic" : "plain")}");

        // Stage directions (C-42): (…) steers, never spoken; tone from spoken words
        var (script1, dir1) = AnnouncerService.ParseDirections("(imitate a movie trailer voice) Sunday, SUNDAY");
        Check(script1 == "Sunday, SUNDAY" && dir1 == "imitate a movie trailer voice",
            "parentheses parse into directions + script");
        var (script2, dir2) = AnnouncerService.ParseDirections("(sad) (slow) game cancelled");
        Check(script2 == "game cancelled" && dir2 == "sad; slow", "multiple directions join");
        var (script3, dir3) = AnnouncerService.ParseDirections("plain call, no parens!");
        Check(script3 == "plain call, no parens!" && dir3 is null, "no parens -> no directions");
        Check(!AnnouncerService.IsDramatic(AnnouncerService.ParseDirections("(SCREAM LIKE CRAZY!!) welcome everyone").Script),
            "caps inside directions don't force dramatic tone");
        Check(AnnouncerService.ParseDirections("(only directions here)").Script.Length == 0,
            "all-directions text yields empty script");

        // Verbatim guard (C-42): transcript vs script
        Check(AnnouncerService.OnScript("Blue Devils win! Hasta la vista, baby!",
                "Blue Devils win — hasta la vista, baby!"),
            "on-script transcript passes");
        Check(AnnouncerService.OnScript("GOOOOAL BY NUMBER NINETY-SEVEN!!!",
                "Goal by number ninety-seven!"),
            "elongation and punctuation tolerated");
        Check(!AnnouncerService.OnScript("Blue Devils win!",
                "I know you want that iconic terminator tone, I will say it exactly as it is: Blue Devils win!"),
            "meta-commentary preamble is flagged off-script");
        Check(!AnnouncerService.OnScript("Please clear the ice, the zamboni is coming out",
                "Welcome everyone to the game tonight"),
            "unrelated speech is flagged off-script");
        Check(AnnouncerService.OnScript("anything", null), "missing transcript assumed on-script");
        // C-42: excited exclamations welcome; invented commentary still caught
        Check(AnnouncerService.OnScript("Blue Devils win!", "WHOO! YEAH! Blue Devils win! WOW!"),
            "excited exclamations (whoo/yeah/wow) are allowed");
        Check(!AnnouncerService.OnScript("Blue Devils win!", "Blue Devils win, he scores!"),
            "invented commentary (he scores) still flagged off-script");

        // Personalities (C-42): three, unique ids and voices, safe default
        Check(AnnouncerService.Personalities.Length == 3
              && AnnouncerService.Personalities.Select(p => p.Id).Distinct().Count() == 3
              && AnnouncerService.Personalities.Select(p => p.OpenAiVoice).Distinct().Count() == 3,
            "three distinct announcer personalities");
        Check(AnnouncerService.GetPersonality(null).Id == "deep_bass"
              && AnnouncerService.GetPersonality("bogus").Id == "deep_bass"
              && AnnouncerService.GetPersonality("energetic").Id == "energetic",
            "personality lookup defaults to deep bass");

        // Key at rest is plaintext (portable across machines) and round-trips (C-43)
        var cfgKey = new Config();
        cfgKey.SetAnnouncerApiKey("sk-test-roundtrip-1234567890");
        Check(cfgKey.AnnouncerApiKeyStored == "sk-test-roundtrip-1234567890", "announcer key stored plaintext");
        Check(cfgKey.AnnouncerApiKey == "sk-test-roundtrip-1234567890", "announcer key round-trips");

        // Legacy DPAPI-CurrentUser values (pre-2026-07-25) still decrypt on the
        // machine/account that saved them (C-43 amendment)
        var cfgLegacy = new Config
        {
            AnnouncerApiKeyStored = "dpapi:" + Convert.ToBase64String(
                System.Security.Cryptography.ProtectedData.Protect(
                    System.Text.Encoding.UTF8.GetBytes("sk-legacy-1234567890"),
                    null, System.Security.Cryptography.DataProtectionScope.CurrentUser)),
        };
        Check(cfgLegacy.AnnouncerApiKey == "sk-legacy-1234567890", "legacy DPAPI-wrapped key still decrypts");

        // Speech-like (high crest factor) material must actually REACH the loud
        // target — peak-guarded normalize can't, the soft-limited path must
        var bursty = new float[Normalizer.SampleRate * 2 * 4]; // 4 s stereo
        for (int f = 0; f < bursty.Length / 2; f++)
        {
            bool burst = (f / (Normalizer.SampleRate / 4)) % 3 != 2; // 2-on 1-off cadence
            float v = burst ? 0.25f * MathF.Sin(2 * MathF.PI * 220 * f / Normalizer.SampleRate) : 0;
            bursty[f * 2] = v;
            bursty[f * 2 + 1] = v;
        }
        AnnouncerService.NormalizeSpeech(bursty, -6.0);
        double loud = Normalizer.MeasureLufs(bursty);
        Check(Math.Abs(loud - (-6.0)) < 1.5, $"speech normalize reaches -6 LUFS (got {loud:0.0})");

        // gpt-audio WAVs carry streaming placeholder chunk sizes (0xFFFFFFFF):
        // decode must survive them (the bug behind "Stream length must be non-negative")
        var wavPath = Path.Combine(tempDir, "stream-hdr.wav");
        Normalizer.EncodeWav16(wavPath, Sine(440, 0.3f, 1.0));
        var wavBytes = File.ReadAllBytes(wavPath);
        int dataAt = -1; // locate the "data" chunk id (fmt chunk size varies)
        for (int i = 12; i < wavBytes.Length - 8; i++)
            if (wavBytes[i] == 'd' && wavBytes[i + 1] == 'a' && wavBytes[i + 2] == 't' && wavBytes[i + 3] == 'a')
            { dataAt = i; break; }
        for (int i = 0; i < 4; i++) { wavBytes[4 + i] = 0xFF; wavBytes[dataAt + 4 + i] = 0xFF; } // RIFF + data sizes
        var decoded = AnnouncerService.Decode(wavBytes, "wav");
        Check(Math.Abs(decoded.Length - Normalizer.SampleRate * 2) < 4800,
            $"streaming-header wav decodes ({decoded.Length} samples)");

        // Announcement ducks music by announcer_duck_db and recovers (C-42)
        var dir = Path.Combine(tempDir, "ann");
        Directory.CreateDirectory(dir);
        var cfg = new Config();
        var lib = new LibraryStore(dir);
        var col = lib.AddCollection("ann", 1);
        Normalizer.EncodeWav16(Path.Combine(lib.AudioDir, "bed.wav"), Sine(330, 0.4f, 6.0));
        lib.AddClip("bed", "bed.wav", 6.0, new[] { col.Id });

        using var engine = new AudioEngine(cfg, lib, startRenderThread: false);
        var mix = new float[480 * 2];
        var pcm = new byte[480 * 2 * 2];
        double Level(int settleBlocks)
        {
            for (int b = 0; b < settleBlocks; b++) engine.RenderOneBlockForTest(mix, pcm);
            engine.RenderOneBlockForTest(mix, pcm);
            double sum = 0;
            foreach (var s in mix) sum += Math.Abs(s);
            return sum / mix.Length;
        }

        engine.SetMode(1);
        engine.ToggleSpace();
        double full = Level(50);
        engine.PlayAnnouncement(new float[Normalizer.SampleRate * 2]); // 1 s of silence isolates the duck
        Check(engine.AnnouncementActive, "announcement reports active");
        double ducked = Level(40);
        double expectedDuck = Math.Pow(10, cfg.AnnouncerDuckDb / 20.0);
        Check(Math.Abs(ducked / full - expectedDuck) < 0.08,
            $"announcement ducks music to {expectedDuck:0.00} ({ducked / full:0.00})");
        double recovered = Level(120); // announcement over: duck releases
        Check(!engine.AnnouncementActive && recovered / full > 0.9,
            $"music recovers after announcement ({recovered / full:0.00})");

        // Pre-roll (C-42): the first ~150 ms of an announcement is silence, so a
        // buffer hiccup at onset can never clip the first syllable
        engine.ToggleSpace(); // stop the music bed
        Level(150); // fade completes
        engine.PlayAnnouncement(Sine(440, 0.4f, 0.5));
        double preRoll = 0;
        for (int b = 0; b < 10; b++) preRoll += Level(0); // first 100 ms
        double onset = 0;
        for (int b = 0; b < 20; b++) onset += Level(0); // next 200 ms
        Check(preRoll < 1e-4 && onset > 0.05,
            $"announcement pre-roll: silent lead-in then voice ({preRoll:0.00000} -> {onset:0.00})");
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
