// rgas-source AI announcer — SPEC C-42/C-43.
//
// Typed text is PERFORMED by an omni voice model (OpenAI gpt-audio via chat
// completions with audio output) or ElevenLabs v3, chosen automatically from
// the key format (sk-… OpenAI / sk_… ElevenLabs). Delivery is inferred from
// typography: caps ratio, exclamations, or stretched letters => screaming
// arena announcer; otherwise a calm PA voice. Results are standardized to
// 48 kHz stereo, loudness-normalized, and cached for instant replay.
//
// This is the app's ONLY online feature (C-15 amendment): every failure is an
// exception the popup renders inline — nothing else in the app is affected.
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RgasSoundboard.Audio;

public sealed class AnnouncerService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    private readonly Config _cfg;
    private readonly object _gate = new();
    private readonly Dictionary<string, float[]> _cache = new(); // (tone:text) -> samples
    private readonly List<Take> _history = new();

    public AnnouncerService(Config cfg) => _cfg = cfg;

    public bool HasKey => !string.IsNullOrWhiteSpace(_cfg.AnnouncerApiKey);

    // --- C-42: personalities ------------------------------------------------------

    public sealed record Personality(string Id, string Label, string Emoji, string OpenAiVoice, string Persona);

    public static readonly Personality[] Personalities =
    {
        new("energetic", "ENERGETIC MALE", "⚡", "ash",
            "a high-octane young male arena announcer, fast-paced and bursting with infectious energy"),
        new("deep_bass", "DEEP BASS MALE", "🎙", "onyx",
            "a legendary veteran male announcer with a deep, booming bass voice that rumbles through the arena"),
        new("authority_f", "AUTHORITATIVE FEMALE", "👑", "sage",
            "a commanding, authoritative female arena announcer with crisp, confident, unmistakable delivery"),
    };

    public static Personality GetPersonality(string? id) =>
        Personalities.FirstOrDefault(p => p.Id == id) ?? Personalities[1]; // default: deep bass

    /// <summary>C-42: recent takes survive the popup's auto-close for replay.</summary>
    public sealed record Take(string Text, bool Dramatic, float[] Samples)
    {
        public override string ToString() => $"{(Dramatic ? "🔥" : "·")}  {Text}";
    }

    public IReadOnlyList<Take> History { get { lock (_gate) return _history.ToList(); } }

    public void RecordTake(string text, bool dramatic, float[] samples)
    {
        lock (_gate)
        {
            _history.RemoveAll(t => t.Text == text && t.Dramatic == dramatic);
            _history.Insert(0, new Take(text, dramatic, samples));
            if (_history.Count > 5) _history.RemoveRange(5, _history.Count - 5);
        }
    }

    /// <summary>C-42: text in (parentheses) is a stage direction — it steers the
    /// performance and is never spoken. Returns the spoken script + directions.</summary>
    public static (string Script, string? Directions) ParseDirections(string text)
    {
        var directions = new List<string>();
        var script = Regex.Replace(text, @"\(([^)]*)\)", m =>
        {
            var d = m.Groups[1].Value.Trim();
            if (d.Length > 0) directions.Add(d);
            return " ";
        });
        script = Regex.Replace(script, @"\s+", " ").Trim();
        return (script, directions.Count > 0 ? string.Join("; ", directions) : null);
    }

    /// <summary>C-42 tone heuristic: caps ratio > 0.5, >= 2 bangs, or letter runs of 3+.</summary>
    public static bool IsDramatic(string text)
    {
        int letters = 0, caps = 0, bangs = 0;
        foreach (var ch in text)
        {
            if (char.IsLetter(ch)) { letters++; if (char.IsUpper(ch)) caps++; }
            else if (ch == '!') bangs++;
        }
        bool elongated = Regex.IsMatch(text, @"(\p{L})\1{2,}", RegexOptions.IgnoreCase);
        double capsRatio = letters >= 4 ? caps / (double)letters : 0;
        return capsRatio > 0.5 || bangs >= 2 || elongated;
    }

    /// <summary>Synthesize (or fetch cached) announcement audio, engine-ready.</summary>
    public async Task<float[]> SynthesizeAsync(string text, Personality personality, CancellationToken ct = default)
    {
        text = text.Trim();
        var (script, directions) = ParseDirections(text); // C-42: (…) = stage direction
        if (script.Length == 0)
            throw new InvalidOperationException("Nothing to announce — the text is all (directions).");
        bool dramatic = IsDramatic(script); // tone from the SPOKEN words only
        var cacheKey = $"{personality.Id}:{(dramatic ? "D" : "P")}:{text}";
        lock (_gate) { if (_cache.TryGetValue(cacheKey, out var hit)) return hit; }

        var key = _cfg.AnnouncerApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No announcer API key configured.");

        bool openAi = key.StartsWith("sk-", StringComparison.Ordinal); // ElevenLabs keys are sk_
        byte[] audio;
        string ext;
        if (openAi && dramatic)
        {
            // Dramatic: the omni PERFORMANCE model, with the verbatim guard — check
            // the returned transcript against the script; off-script is retried
            // once, then refused. Improv never airs.
            ext = "wav";
            string? transcript;
            (audio, transcript) = await OpenAiOmniAsync(key, text, personality, ct);
            if (!OnScript(script, transcript))
            {
                (audio, transcript) = await OpenAiOmniAsync(key, text, personality, ct);
                if (!OnScript(script, transcript))
                    throw new InvalidOperationException(
                        $"The performance went off-script twice (said: “{Truncate(transcript ?? "?", 90)}”). Try again.");
            }
        }
        else if (openAi)
        {
            // Plain: the deterministic TTS endpoint — verbatim by construction, it
            // cannot ad-lib (routine PA lines invite the omni model to pad with
            // boilerplate). Faster and cheaper as a bonus.
            ext = "mp3";
            audio = await OpenAiTtsAsync(key, script, directions, personality, ct);
        }
        else
        {
            ext = "mp3";
            audio = await ElevenLabsAsync(key, script, directions, dramatic, ct);
        }

        var samples = Decode(audio, ext);
        NormalizeSpeech(samples, _cfg.AnnouncerTargetLufs); // C-42
        SaveLastTake(audio, ext, samples); // diagnostics + reuse: exactly what went on air
        lock (_gate) _cache[cacheKey] = samples;
        return samples;
    }

    /// <summary>Keeps the raw API audio and the processed take next to the exe
    /// (overwritten each announcement) — ground truth when onsets sound clipped,
    /// and a way to salvage a great take as a clip.</summary>
    private static void SaveLastTake(byte[] rawApiAudio, string ext, float[] processed)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "announcements");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"last-api.{ext}"), rawApiAudio);
            Normalizer.EncodeWav16(Path.Combine(dir, "last-processed.wav"), processed);
        }
        catch { /* diagnostics only — never fail an announcement over this */ }
    }

    /// <summary>Speech has a huge crest factor, so a peak-guarded normalize can never
    /// reach a hot integrated target. Instead: full gain to target, then the same
    /// soft limiter shape as the master bus tames the peaks. (internal for selftest)</summary>
    internal static void NormalizeSpeech(float[] samples, double targetLufs)
    {
        double measured = Normalizer.MeasureLufs(samples);
        float gain = (float)Math.Pow(10, (targetLufs - measured) / 20.0);
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i] * gain;
            float av = Math.Abs(v);
            samples[i] = av > 0.9f ? MathF.Sign(v) * (0.9f + 0.1f * MathF.Tanh((av - 0.9f) / 0.1f)) : v;
        }
    }

    // --- providers ---------------------------------------------------------------

    /// <summary>Lenient script-vs-transcript check (internal for selftest): tolerant
    /// of elongation ("GOOOAL"), punctuation, and spoken numbers; flags missing
    /// words or substantial ad-libbed extras. No transcript => assume on-script.</summary>
    internal static bool OnScript(string script, string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return true;

        static List<string> Tokens(string s) => Regex.Matches(s.ToLowerInvariant(), @"\p{L}+")
            .Select(m => Regex.Replace(m.Value, @"(\p{L})\1+", "$1")) // collapse elongation
            .ToList();

        var expected = Tokens(script);
        if (expected.Count == 0) return true;
        var got = Tokens(transcript);
        var pool = new List<string>(got);
        int matched = 0;
        foreach (var word in expected)
            if (pool.Remove(word)) matched++;
        double coverage = matched / (double)expected.Count;
        int extras = pool.Count; // spoken words that aren't in the script
        return coverage >= 0.6 && extras <= Math.Max(4, (int)(expected.Count * 0.8));
    }

    /// <summary>Plain path: /v1/audio/speech reads the input verbatim — no script
    /// guard needed. Persona + any (directions) ride in the instructions field.</summary>
    private async Task<byte[]> OpenAiTtsAsync(string key, string script, string? directions,
        Personality personality, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = "gpt-4o-mini-tts",
            ["voice"] = personality.OpenAiVoice,
            ["input"] = script, // parens stripped: a TTS engine would read them aloud
            ["instructions"] = $"You are {personality.Persona}, making a routine " +
                               "public-address announcement at a community ice rink. " +
                               "Calm, clear, unhurried delivery." +
                               (directions is null ? "" : $" Voice direction: {directions}."),
            ["response_format"] = "mp3",
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(body),
                new MediaTypeHeaderValue("application/json")),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw ApiError(resp, await resp.Content.ReadAsStringAsync(ct));
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<(byte[] Audio, string? Transcript)> OpenAiOmniAsync(string key, string text,
        Personality personality, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _cfg.AnnouncerOpenAiModel,
            ["modalities"] = new[] { "text", "audio" },
            ["audio"] = new Dictionary<string, object?>
            {
                ["voice"] = personality.OpenAiVoice, // C-42: personality picks the voice
                ["format"] = "wav",
            },
            ["messages"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "system",
                    // C-42: the original dramatic prompt — the version that behaved.
                    // (Plain announcements never reach this model; see OpenAiTtsAsync.)
                    ["content"] =
                        $"You are {personality.Persona}, at the exact moment of " +
                        "a game-winning overtime goal. Perform the user's announcement with " +
                        "full-throated, screaming, ecstatic intensity — explosive attack, huge " +
                        "dramatic build, voice cracking with excitement. Stretch elongated " +
                        "words ('GOOOAL') for seconds. Bellow ALL-CAPS words. Speak ONLY the " +
                        "announcement itself, word-for-word as given — no greetings, no " +
                        "commentary, nothing added. Text in (parentheses) is performance " +
                        "direction only: act on it, never speak it.",
                },
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = text },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body),
                new MediaTypeHeaderValue("application/json")),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw ApiError(resp, json);
        using var doc = JsonDocument.Parse(json);
        var audioEl = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("audio");
        var b64 = audioEl.GetProperty("data").GetString()!;
        string? transcript = audioEl.TryGetProperty("transcript", out var t) ? t.GetString() : null;
        return (Convert.FromBase64String(b64), transcript);
    }

    private async Task<byte[]> ElevenLabsAsync(string key, string text, string? directions,
        bool dramatic, CancellationToken ct)
    {
        // v3 takes free-form audio tags: stage directions map straight onto them
        var prefix = (directions is null ? "" : $"[{directions}] ") + (dramatic ? "[excited] [shouting] " : "");
        var body = new Dictionary<string, object?>
        {
            ["text"] = prefix + text,
            ["model_id"] = _cfg.AnnouncerElevenLabsModel,
            ["voice_settings"] = new Dictionary<string, object?>
            {
                ["stability"] = dramatic ? 0.0 : 0.5, // v3: 0=creative, 0.5=natural, 1=robust
            },
        };
        var url = $"https://api.elevenlabs.io/v1/text-to-speech/{_cfg.AnnouncerElevenLabsVoice}?output_format=mp3_44100_128";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body),
                new MediaTypeHeaderValue("application/json")),
        };
        req.Headers.Add("xi-api-key", key);
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw ApiError(resp, await resp.Content.ReadAsStringAsync(ct));
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private static Exception ApiError(HttpResponseMessage resp, string detail)
    {
        var code = (int)resp.StatusCode;
        var friendly = code switch
        {
            401 => "The API key was rejected (401). Check or replace the key.",
            429 => "Rate limit / quota exceeded (429). Try again in a moment.",
            _ => $"Announcer service error ({code}).",
        };
        return new InvalidOperationException(
            $"{friendly} {Truncate(detail, 200)}", new HttpRequestException(detail));
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // --- decode to engine format ----------------------------------------------------

    /// <summary>Always via a temp FILE: gpt-audio's WAV carries streaming-style
    /// placeholder chunk sizes (0xFFFFFFFF) that make MemoryStream parsing throw
    /// on seek; FileStream tolerates seeking past EOF, so file-based decode works
    /// for well-formed and streaming headers alike. (internal for selftest)</summary>
    internal static float[] Decode(byte[] audio, string ext)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"rgas-ann-{Guid.NewGuid():N}.{ext}");
        try
        {
            File.WriteAllBytes(tmp, audio);
            return Normalizer.DecodeFile(tmp);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
