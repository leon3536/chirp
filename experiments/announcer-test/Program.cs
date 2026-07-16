// AI announcer prototype (planned RGAS C-42) — standalone, local playback only.
//
// Type a sentence: the tone is inferred from typography (ALL CAPS, !!, GOOOAL
// elongation => dramatic; otherwise plain), sent to ElevenLabs, and played on
// the DEFAULT OUTPUT DEVICE of this machine. Never touches the booth.
//
// Commands:
//   <text>            announce (auto tone)
//   /plain <text>     force plain delivery
//   /drama <text>     force dramatic delivery
//   /voices           list voices available to the account
//   /quit             exit
//   --test (arg)      run offline tone-detector tests, no network
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;

var baseDir = AppContext.BaseDirectory;

if (args.Contains("--test")) return ToneTests.Run();

var configPath = Path.Combine(baseDir, "config.json");
if (!File.Exists(configPath))
{
    // dotnet run: also look next to the source
    var alt = Path.Combine(Directory.GetCurrentDirectory(), "config.json");
    if (File.Exists(alt)) configPath = alt;
}
if (!File.Exists(configPath))
{
    Console.WriteLine("config.json not found. Copy config.example.json to config.json and set api_key.");
    return 1;
}
var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath))!;
if (string.IsNullOrWhiteSpace(cfg.ApiKey))
{
    Console.WriteLine("api_key is empty in config.json — paste your ElevenLabs key there and rerun.");
    return 1;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.Add("xi-api-key", cfg.ApiKey);

Console.WriteLine($"AI announcer prototype — voice {cfg.VoiceId}, model {cfg.ModelId}");
Console.WriteLine("Type text (ALL CAPS / !! / GOOOAL => dramatic). /voices /plain /drama /quit\n");

int take = 0;
while (true)
{
    Console.Write("announce> ");
    var line = Console.ReadLine();
    if (line is null || line.Trim() == "/quit") break;
    line = line.Trim();
    if (line.Length == 0) continue;

    if (line == "/voices")
    {
        await ListVoices();
        continue;
    }

    bool? forced = null;
    if (line.StartsWith("/plain ")) { forced = false; line = line[7..]; }
    else if (line.StartsWith("/drama ")) { forced = true; line = line[7..]; }

    bool dramatic = forced ?? Tone.IsDramatic(line);
    Console.WriteLine($"  tone: {(dramatic ? "🔥 DRAMATIC" : "· plain")}");
    try
    {
        var sw = Stopwatch.StartNew();
        var mp3 = await Synthesize(line, dramatic);
        var synthMs = sw.ElapsedMilliseconds;
        var file = Path.Combine(baseDir, $"out-{++take:00}.mp3");
        await File.WriteAllBytesAsync(file, mp3);
        Console.WriteLine($"  synthesized {mp3.Length / 1024} KB in {synthMs} ms -> {Path.GetFileName(file)}");
        Play(file);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAILED: {ex.Message}");
    }
}
return 0;

async Task<byte[]> Synthesize(string text, bool dramatic)
{
    // Dramatic: ElevenLabs v3 audio tags + creative stability; caps/elongation in
    // the text itself also steer v3's acting. Plain: verbatim, natural stability.
    string speak = dramatic ? $"[excited] {text}" : text;
    var body = new Dictionary<string, object?>
    {
        ["text"] = speak,
        ["model_id"] = cfg.ModelId,
        ["voice_settings"] = new Dictionary<string, object?>
        {
            ["stability"] = dramatic ? 0.0 : 0.5, // v3: 0=creative, 0.5=natural, 1=robust
        },
    };
    var url = $"https://api.elevenlabs.io/v1/text-to-speech/{cfg.VoiceId}?output_format=mp3_44100_128";
    using var resp = await http.PostAsync(url,
        new StringContent(JsonSerializer.Serialize(body), new MediaTypeHeaderValue("application/json")));
    if (!resp.IsSuccessStatusCode)
    {
        var detail = await resp.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"{(int)resp.StatusCode} {resp.StatusCode}: {Truncate(detail, 300)}");
    }
    return await resp.Content.ReadAsByteArrayAsync();
}

async Task ListVoices()
{
    using var resp = await http.GetAsync("https://api.elevenlabs.io/v1/voices");
    var json = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode) { Console.WriteLine($"  voices failed: {Truncate(json, 200)}"); return; }
    using var doc = JsonDocument.Parse(json);
    foreach (var v in doc.RootElement.GetProperty("voices").EnumerateArray())
    {
        var labels = v.TryGetProperty("labels", out var l) ? string.Join(", ",
            l.EnumerateObject().Select(p => p.Value.GetString())) : "";
        Console.WriteLine($"  {v.GetProperty("voice_id").GetString()}  {v.GetProperty("name").GetString(),-22} {labels}");
    }
}

static void Play(string mp3Path)
{
    using var reader = new AudioFileReader(mp3Path);
    using var output = new WasapiOut();
    output.Init(reader);
    output.Play();
    while (output.PlaybackState == PlaybackState.Playing) Thread.Sleep(100);
}

static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

internal sealed class Config
{
    [System.Text.Json.Serialization.JsonPropertyName("api_key")] public string ApiKey { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("voice_id")] public string VoiceId { get; set; } = "pNInz6obpgDQGcFmaJgB"; // Adam (premade)
    [System.Text.Json.Serialization.JsonPropertyName("model_id")] public string ModelId { get; set; } = "eleven_v3";
}

internal static class Tone
{
    // Planned C-42 heuristic: caps ratio > 0.5, >= 2 bangs, or a letter run of 3+.
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
}

internal static class ToneTests
{
    public static int Run()
    {
        (string text, bool dramatic)[] cases =
        {
            ("GOOOOAL!!!", true),
            ("GOAL SCORED BY NUMBER 97", true),
            ("Let's gooooo", true),
            ("Wow!! Amazing!!", true),
            ("Zamboni break, five minutes", false),
            ("Please clear the ice", false),
            ("Next game starts at 8 PM!", false),      // one bang, calm text
            ("Thank you for coming", false),
            ("HUGE SAVE BY THE GOALIE!!!", true),
            ("The snack bar closes in 10 minutes.", false),
        };
        int failures = 0;
        foreach (var (text, expected) in cases)
        {
            bool got = Tone.IsDramatic(text);
            Console.WriteLine($"{(got == expected ? "PASS" : "FAIL")}  {(got ? "drama" : "plain"),-5}  \"{text}\"");
            if (got != expected) failures++;
        }
        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
        return failures;
    }
}
