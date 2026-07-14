// rgas-booth app bootstrap: log -> config -> core -> dashboard window (S-16).
using System.Windows;

namespace RgasReceiver;

public partial class App : Application
{
    private ReceiverCore? _core;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var baseDir = AppContext.BaseDirectory;
        Log.Init(baseDir);
        var cfg = Config.Load(baseDir);

        try
        {
            _core = new ReceiverCore(cfg);
            _core.Start();
        }
        catch (Exception ex)
        {
            // Port in use / no audio device: fail loud, let the supervisor retry (S-12).
            Log.Warn($"fatal on startup: {ex.Message}");
            MessageBox.Show(ex.Message, "RGAS Booth Receiver — failed to start",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow(_core);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _core?.Dispose();
        base.OnExit(e);
    }
}
