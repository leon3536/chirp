// rgas-source game-time surface — SPEC C-3, C-5, C-8..C-10, C-16..C-18, C-37.
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RgasSoundboard.Audio;
using RgasSoundboard.Library;

namespace RgasSoundboard.UI;

public partial class SoundboardView : UserControl
{
    private AudioEngine _engine = null!;
    private LibraryStore _lib = null!;
    private int _shownMode = -1;
    private string? _highlightedCollectionId;
    private List<ClipRowVM> _clipRows = new();
    private string _transientHint = "";
    private DateTime _transientHintUntil = DateTime.MinValue;

    /// <summary>Playlist row (C-37): IsPlaying drives the per-row play/stop toggle.</summary>
    public sealed class ClipRowVM : INotifyPropertyChanged
    {
        private bool _isPlaying;
        public required string Id { get; init; }
        public required string Label { get; init; }
        public required string Duration { get; init; }
        public bool IsPlaying
        {
            get => _isPlaying;
            set { if (_isPlaying != value) { _isPlaying = value; PropertyChanged?.Invoke(this, new(nameof(IsPlaying))); } }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public SoundboardView() => InitializeComponent();

    public void Init(AudioEngine engine, LibraryStore lib)
    {
        _engine = engine;
        _lib = lib;
        _lib.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_shownMode >= 1) { BuildCollectionList(_shownMode); BuildClipList(); }
        });
    }

    // --- actions -----------------------------------------------------------------

    private void Mode_Click(object sender, RoutedEventArgs e) =>
        _engine.SetMode(int.Parse((string)((Button)sender).Tag));

    private void Play_Click(object sender, RoutedEventArgs e) => _engine.ToggleSpace();

    private void Skip_Click(object sender, RoutedEventArgs e) => _engine.Skip();

    private void Order_Click(object sender, RoutedEventArgs e)
    {
        var s = _engine.Snapshot();
        int mode = s.Mode is >= 1 and <= 3 ? s.Mode : 1;
        _lib.SetOrder(mode, _lib.GetOrder(mode) == "shuffle" ? "sequential" : "shuffle"); // C-10
        UpdateOrderText(mode);
    }

    private void Announce_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.OpenAnnouncer(); // C-42

    private void OpenMic_Click(object sender, RoutedEventArgs e) => _engine.ToggleMic(); // C-45

    private void Horn_Down(object sender, MouseButtonEventArgs e) { _engine.HornDown(); BtnHorn.CaptureMouse(); }
    private void Horn_Up(object sender, RoutedEventArgs e) { _engine.HornUp(); BtnHorn.ReleaseMouseCapture(); }
    private void Horn_TouchDown(object sender, TouchEventArgs e) { _engine.HornDown(); e.Handled = true; }
    private void Horn_TouchUp(object sender, RoutedEventArgs e) { _engine.HornUp(); }

    // --- collection browser (C-37) --------------------------------------------------

    private void CollectionCheck_Click(object sender, RoutedEventArgs e)
    {
        var checkBox = (CheckBox)sender;
        var id = (string)checkBox.Tag;
        bool wanted = checkBox.IsChecked == true;
        if (!_lib.SetSelected(id, wanted)) // C-17: refuses to empty a populated mode
        {
            checkBox.IsChecked = true;
            ShowTransientHint("At least one collection must stay selected for this mode.");
        }
    }

    private void Collections_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LbCollections.SelectedItem is ListBoxItem { Tag: string id })
        {
            _highlightedCollectionId = id;
            BuildClipList();
        }
    }

    /// <summary>C-39: populate the "Move to collection" submenu with every other
    /// collection, grouped by mode, fresh from the library at open time.</summary>
    private void ClipMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        var moveItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag as string == "move");
        if (moveItem is null) return;
        var row = menu.DataContext as ClipRowVM
                  ?? (menu.PlacementTarget as FrameworkElement)?.DataContext as ClipRowVM;
        moveItem.Items.Clear();
        if (row is null || _highlightedCollectionId is null) { moveItem.IsEnabled = false; return; }
        var targets = _lib.Collections
            .Where(c => c.Id != _highlightedCollectionId)
            .OrderBy(c => c.Mode).ThenBy(c => c.Name)
            .ToList();
        moveItem.IsEnabled = targets.Count > 0;
        foreach (var col in targets)
        {
            var target = col;
            var child = new MenuItem { Header = $"mode {target.Mode} · {target.Name}" };
            child.Click += (_, _) => _lib.MoveClip(row.Id, _highlightedCollectionId!, target.Id);
            moveItem.Items.Add(child);
        }
    }

    /// <summary>C-39: clip row context menu — remove from the highlighted collection.</summary>
    private void ClipRemoveFromCollection_Click(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).DataContext is not ClipRowVM row) return;
        if (_highlightedCollectionId is null) return;
        _lib.ToggleMembership(row.Id, _highlightedCollectionId);
    }

    /// <summary>C-39: clip row context menu — delete the clip everywhere.</summary>
    private void ClipDelete_Click(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).DataContext is not ClipRowVM row) return;
        if (MessageBox.Show($"Delete “{row.Label}” from the library? This removes the audio file.",
                "Delete clip", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _lib.DeleteClip(row.Id);
    }

    /// <summary>C-37: per-row play button; toggles to stop while that clip plays.</summary>
    private void ClipPlay_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ClipRowVM row) return;
        if (row.IsPlaying)
        {
            _engine.Stop();
            return;
        }
        if (_engine.Snapshot().Mode == 4)
        {
            ShowTransientHint("Mode 4 is silence — switch to a mode first (keys 1-3).");
            return;
        }
        _engine.PlayClip(row.Id);
    }

    // --- rendering -----------------------------------------------------------------

    /// <summary>Called by MainWindow's UI timer with a fresh engine snapshot.</summary>
    public void RefreshFromSnapshot(EngineSnapshot s)
    {
        if (s.Mode != _shownMode)
        {
            _shownMode = s.Mode;
            HighlightModes(s.Mode);
            BuildCollectionList(s.Mode);
            BuildClipList();
            UpdateOrderText(s.Mode);
        }

        HornNameText.Text = s.ActiveHorn == "devil" ? "📢 DEVIL HORN" : "📢 STAR HORN"; // C-46

        PlayText.Text = s.IsPlaying ? "◼  STOP" : "▶  PLAY";
        BtnPlay.IsEnabled = s.Mode != 4;
        NowLabel.Text = s.IsPlaying ? s.NowPlayingLabel : (s.Mode == 4 ? "— silence —" : "—");
        TimeLeft.Text = s.IsPlaying ? $"-{TimeSpan.FromSeconds(s.RemainingSeconds):m\\:ss}" : "";
        Progress.Value = s.IsPlaying && s.DurationSeconds > 0
            ? 1 - s.RemainingSeconds / s.DurationSeconds
            : 0;
        NextUpText.Text = string.IsNullOrEmpty(s.NextLabel) ? "" : $"NEXT: {s.NextLabel}";
        BtnSkip.IsEnabled = !string.IsNullOrEmpty(s.NextLabel);

        OpenMicText.Text = s.MicOpen ? "🔴 MIC LIVE" : "🎤 OPEN MIC";
        BtnOpenMic.Background = s.MicOpen
            ? (Brush)FindResource("HornGradient")
            : (Brush)FindResource("PanelGradient");

        foreach (var row in _clipRows) // C-37 playlist toggles
            row.IsPlaying = s.IsPlaying && row.Id == s.NowPlayingClipId;

        if (DateTime.UtcNow < _transientHintUntil)
            HintText.Text = _transientHint;
        else if (s.Mode != 4 && s.PoolEmpty)
            HintText.Text = _lib.CollectionsForMode(s.Mode).Count == 0
                ? "No collections in this mode yet — add clips on the CAPTURE tab."
                : "Selected collections are empty — add clips on the CAPTURE tab.";
        else
            HintText.Text = "";
    }

    private void HighlightModes(int mode)
    {
        var active = (Brush)FindResource("AccentGradient");
        var normal = (Brush)FindResource("PanelGradient");
        BtnMode1.Background = mode == 1 ? active : normal;
        BtnMode2.Background = mode == 2 ? active : normal;
        BtnMode3.Background = mode == 3 ? active : normal;
        BtnMode4.Background = mode == 4 ? active : normal;
    }

    private void UpdateOrderText(int mode)
    {
        if (mode is < 1 or > 3) { BtnOrder.Visibility = Visibility.Hidden; return; }
        BtnOrder.Visibility = Visibility.Visible;
        OrderText.Text = _lib.GetOrder(mode) == "sequential" ? "▶▶ SEQUENTIAL" : "🔀 SHUFFLE";
    }

    private void BuildCollectionList(int mode)
    {
        LbCollections.Items.Clear();
        if (mode == 4)
        {
            CollectionsHeader.Text = "COLLECTIONS — MODE 4 IS SILENCE";
            return;
        }
        CollectionsHeader.Text = "COLLECTIONS — CHECK = ACTIVE IN THIS MODE";
        var clips = _lib.Clips;
        ListBoxItem? toSelect = null;
        foreach (var col in _lib.CollectionsForMode(mode))
        {
            var check = new CheckBox
            {
                IsChecked = col.Selected,
                Tag = col.Id,
                VerticalAlignment = VerticalAlignment.Center,
                LayoutTransform = new ScaleTransform(1.7, 1.7), // touch target (C-3)
            };
            check.Click += CollectionCheck_Click;
            int count = clips.Count(c => c.Collections.Contains(col.Id));
            var badge = new Border
            {
                Background = (Brush)FindResource("PanelLightBrush"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 3, 12, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = count == 1 ? "1 clip" : $"{count} clips",
                    FontSize = 16,
                    Foreground = (Brush)FindResource("MutedBrush"),
                },
            };
            DockPanel.SetDock(badge, Dock.Right);
            var name = new TextBlock
            {
                Text = col.Name,
                FontSize = 23,
                FontFamily = (FontFamily)FindResource("DisplayFont"),
                Margin = new Thickness(14, 0, 14, 0),
                Foreground = (Brush)FindResource("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var panel = new DockPanel();
            panel.Children.Add(check);
            panel.Children.Add(badge);
            panel.Children.Add(name);
            var item = new ListBoxItem { Content = panel, Tag = col.Id, MinHeight = 58 };
            // C-39: collection context menu
            var menu = new ContextMenu();
            var delete = new MenuItem { Header = $"🗑 Delete collection “{col.Name}”…" };
            string colId = col.Id;
            string colName = col.Name;
            delete.Click += (_, _) =>
            {
                if (MessageBox.Show(
                        $"Delete collection “{colName}”? Its clips stay in the library.",
                        "Delete collection", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    == MessageBoxResult.Yes)
                    _lib.DeleteCollection(colId);
            };
            menu.Items.Add(delete);
            item.ContextMenu = menu;
            LbCollections.Items.Add(item);
            if (col.Id == _highlightedCollectionId) toSelect = item;
        }
        LbCollections.SelectedItem = toSelect ?? (LbCollections.Items.Count > 0 ? LbCollections.Items[0] : null);
    }

    private void BuildClipList()
    {
        if (_highlightedCollectionId is null && LbCollections.SelectedItem is ListBoxItem { Tag: string id })
            _highlightedCollectionId = id;
        var col = _lib.Collections.FirstOrDefault(c => c.Id == _highlightedCollectionId);
        if (col is null || _shownMode == 4)
        {
            ClipsHeader.Text = "CLIPS";
            _clipRows = new List<ClipRowVM>();
            LvClips.ItemsSource = null;
            return;
        }
        ClipsHeader.Text = $"CLIPS IN “{col.Name.ToUpperInvariant()}”";
        var s = _engine.Snapshot();
        _clipRows = _lib.Clips
            .Where(c => c.Collections.Contains(col.Id))
            .OrderBy(c => c.Label)
            .Select(c => new ClipRowVM
            {
                Id = c.Id,
                Label = c.Label,
                Duration = TimeSpan.FromSeconds(c.DurationSeconds).ToString(@"m\:ss"),
                IsPlaying = s.IsPlaying && c.Id == s.NowPlayingClipId,
            })
            .ToList();
        LvClips.ItemsSource = _clipRows;
    }

    private void ShowTransientHint(string text)
    {
        _transientHint = text;
        _transientHintUntil = DateTime.UtcNow.AddSeconds(4);
        HintText.Text = text;
    }
}
