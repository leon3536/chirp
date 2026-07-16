// rgas-source announcer popup — SPEC C-42 (entry, personalities, tone badge,
// history replay), C-43 (graceful key onboarding, inline error surfacing).
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RgasSoundboard.Audio;
using RgasSoundboard.Library;

namespace RgasSoundboard.UI;

public partial class AnnouncerDialog : Window
{
    private readonly AnnouncerService _service;
    private readonly AudioEngine _engine;
    private readonly Config _cfg;
    private readonly LibraryStore _lib;
    private AnnouncerService.Personality _personality;
    private bool _busy;

    public AnnouncerDialog(AnnouncerService service, AudioEngine engine, Config cfg, LibraryStore lib)
    {
        _service = service;
        _engine = engine;
        _cfg = cfg;
        _lib = lib;
        _personality = AnnouncerService.GetPersonality(lib.GetAnnouncerPersonality()); // persisted (C-42)
        InitializeComponent();
        BuildPersonalityButtons();
        ShowPanel(keyEntry: !_service.HasKey); // C-43: friendly onboarding, not an error
        RefreshHistory(); // C-42: takes survive auto-close (service-held)
        Loaded += (_, _) => (_service.HasKey ? (UIElement)TxtAnnounce : PbKey).Focus();
    }

    private void RefreshHistory() => LbHistory.ItemsSource = _service.History;

    // --- personalities (C-42): touch buttons with indicator lights --------------------

    private void BuildPersonalityButtons()
    {
        PanelPersonalities.Children.Clear();
        foreach (var personality in AnnouncerService.Personalities)
        {
            var light = new Ellipse
            {
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(light);
            row.Children.Add(new TextBlock { Text = $"{personality.Emoji} {personality.Label}", FontSize = 18 });
            var button = new ToggleButton
            {
                Style = (Style)FindResource("BigButton"),
                MinHeight = 56,
                Margin = new Thickness(0, 0, personality == AnnouncerService.Personalities[^1] ? 0 : 12, 0),
                Content = row,
                Tag = (personality, light),
            };
            button.Click += Personality_Click;
            PanelPersonalities.Children.Add(button);
        }
        RefreshPersonalityLights();
    }

    private void Personality_Click(object sender, RoutedEventArgs e)
    {
        var (personality, _) = ((AnnouncerService.Personality, Ellipse))((ToggleButton)sender).Tag;
        _personality = personality;
        _lib.SetAnnouncerPersonality(personality.Id); // persisted (C-42)
        RefreshPersonalityLights();
        TxtAnnounce.Focus();
    }

    private void RefreshPersonalityLights()
    {
        var on = (Brush)FindResource("PlayingBrush");
        var off = new SolidColorBrush(Color.FromRgb(0x33, 0x3F, 0x4F));
        var active = (Brush)FindResource("AccentGradient");
        var normal = (Brush)FindResource("PanelGradient");
        foreach (ToggleButton button in PanelPersonalities.Children)
        {
            var (personality, light) = ((AnnouncerService.Personality, Ellipse))button.Tag;
            bool selected = personality.Id == _personality.Id;
            button.IsChecked = selected;
            button.Background = selected ? active : normal;
            light.Fill = selected ? on : off;
        }
    }

    private void ShowPanel(bool keyEntry)
    {
        KeyPanel.Visibility = keyEntry ? Visibility.Visible : Visibility.Collapsed;
        EntryPanel.Visibility = keyEntry ? Visibility.Collapsed : Visibility.Visible;
    }

    // --- key onboarding (C-43) -----------------------------------------------------

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = PbKey.Password.Trim();
        if (key.Length < 20 || !(key.StartsWith("sk-") || key.StartsWith("sk_")))
        {
            KeyStatus.Text = "That doesn't look like an API key — expected sk-… (OpenAI) or sk_… (ElevenLabs).";
            return;
        }
        _cfg.SetAnnouncerApiKey(key);
        try { _cfg.Save(AppContext.BaseDirectory); }
        catch (Exception ex) { KeyStatus.Text = $"Could not save config: {ex.Message}"; return; }
        PbKey.Clear();
        KeyStatus.Text = "";
        ShowPanel(keyEntry: false);
        StatusText.Text = "Key saved (encrypted). Type an announcement.";
        TxtAnnounce.Focus();
    }

    private void ChangeKey_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(keyEntry: true);
        PbKey.Focus();
    }

    // --- entry (C-42) -----------------------------------------------------------------

    private void TxtAnnounce_TextChanged(object sender, TextChangedEventArgs e)
    {
        var (script, directions) = AnnouncerService.ParseDirections(TxtAnnounce.Text);
        bool dramatic = AnnouncerService.IsDramatic(script); // spoken words only (C-42)
        ToneBadge.Text = (dramatic ? "🔥 DRAMATIC delivery" : "· plain delivery")
            + (directions is null ? "" : "  ·  🎭 directed");
        ToneBadge.Foreground = dramatic
            ? (Brush)FindResource("HornBrush")
            : (Brush)FindResource("MutedBrush");
    }

    private void TxtAnnounce_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = AnnounceAsync(); }
    }

    private void Announce_Click(object sender, RoutedEventArgs e) => _ = AnnounceAsync();

    private async Task AnnounceAsync()
    {
        if (_busy) return;
        var text = TxtAnnounce.Text.Trim();
        if (text.Length == 0) { StatusText.Text = "Type something to announce."; return; }

        _busy = true;
        BtnAnnounce.IsEnabled = false;
        bool dramatic = AnnouncerService.IsDramatic(text);
        AnnounceText.Text = "🎙 PERFORMING…";
        StatusText.Foreground = (Brush)FindResource("MutedBrush");
        StatusText.Text = dramatic ? "Synthesizing a dramatic call…" : "Synthesizing…";
        try
        {
            // Task.Run: the decode + loudness pass is CPU-heavy — keep it off the
            // UI thread so it can't pile onto the render thread's timing.
            var samples = await Task.Run(() => _service.SynthesizeAsync(text, _personality));
            _engine.PlayAnnouncement(samples); // C-42: over ducked music, to booth + local
            _service.RecordTake(text, dramatic, samples);
            Close(); // C-42: it's on air — return the operator to the board
            return;
        }
        catch (Exception ex)
        {
            // C-43: failures stay inside the popup, never break the soundboard
            StatusText.Foreground = (Brush)FindResource("WarnBrush");
            StatusText.Text = ex.Message;
            BtnChangeKey.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            BtnAnnounce.IsEnabled = true;
            AnnounceText.Text = "🎙 ANNOUNCE  (Enter)";
        }
    }

    private void History_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // ClipItem rows are Focusable=False, so ListBox selection never happens —
        // resolve the tapped row directly from the visual tree instead.
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null && dep is not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is ListBoxItem { DataContext: AnnouncerService.Take take })
        {
            _engine.PlayAnnouncement(take.Samples); // cached: instant, free
            Close(); // same on-air auto-close as a fresh announcement
        }
    }

    // --- window ------------------------------------------------------------------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
