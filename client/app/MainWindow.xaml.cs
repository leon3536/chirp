// rgas-source main window — SPEC C-4 (tabs), C-6/C-7 (background hotkeys
// toggle + hotkey routing), C-31/C-35 (connection warning), C-31a (kicked-by-booth dialog),
// C-32 (speaker button), C-34 (scan), C-38 (live spectrum analyzer).
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using RgasSoundboard.Audio;
using RgasSoundboard.Hotkeys;

namespace RgasSoundboard;

public partial class MainWindow : Window
{
    private const int EqBands = 20;

    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _eqTimer;
    private readonly DispatcherTimer _autoScanTimer;
    private readonly DispatcherTimer _volumeSaveTimer; // C-41: debounce persists while dragging
    private bool _autoScanBusy;
    private bool _announcerOpen; // C-42: gates ALL hotkeys while the popup is up
    private readonly Rectangle[] _eqBars = new Rectangle[EqBands];
    private readonly Rectangle[] _eqCaps = new Rectangle[EqBands];
    private readonly double[] _eqShown = new double[EqBands];
    private readonly double[] _eqPeaks = new double[EqBands];
    private readonly DropShadowEffect _connGlow = new() { ShadowDepth = 0, BlurRadius = 12, Opacity = 0.9 };
    private bool _hornHeldByKeyboard;

    public MainWindow()
    {
        InitializeComponent();

        Soundboard.Init(App.Engine, App.Lib);
        Capture.Init(App.Engine, App.Lib, App.Cfg);

        // C-40: restore the persisted playback device BEFORE the speaker mode
        // creates the local output (stale ids fall back to default inside the engine)
        App.Engine.SetOutputDevice(App.Lib.GetOutputDevice());

        // C-32: restore the persisted speaker mode (config default on first run)
        App.Engine.Speaker = Config.ParseSpeaker(App.Lib.GetSpeakerMode()) ?? App.Cfg.DefaultSpeaker;
        UpdateSpeakerButton();

        // C-46: restore the persisted active horn (config default on first run)
        App.Engine.SetActiveHorn(App.Lib.GetActiveHorn() ?? App.Cfg.DefaultHorn);

        // C-41: restore the persisted master volume (slider drives the engine)
        _volumeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _volumeSaveTimer.Tick += (_, _) =>
        {
            _volumeSaveTimer.Stop();
            App.Lib.SetMasterVolume(App.Engine.MasterVolume);
        };
        VolumeSlider.Value = (App.Lib.GetMasterVolume() ?? 1.0) * 100;

        // C-6/C-7/C-42: background hotkeys routing (installed on the UI thread).
        // The hook never consumes a key — it's a passive subscription — so the
        // foreground app always receives Space/1-4/H/A normally too. While the
        // announcer popup is open, EVERY hotkey passes through untouched so typing
        // 1/H/Space/A can never trigger soundboard actions.
        App.Hook.SuppressCheck = () => _announcerOpen || (IsActive &&
            (Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is PasswordBox));
        App.Hook.Hotkey += OnHotkey;

        // C-31a: the booth deliberately replaced us with another client — stop
        // pretending we'll reconnect and tell the operator plainly. Fires on the
        // push thread, so hop to the UI thread before touching WPF.
        App.Engine.BoothKicked += () => Dispatcher.BeginInvoke(OnBoothKicked);

        // Background hotkeys off: plain focused-window keys still work (C-3: hotkeys are an accelerator)
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;

        ConnDot.Effect = _connGlow;

        // Safety: losing focus mid-press must never leave the horn latched on
        // (the key-up would go to another window). C-9: tail plays, horn ends.
        Deactivated += (_, _) =>
        {
            if (_hornHeldByKeyboard) { _hornHeldByKeyboard = false; App.Engine.HornUp(); }
        };

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _uiTimer.Tick += (_, _) => RefreshStatus();
        _uiTimer.Start();

        // C-34: automatic discovery — while the booth is unreachable, background-
        // scan the local subnets and adopt whatever answers on :4953. The
        // configured address is only a first candidate, never a trap.
        _autoScanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _autoScanTimer.Tick += async (_, _) => await AutoScanAsync();
        _autoScanTimer.Start();

        // C-38: spectrum refresh, faster cadence for smooth motion
        _eqTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _eqTimer.Tick += (_, _) => RefreshSpectrum();
        _eqTimer.Start();
        EqCanvas.Loaded += (_, _) => BuildEqBars();
        EqCanvas.SizeChanged += (_, _) => BuildEqBars();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark title bar (Windows 11)
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // --- hotkeys (C-6..C-9, C-44, C-45) ------------------------------------------

    private void OnHotkey(HotkeyAction action, bool isDown)
    {
        switch (action)
        {
            case HotkeyAction.Space when isDown: App.Engine.ToggleSpace(); break;
            case HotkeyAction.Mode1 when isDown: App.Engine.SetMode(1); break;
            case HotkeyAction.Mode2 when isDown: App.Engine.SetMode(2); break;
            case HotkeyAction.Mode3 when isDown: App.Engine.SetMode(3); break;
            case HotkeyAction.Mode4 when isDown: App.Engine.SetMode(4); break;
            case HotkeyAction.Horn:
                _hornHeldByKeyboard = isDown;
                if (isDown) App.Engine.HornDown(); else App.Engine.HornUp();
                break;
            case HotkeyAction.HornToggle when isDown:
                App.Lib.SetActiveHorn(App.Engine.ToggleHorn()); // C-46: Ctrl+H, persisted
                break;
            case HotkeyAction.Announce when isDown:
                // Never block inside the low-level hook callback (Windows would
                // silently drop the hook) — open the modal on the next dispatch.
                Dispatcher.BeginInvoke(OpenAnnouncer);
                break;
            case HotkeyAction.Skip when isDown: App.Engine.Skip(); break;
            case HotkeyAction.OpenMic when isDown: App.Engine.ToggleMic(); break;
        }
    }

    /// <summary>C-42: 🎙 popup — modal; all hotkeys suppressed while it is open.</summary>
    public void OpenAnnouncer()
    {
        if (_announcerOpen) return;
        _announcerOpen = true;
        try
        {
            new UI.AnnouncerDialog(App.Announcer, App.Engine, App.Cfg, App.Lib) { Owner = this }.ShowDialog();
        }
        finally
        {
            _announcerOpen = false;
        }
    }

    /// <summary>C-31a: the booth told us another client took over (S-3a kick
    /// marker). Modal, no auto-retry underneath — the operator acknowledges and
    /// the app closes, avoiding a silent ping-pong with the client that replaced us.</summary>
    private void OnBoothKicked()
    {
        MessageBox.Show(this,
            "Your session has ended because another client has connected to the RGAS server. " +
            "Please make sure no one else is using the system.",
            "RGAS session ended", MessageBoxButton.OK, MessageBoxImage.Warning);
        Application.Current.Shutdown();
    }

    private static HotkeyAction? MapKey(Key key) => key switch
    {
        Key.Space => HotkeyAction.Space,
        Key.D1 or Key.NumPad1 => HotkeyAction.Mode1,
        Key.D2 or Key.NumPad2 => HotkeyAction.Mode2,
        Key.D3 or Key.NumPad3 => HotkeyAction.Mode3,
        Key.D4 or Key.NumPad4 => HotkeyAction.Mode4,
        Key.H => HotkeyAction.Horn,
        Key.A => HotkeyAction.Announce,
        Key.K => HotkeyAction.Skip,
        Key.O when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) => HotkeyAction.OpenMic,
        _ => null,
    };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (App.Hook.Enabled) return; // the background hook already routes these
        if (e.IsRepeat) { if (MapKey(e.Key) is not null) e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is PasswordBox) return; // C-7
        var action = MapKey(e.Key);
        if (action is null) return;
        // C-46: Ctrl+H toggles the active horn (focused mode); plain H is the hold.
        if (action == HotkeyAction.Horn && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            action = HotkeyAction.HornToggle;
        OnHotkey(action.Value, true); // Horn case records _hornHeldByKeyboard
        e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (App.Hook.Enabled) return;
        if (Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is PasswordBox) return;
        var action = MapKey(e.Key);
        if (action is null) return;
        if (action == HotkeyAction.Horn) OnHotkey(HotkeyAction.Horn, false);
        e.Handled = true;
    }

    private void BackgroundHotkeysToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = BackgroundHotkeysToggle.IsChecked == true;
        App.Hook.Enabled = enabled;
        if (!enabled && _hornHeldByKeyboard) { _hornHeldByKeyboard = false; App.Engine.HornUp(); }
        BackgroundHotkeysText.Text = enabled ? "🟢 BACKGROUND HOTKEYS ON\nSpace · 1-4 · H" : "BACKGROUND HOTKEYS OFF\nCLICK TO ENABLE";
        BackgroundHotkeysToggle.Background = enabled
            ? (Brush)FindResource("HornGradient")
            : (Brush)FindResource("PanelGradient");
    }

    // --- master volume (C-41) -------------------------------------------------------

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeSaveTimer is null) return; // during InitializeComponent
        App.Engine.MasterVolume = VolumeSlider.Value / 100.0; // % shown on the thumb itself
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start(); // persist once the dragging settles
    }

    // --- speaker (C-32) ----------------------------------------------------------

    private void SpeakerButton_Click(object sender, RoutedEventArgs e)
    {
        var next = App.Engine.Speaker switch
        {
            SpeakerMode.StreamOnly => SpeakerMode.PlaybackAndStream,
            SpeakerMode.PlaybackAndStream => SpeakerMode.PlaybackOnly,
            _ => SpeakerMode.StreamOnly,
        };
        App.Engine.Speaker = next;
        App.Lib.SetSpeakerMode(Config.SpeakerName(next)); // persisted (C-32)
        UpdateSpeakerButton();
    }

    private void UpdateSpeakerButton()
    {
        SpeakerText.Text = App.Engine.Speaker switch
        {
            SpeakerMode.PlaybackAndStream => "🔊 PLAYBACK + STREAM",
            SpeakerMode.PlaybackOnly => "💻 PLAYBACK ONLY",
            _ => "📡 STREAM ONLY",
        };
    }

    /// <summary>C-40: right-click the Speaker button — pick the playback device.</summary>
    private void SpeakerButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = SpeakerButton.ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = "PLAYBACK DEVICE", IsEnabled = false, FontSize = 14 });
        menu.Items.Add(new Separator());

        var current = App.Engine.OutputDeviceId;
        void AddChoice(string? id, string name)
        {
            bool selected = id == current;
            var item = new MenuItem { Header = $"{(selected ? "✓" : "    ")}  {name}" };
            item.Click += (_, _) =>
            {
                App.Engine.SetOutputDevice(id);   // re-binds live, booth unaffected
                App.Lib.SetOutputDevice(id ?? ""); // persisted (C-40)
            };
            menu.Items.Add(item);
        }

        AddChoice(null, "System default (follows Windows)");
        foreach (var (id, name) in AudioEngine.EnumerateOutputDevices())
            AddChoice(id, name);
    }

    // --- booth scan (C-34) ---------------------------------------------------------

    /// <summary>C-34 automatic discovery: runs only while disconnected; adopts and
    /// persists a live booth silently. Manual SCAN (with confirm) stays available.</summary>
    private async Task AutoScanAsync()
    {
        if (_autoScanBusy || App.Engine.BoothConnected) return;
        _autoScanBusy = true;
        try
        {
            var found = await BoothDiscovery.ScanAsync(App.Cfg.BoothPort, TimeSpan.FromMilliseconds(400));
            if (App.Engine.BoothConnected || found.Count == 0) return; // reconnected meanwhile / nothing there
            // Prefer a host that isn't the already-failing configured address.
            var pick = found.FirstOrDefault(ip => ip != App.Cfg.BoothIp) ?? found[0];
            if (pick == App.Cfg.BoothIp) return; // it IS the configured one; the pusher will get it
            App.Cfg.BoothIp = pick;
            try { App.Cfg.Save(AppContext.BaseDirectory); } catch { }
            App.Engine.SetBoothTarget(pick, App.Cfg.BoothPort);
            ConnText.Text = $"auto-scan found booth {pick} — connecting…";
        }
        catch { /* discovery is best-effort; next tick retries */ }
        finally
        {
            _autoScanBusy = false;
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ConnText.Text = "scanning subnet for booth (:4953)…";
        try
        {
            var found = await BoothDiscovery.ScanAsync(App.Cfg.BoothPort, TimeSpan.FromMilliseconds(400));
            if (found.Count == 0)
            {
                ConnText.Text = "scan: no booth found on this network";
                return;
            }
            var pick = found[0];
            var answer = MessageBox.Show(this,
                found.Count == 1
                    ? $"Found a booth at {pick}. Use it?"
                    : $"Found {found.Count} booths ({string.Join(", ", found)}). Use {pick}?",
                "Booth scan", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                App.Cfg.BoothIp = pick;
                try { App.Cfg.Save(AppContext.BaseDirectory); } catch { }
                App.Engine.SetBoothTarget(pick, App.Cfg.BoothPort);
            }
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    // --- status ---------------------------------------------------------------------

    private void RefreshStatus()
    {
        var s = App.Engine.Snapshot();
        var dotBrush = (SolidColorBrush)FindResource(s.BoothConnected ? "PlayingBrush" : "WarnBrush");
        ConnDot.Fill = dotBrush;
        _connGlow.Color = dotBrush.Color;
        ConnText.Text = s.BoothStatus;

        if (s.HornActive)
        {
            NowPlayingText.Text = s.IsPlaying ? $"📢 HORN + {s.NowPlayingLabel}" : "📢 HORN";
            RemainingText.Text = "";
        }
        else if (s.IsPlaying)
        {
            NowPlayingText.Text = $"▶ {s.NowPlayingLabel}";
            RemainingText.Text = $"-{TimeSpan.FromSeconds(s.RemainingSeconds):m\\:ss}";
        }
        else
        {
            NowPlayingText.Text = s.Mode == 4 ? "SILENCE" : "READY";
            RemainingText.Text = "";
        }
        Soundboard.RefreshFromSnapshot(s);
    }

    // --- spectrum analyzer (C-38) ------------------------------------------------------

    private void BuildEqBars()
    {
        EqCanvas.Children.Clear();
        double w = EqCanvas.ActualWidth, h = EqCanvas.ActualHeight;
        if (w < 10 || h < 10) return;
        double slot = w / EqBands;
        var barBrush = (Brush)FindResource("EqBarGradient");
        for (int i = 0; i < EqBands; i++)
        {
            var bar = new Rectangle
            {
                Width = Math.Max(2, slot - 5),
                Height = 2,
                RadiusX = 2,
                RadiusY = 2,
                Fill = barBrush,
            };
            Canvas.SetLeft(bar, i * slot + 2);
            Canvas.SetTop(bar, h - 2);
            EqCanvas.Children.Add(bar);
            _eqBars[i] = bar;

            var cap = new Rectangle
            {
                Width = Math.Max(2, slot - 5),
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(230, 125, 227, 255)),
            };
            Canvas.SetLeft(cap, i * slot + 2);
            Canvas.SetTop(cap, h - 5);
            EqCanvas.Children.Add(cap);
            _eqCaps[i] = cap;
        }
    }

    private void RefreshSpectrum()
    {
        if (_eqBars[0] is null) return;
        double h = EqCanvas.ActualHeight;
        if (h < 10) return;
        var bands = App.Engine.GetSpectrum(EqBands);
        for (int i = 0; i < EqBands; i++)
        {
            // fast attack, smooth release
            _eqShown[i] = bands[i] > _eqShown[i]
                ? bands[i]
                : _eqShown[i] * 0.78;
            // peak-hold caps with slow decay
            _eqPeaks[i] = Math.Max(_eqPeaks[i] - 0.022, _eqShown[i]);

            double bh = Math.Max(2, _eqShown[i] * (h - 6));
            _eqBars[i].Height = bh;
            Canvas.SetTop(_eqBars[i], h - bh);
            Canvas.SetTop(_eqCaps[i], Math.Max(0, h - _eqPeaks[i] * (h - 6) - 5));
        }
    }
}
