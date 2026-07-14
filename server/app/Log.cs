// rgas-booth logging — SPEC S-9: console + size-capped logs/receiver.log.
// S-16: the status window subscribes to LineWritten and seeds from Recent().
using System.IO;

namespace RgasReceiver;

public static class Log
{
    private const long MaxBytes = 1_000_000; // cap; rotate to .old once
    private const int RecentCap = 300;
    private static readonly object Gate = new();
    private static readonly Queue<string> RecentLines = new();
    private static string? _path;

    public static event Action<string>? LineWritten;

    public static void Init(string baseDir)
    {
        var dir = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "receiver.log");
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);

    public static string[] Recent()
    {
        lock (Gate) return RecentLines.ToArray();
    }

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {level} {msg}";
        lock (Gate)
        {
            Console.WriteLine(line);
            RecentLines.Enqueue(line);
            while (RecentLines.Count > RecentCap) RecentLines.Dequeue();
            if (_path is not null)
            {
                try
                {
                    if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                        File.Move(_path, _path + ".old", overwrite: true);
                    File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
                }
                catch { /* logging must never take the audio path down */ }
            }
        }
        LineWritten?.Invoke(line);
    }
}
