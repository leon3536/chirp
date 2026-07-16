// rgas-booth config — SPEC S-8: config.json next to the exe, defaults on any gap.
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RgasReceiver;

public sealed class Config
{
    [JsonPropertyName("listen_port")] public int ListenPort { get; set; } = 4953;
    [JsonPropertyName("buffer_ms")] public int BufferMs { get; set; } = 150;   // S-4; RGAS tunable 80–250
    [JsonPropertyName("output_device_match")] public string OutputDeviceMatch { get; set; } = ""; // S-6; "" = default device
    // S-6b: re-assert endpoint volume + unmute on every device claim; null = leave alone
    [JsonPropertyName("output_volume_percent")] public int? OutputVolumePercent { get; set; }
    [JsonPropertyName("status_port")] public int StatusPort { get; set; } = 1780; // S-7

    public static Config Load(string baseDir)
    {
        var path = Path.Combine(baseDir, "config.json");
        if (!File.Exists(path))
        {
            Log.Info($"config.json not found at {path}; using defaults (S-8)");
            return new Config();
        }
        try
        {
            var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config();
            if (cfg.BufferMs < 20 || cfg.BufferMs > 2000)
            {
                Log.Warn($"buffer_ms {cfg.BufferMs} out of range; using 150");
                cfg.BufferMs = 150;
            }
            if (cfg.OutputVolumePercent is < 0 or > 100)
            {
                Log.Warn($"output_volume_percent {cfg.OutputVolumePercent} out of range; ignoring (S-6b)");
                cfg.OutputVolumePercent = null;
            }
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Warn($"config.json unreadable ({ex.Message}); using defaults");
            return new Config();
        }
    }
}
