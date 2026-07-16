// rgas-source config — SPEC C-33: one config.json next to the exe, no
// hardcoded values. Missing file/keys fall back to defaults; a first run
// writes the defaults out so there is a file to edit. C-34: a successful
// booth scan persists the discovered address here.
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RgasSoundboard.Audio;

namespace RgasSoundboard;

public sealed class Config
{
    [JsonPropertyName("booth_ip")] public string BoothIp { get; set; } = "";                    // empty: warn + offer scan (C-34/C-35)
    [JsonPropertyName("booth_port")] public int BoothPort { get; set; } = 4953;
    [JsonPropertyName("fade_out_seconds")] public double FadeOutSeconds { get; set; } = 1.0;      // C-12
    [JsonPropertyName("crossfade_seconds")] public double CrossfadeSeconds { get; set; } = 2.0;   // C-13
    [JsonPropertyName("horn_duck_db")] public double HornDuckDb { get; set; } = -6.0;             // C-9
    [JsonPropertyName("music_target_lufs")] public double MusicTargetLufs { get; set; } = -16.0;  // C-25
    [JsonPropertyName("horn_target_lufs")] public double HornTargetLufs { get; set; } = -11.0;    // C-25
    // C-11: per-mode advance behavior — "continuous" | "single_shot"
    [JsonPropertyName("mode_advance")] public Dictionary<string, string> ModeAdvance { get; set; } = new()
    {
        ["1"] = "continuous",
        ["2"] = "single_shot",
        ["3"] = "continuous",
    };
    // C-32/C-33: fresh-install default; the operator's last choice is persisted
    // in the library store and wins — "stream_only" | "playback_and_stream" | "playback_only"
    [JsonPropertyName("default_speaker_mode")] public string DefaultSpeakerMode { get; set; } = "stream_only";

    public bool IsContinuous(int mode) =>
        !ModeAdvance.TryGetValue(mode.ToString(), out var v) || v != "single_shot";

    public SpeakerMode DefaultSpeaker => ParseSpeaker(DefaultSpeakerMode) ?? SpeakerMode.StreamOnly;

    public static SpeakerMode? ParseSpeaker(string? value) => value switch
    {
        "stream_only" => SpeakerMode.StreamOnly,
        "playback_and_stream" => SpeakerMode.PlaybackAndStream,
        "playback_only" => SpeakerMode.PlaybackOnly,
        _ => null,
    };

    public static string SpeakerName(SpeakerMode mode) => mode switch
    {
        SpeakerMode.PlaybackAndStream => "playback_and_stream",
        SpeakerMode.PlaybackOnly => "playback_only",
        _ => "stream_only",
    };

    public static Config Load(string baseDir)
    {
        var path = Path.Combine(baseDir, "config.json");
        if (!File.Exists(path))
        {
            var cfg = new Config();
            try { cfg.Save(baseDir); } catch { }
            return cfg;
        }
        try
        {
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config();
        }
        catch
        {
            return new Config(); // bad config must never keep the soundboard from starting
        }
    }

    public void Save(string baseDir)
    {
        var path = Path.Combine(baseDir, "config.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
