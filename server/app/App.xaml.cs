// rgas-booth app bootstrap: single-instance guard -> log -> config -> core ->
// dashboard window (S-16, S-18).
using System.Threading;
using System.Windows;

namespace RgasReceiver;

public partial class App : Application
{
    // System-wide name: any second launch (duplicate supervisor, Startup shortcut
    // firing over an installer-started copy) finds this held and bows out (S-18).
    private const string MutexName = "Global\\RGAS_Booth_Receiver_SingleInstance";
    private Mutex? _instanceMutex;
    private ReceiverCore? _core;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // S-18: exactly one receiver process binds the ports. Silent exit(0) on a
        // duplicate — no dialog, no crash, so the supervisor loop never spams.
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isNew);
        if (!isNew)
        {
            Shutdown(0);
            return;
        }

        var baseDir = AppContext.BaseDirectory;
        Log.Init(baseDir);
        var cfg = Config.Load(baseDir);

        // core.Start() binds with retry and never throws for port reasons (S-18);
        // the window opens regardless and shows any fault in its state area.
        _core = new ReceiverCore(cfg);
        _core.Start();

        var window = new MainWindow(_core);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _core?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
