// rgas-booth dashboard — SPEC S-16 (status window) + S-17 (telemetry).
// A read-only viewer over ReceiverCore. Visual method per the dataviz skill:
// severity meters (fill + track from the same ramp), sentence-case labels,
// status colors validated against the card surface, peak-hold ticks, a
// de-emphasized buffer sparkline, and a breathing glow only when ON AIR.
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RgasReceiver;

public partial class MainWindow : Window
{
    private const double HoldSeconds = 1.5;     // peak-hold persistence
    private const float WarnLevel = 0.501f;     // -6 dBFS: fill switches to warning
    private const float HotLevel = 0.841f;      // -1.5 dBFS: fill switches to critical
    private const int SparkCapacity = 90;       // 90 × 0.5 s = 45 s of buffer history

    private readonly ReceiverCore _core;
    private readonly DispatcherTimer _timer;
    private readonly Queue<double> _fillHistory = new();
    private readonly List<string> _logLines = new();
    private int _historyVersion = -1;
    private int _tick;
    private double _holdL, _holdR;
    private DateTime _holdUntilL, _holdUntilR;
    private BufferState _lastState = (BufferState)(-1);

    private static readonly Color GoodC = Color.FromRgb(0x0C, 0xA3, 0x0C);
    private static readonly Color WarningC = Color.FromRgb(0xFA, 0xB2, 0x19);
    private static readonly Color AccentC = Color.FromRgb(0x39, 0x87, 0xE5);

    public MainWindow(ReceiverCore core)
    {
        _core = core;
        InitializeComponent();

        PortText.Text = $"tcp :{core.Cfg.ListenPort}";
        DeviceText.Text = $"output — {core.Player.DeviceName}";
        StatusUrlText.Text = $"http://{LocalIp()}:{core.Cfg.StatusPort}/status";

        foreach (var line in Log.Recent()) _logLines.Add(line);
        RenderLog();
        Log.LineWritten += line => Dispatcher.BeginInvoke(() => AppendLog(line));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    // --- refresh ------------------------------------------------------------------

    private void Refresh()
    {
        var buffer = _core.Buffer;
        var state = buffer.State;
        bool sourceUp = buffer.SourceConnected;

        // S-18: a port-bind fault shows here, never as a modal dialog
        if (_core.Fault is string fault)
        {
            StateText.Text = "Port in use";
            StateSubText.Text = fault;
            var crit = (SolidColorBrush)FindResource("CriticalBrush");
            StateDot.Fill = crit; TitleDot.Fill = crit; HeroWash.Background = crit;
            ((DropShadowEffect)StateDot.Effect).Color = crit.Color;
            _lastState = (BufferState)(-1); // force a repaint when the fault clears
            return;
        }

        if (state != _lastState)
        {
            _lastState = state;
            ApplyState(state, sourceUp);
        }
        if (state != BufferState.Waiting)
            StateSubText.Text = sourceUp ? $"from {_core.SourceAddress}" : "draining";

        // indicator lights (S-17c)
        SetLed(SourceLed, sourceUp, GoodC);
        SetLed(SignalLed, buffer.SignalActive, AccentC);

        // buffer gauge: scale is 2x target so the target tick sits mid-track
        double track = BufferTrack.ActualWidth;
        if (track > 0)
        {
            double frac = Math.Min(1.0, buffer.FillMs / (2.0 * _core.Cfg.BufferMs));
            BufferFill.Width = frac * track;
            BufferTick.Margin = new Thickness(track * 0.5, -3, 0, -3);
        }
        BufferText.Text = $"{buffer.FillMs} / {_core.Cfg.BufferMs} ms";

        // sparkline: push every 5th tick (0.5 s), 45 s window
        if (++_tick % 5 == 0)
        {
            _fillHistory.Enqueue(buffer.FillMs);
            while (_fillHistory.Count > SparkCapacity) _fillHistory.Dequeue();
            RenderSparkline();
        }

        // level meters: severity fill on a same-ramp track + peak-hold tick
        UpdateMeter(buffer.PeakL, MeterLTrack, MeterLFill, MeterLBack, MeterLHold, MeterLText,
                    ref _holdL, ref _holdUntilL);
        UpdateMeter(buffer.PeakR, MeterRTrack, MeterRFill, MeterRBack, MeterRHold, MeterRText,
                    ref _holdR, ref _holdUntilR);

        // footer: device can change at runtime (S-6a)
        DeviceText.Text = $"output — {_core.Player.DeviceName}";

        // tiles (S-17a/b)
        ConnCountText.Text = _core.TotalConnections.ToString();
        BiteCountText.Text = buffer.SoundBites.ToString();
        UnderrunText.Text = buffer.Underruns.ToString();
        // delta semantics: a non-zero underrun count is attention-worthy
        UnderrunText.Foreground = buffer.Underruns > 0
            ? (Brush)FindResource("WarningBrush") : (Brush)FindResource("InkPrimaryBrush");
        var up = _core.Uptime;
        UptimeText.Text = $"{(int)up.TotalHours}:{up.Minutes:00}:{up.Seconds:00}";

        // connection history (S-17a), rebuilt only when it changed
        if (_core.HistoryVersion != _historyVersion)
        {
            _historyVersion = _core.HistoryVersion;
            ConnList.Items.Clear();
            foreach (var ev in _core.History())
                ConnList.Items.Add(ConnRow(ev));
        }
    }

    private void ApplyState(BufferState state, bool sourceUp)
    {
        (string title, string sub, string brushKey) = state switch
        {
            BufferState.Playing => ("On air", $"from {_core.SourceAddress}", "GoodBrush"),
            BufferState.Filling => ("Buffering", sourceUp ? $"from {_core.SourceAddress}" : "draining", "AccentBrush"),
            _ => ("Waiting for rinkside", "no source connected", "WarningBrush"),
        };
        StateText.Text = title;
        StateSubText.Text = sub;
        var brush = (SolidColorBrush)FindResource(brushKey);
        StateDot.Fill = brush;
        TitleDot.Fill = brush;
        HeroWash.Background = brush;
        ((DropShadowEffect)StateDot.Effect).Color = brush.Color;

        // breathe only when ON AIR; steady otherwise
        var glow = (DropShadowEffect)StateDot.Effect;
        if (state == BufferState.Playing)
        {
            var pulse = new DoubleAnimation(0.45, 1.0, TimeSpan.FromSeconds(1.4))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
        }
        else
        {
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            glow.Opacity = 0.85;
        }
    }

    private void UpdateMeter(float peak, Grid trackGrid, Border fill, Border back,
                             Rectangle hold, TextBlock text, ref double holdVal, ref DateTime holdUntil)
    {
        // severity: the fill (and its track, same ramp) switch state wholesale
        string fillKey = peak >= HotLevel ? "CriticalBrush" : peak >= WarnLevel ? "WarningBrush" : "GoodBrush";
        string trackKey = peak >= HotLevel ? "CriticalTrackBrush" : peak >= WarnLevel ? "WarningTrackBrush" : "GoodTrackBrush";
        fill.Background = (Brush)FindResource(fillKey);
        back.Background = (Brush)FindResource(trackKey);

        double track = trackGrid.ActualWidth;
        double frac = Math.Sqrt(Math.Min(1f, peak)); // perceptual-ish
        if (track > 0) fill.Width = frac * track;

        // peak-hold: capture the max, hold 1.5 s, then decay
        var now = DateTime.UtcNow;
        if (frac >= holdVal) { holdVal = frac; holdUntil = now.AddSeconds(HoldSeconds); }
        else if (now > holdUntil) holdVal *= 0.94;
        bool showHold = holdVal > 0.02;
        hold.Visibility = showHold ? Visibility.Visible : Visibility.Collapsed;
        if (showHold && track > 2) hold.Margin = new Thickness(Math.Min(holdVal * track, track - 2), -2, 0, -2);

        text.Text = peak < 0.0001f ? "−∞ dB" : $"{20 * Math.Log10(peak):0.0} dB";
    }

    private void RenderSparkline()
    {
        if (_fillHistory.Count < 5) return; // wait for a real trace before drawing
        double w = 100, h = 22;
        double max = Math.Max(2.0 * _core.Cfg.BufferMs, _fillHistory.Max());
        var points = new PointCollection();
        int i = 0, n = _fillHistory.Count;
        foreach (var fill in _fillHistory)
        {
            double x = w * i++ / (SparkCapacity - 1);
            double y = h - (fill / max) * (h - 2) - 1;
            points.Add(new Point(x, y));
        }
        Sparkline.Points = points;
        var last = points[n - 1];
        SparkDot.Visibility = Visibility.Visible;
        SparkDot.Margin = new Thickness(last.X - 2.5, last.Y - 2.5, 0, 0);
    }

    private void SetLed(Ellipse led, bool on, Color color)
    {
        if (on)
        {
            led.Fill = new SolidColorBrush(color);
            led.Effect ??= new DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 };
            ((DropShadowEffect)led.Effect).Color = color;
        }
        else
        {
            led.Fill = (Brush)FindResource("LedOffBrush");
            led.Effect = null;
        }
    }

    private UIElement ConnRow(ConnEvent ev)
    {
        var (dotKey, kindLabel) = ev.Kind switch
        {
            "connected" => ("GoodBrush", "connected"),
            "replaced" => ("WarningBrush", "replaced"),
            _ => ("InkFaintBrush", "disconnected"),
        };
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Ellipse { Width = 7, Height = 7, Fill = (Brush)FindResource(dotKey), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(dot, 0);
        var time = Cell($"{ev.At:HH:mm:ss}", "InkMutedBrush");
        Grid.SetColumn(time, 1);
        var kind = Cell(kindLabel, "InkSecondaryBrush");
        Grid.SetColumn(kind, 2);
        var addr = Cell(ev.Address, "InkMutedBrush");
        Grid.SetColumn(addr, 3);

        grid.Children.Add(dot);
        grid.Children.Add(time);
        grid.Children.Add(kind);
        grid.Children.Add(addr);
        return grid;

        TextBlock Cell(string content, string brushKey) => new()
        {
            Text = content,
            Foreground = (Brush)FindResource(brushKey),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // --- log pane ---------------------------------------------------------------------

    private void AppendLog(string line)
    {
        _logLines.Add(line);
        if (_logLines.Count > 300) _logLines.RemoveAt(0);
        RenderLog();
    }

    private void RenderLog()
    {
        LogText.Inlines.Clear();
        foreach (var line in _logLines)
        {
            // "HH:mm:ss LEVEL message" — time muted, WARN token in warning color
            var timePart = line.Length >= 8 ? line[..8] : line;
            var rest = line.Length > 8 ? line[8..] : "";
            bool warn = rest.StartsWith(" WARN");
            LogText.Inlines.Add(new Run(timePart) { Foreground = (Brush)FindResource("InkFaintBrush") });
            if (warn)
            {
                LogText.Inlines.Add(new Run(" WARN") { Foreground = (Brush)FindResource("WarningBrush") });
                rest = rest[5..];
            }
            LogText.Inlines.Add(new Run(rest) { Foreground = (Brush)FindResource("InkSecondaryBrush") });
            LogText.Inlines.Add(new LineBreak());
        }
        LogScroll.ScrollToEnd();
    }

    // --- chrome -------------------------------------------------------------------------

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string LocalIp()
    {
        // Preferred: the source IP the OS would use to leave this box. No packets
        // are sent; it just resolves the route. Works without real internet as
        // long as a gateway exists.
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            s.Connect("8.8.8.8", 65530);
            var ip = ((IPEndPoint)s.LocalEndPoint!).Address;
            if (!IPAddress.IsLoopback(ip)) return ip.ToString();
        }
        catch { /* no route/gateway — fall through */ }

        // Fallback for a gateway-less isolated LAN (RGAS must work fully offline):
        // first up, non-loopback IPv4 on a real adapter.
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    var b = addr.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // skip APIPA link-local (dead adapters)
                    return addr.Address.ToString();
                }
            }
        }
        catch { /* fall through */ }

        return "127.0.0.1";
    }
}
