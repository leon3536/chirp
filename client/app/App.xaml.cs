// rgas-source app bootstrap: config -> library -> engine -> hook -> window.
// `--selftest` runs the headless pipeline checks and exits (no GUI, no devices).
using System.IO;
using System.Windows;
using NAudio.MediaFoundation;
using RgasSoundboard.Audio;
using RgasSoundboard.Hotkeys;
using RgasSoundboard.Library;

namespace RgasSoundboard;

public partial class App : Application
{
    public static Config Cfg { get; private set; } = new();
    public static LibraryStore Lib { get; private set; } = null!;
    public static AudioEngine Engine { get; private set; } = null!;
    public static GlobalKeyboardHook Hook { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var baseDir = AppContext.BaseDirectory;

        if (e.Args.Contains("--selftest"))
        {
            int code = SelfTest.Run(Path.Combine(baseDir, "selftest.log"));
            Shutdown(code);
            return;
        }

        try { MediaFoundationApi.Startup(); } catch { } // mp3/m4a/flac decode (C-23)

        Cfg = Config.Load(baseDir);          // C-33
        Lib = new LibraryStore(baseDir);     // C-19
        Engine = new AudioEngine(Cfg, Lib);
        Hook = new GlobalKeyboardHook();     // C-6 (installed on the UI thread)

        DispatcherUnhandledException += (_, args) =>
        {
            // Volunteers can't debug; log next to the exe and keep running if possible.
            try
            {
                File.AppendAllText(Path.Combine(baseDir, "rgas-error.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {args.Exception}\n");
            }
            catch { }
            args.Handled = true;
        };

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Hook?.Dispose();
        Engine?.Dispose();
        base.OnExit(e);
    }
}
