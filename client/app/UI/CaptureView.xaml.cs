// rgas-source capture tab — SPEC C-22 (loopback record), C-23 (import),
// C-24 (trim + local-only preview), C-25 (normalize on save), C-26 (assign,
// inline new collection), C-27 (volunteer-visible), C-28 (recording mutes
// local render and disables preview), C-39 (library context menu).
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using RgasSoundboard.Audio;
using RgasSoundboard.Library;

namespace RgasSoundboard.UI;

public partial class CaptureView : UserControl
{
    private AudioEngine _engine = null!;
    private LibraryStore _lib = null!;
    private Config _cfg = null!;
    private readonly LoopbackRecorder _recorder = new();
    private readonly PreviewPlayer _preview = new();
    private readonly DispatcherTimer _recTimer;

    private float[] _take = Array.Empty<float>(); // stereo 48 kHz, pre-normalization
    private double _takeSeconds;

    private sealed record ClipRow(string Id, string Label, string Duration, string Collections);

    public CaptureView()
    {
        InitializeComponent();
        _recTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _recTimer.Tick += (_, _) =>
        {
            if (_recorder.IsRecording)
                RecStatus.Text = $"recording… {_recorder.Elapsed:m\\:ss} — stop when you've got enough";
        };
    }

    public void Init(AudioEngine engine, LibraryStore lib, Config cfg)
    {
        _engine = engine;
        _lib = lib;
        _cfg = cfg;
        _lib.Changed += () => Dispatcher.BeginInvoke(RefreshLibraryViews);
        RefreshLibraryViews();
    }

    // --- ① source: record / import ---------------------------------------------------

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            var take = _recorder.Stop();
            _engine.LocalMuted = false; // C-28
            _recTimer.Stop();
            RecordText.Text = "●  RECORD WHAT'S PLAYING";
            BtnRecord.Background = (Brush)FindResource("PanelGradient");
            BtnPreview.IsEnabled = true;
            if (take.Length == 0)
            {
                RecStatus.Text = "nothing captured — was anything playing?";
                return;
            }
            LoadTake(take, "recording");
        }
        else
        {
            _preview.Stop();
            _engine.LocalMuted = true; // C-28: never record ourselves
            BtnPreview.IsEnabled = false;
            try
            {
                _recorder.Start();
            }
            catch (Exception ex)
            {
                _engine.LocalMuted = false;
                BtnPreview.IsEnabled = true;
                RecStatus.Text = $"capture failed: {ex.Message}";
                return;
            }
            RecordText.Text = "◼  STOP RECORDING";
            BtnRecord.Background = (Brush)FindResource("HornGradient");
            _recTimer.Start();
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Audio|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.wma;*.ogg|All files|*.*", // C-23
            Title = "Import audio",
        };
        if (dialog.ShowDialog() == true) ImportFile(dialog.FileName);
    }

    private void View_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void View_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            ImportFile(files[0]); // C-23: drag-and-drop
    }

    private async void ImportFile(string path)
    {
        RecStatus.Text = $"importing {System.IO.Path.GetFileName(path)}…";
        BtnImport.IsEnabled = false;
        try
        {
            var take = await Task.Run(() => Normalizer.DecodeFile(path));
            LoadTake(take, System.IO.Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex)
        {
            RecStatus.Text = $"import failed: {ex.Message}";
        }
        finally
        {
            BtnImport.IsEnabled = true;
        }
    }

    private void LoadTake(float[] take, string suggestedLabel)
    {
        _take = take;
        _takeSeconds = take.Length / 2.0 / Normalizer.SampleRate;
        RecStatus.Text = $"got {_takeSeconds:0.0} s — now trim it below (②), then save (③)";
        if (string.IsNullOrWhiteSpace(TxtLabel.Text)) TxtLabel.Text = suggestedLabel;
        SlStart.Maximum = _takeSeconds;
        SlEnd.Maximum = _takeSeconds;
        SlStart.Value = 0;
        SlEnd.Value = _takeSeconds;
        WavePlaceholder.Visibility = Visibility.Collapsed;
        DrawWave();
    }

    // --- ② trim + preview (C-24) -----------------------------------------------------

    private void Trim_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SlStart is null || SlEnd is null || TxtStart is null) return; // during InitializeComponent
        if (SlEnd.Value < SlStart.Value)
        {
            if (ReferenceEquals(sender, SlStart)) SlEnd.Value = SlStart.Value;
            else SlStart.Value = SlEnd.Value;
        }
        TxtStart.Text = $"START {FormatTime(SlStart.Value)}";
        TxtEnd.Text = $"END {FormatTime(SlEnd.Value)}";
        DrawTrimShading();
    }

    private static string FormatTime(double seconds) =>
        $"{(int)seconds / 60}:{seconds % 60:00.0}";

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_take.Length == 0 || _recorder.IsRecording) return; // C-28
        _preview.Play(_take,
            (int)(SlStart.Value * Normalizer.SampleRate),
            (int)(SlEnd.Value * Normalizer.SampleRate));
    }

    private void StopPreview_Click(object sender, RoutedEventArgs e) => _preview.Stop();

    // --- waveform --------------------------------------------------------------------

    private void DrawWave()
    {
        WaveCanvas.Children.Clear();
        if (_take.Length == 0) return;
        double width = WaveCanvas.Width, height = WaveCanvas.Height, mid = height / 2;
        int frames = _take.Length / 2;
        int columns = (int)width;
        var points = new PointCollection();
        var bottom = new List<Point>(columns);
        for (int x = 0; x < columns; x++)
        {
            long from = (long)x * frames / columns, to = Math.Max(from + 1, (long)(x + 1) * frames / columns);
            float min = 0, max = 0;
            for (long f = from; f < to; f++)
            {
                float v = (_take[f * 2] + _take[f * 2 + 1]) * 0.5f;
                if (v > max) max = v;
                if (v < min) min = v;
            }
            points.Add(new Point(x, mid - max * (mid - 4)));
            bottom.Add(new Point(x, mid - min * (mid - 4)));
        }
        for (int i = bottom.Count - 1; i >= 0; i--) points.Add(bottom[i]);
        WaveCanvas.Children.Add(new Polygon
        {
            Points = points,
            Fill = (Brush)FindResource("AccentGradient"),
            Opacity = 0.9,
        });
        DrawTrimShading();
    }

    private void DrawTrimShading()
    {
        if (WaveCanvas is null || _takeSeconds <= 0) return;
        for (int i = WaveCanvas.Children.Count - 1; i >= 0; i--)
            if (WaveCanvas.Children[i] is Rectangle) WaveCanvas.Children.RemoveAt(i);
        double width = WaveCanvas.Width, height = WaveCanvas.Height;
        double left = SlStart.Value / _takeSeconds * width;
        double right = SlEnd.Value / _takeSeconds * width;
        var shade = new SolidColorBrush(Color.FromArgb(190, 8, 11, 16));
        var l = new Rectangle { Width = Math.Max(0, left), Height = height, Fill = shade };
        Canvas.SetLeft(l, 0);
        var r = new Rectangle { Width = Math.Max(0, width - right), Height = height, Fill = shade };
        Canvas.SetLeft(r, right);
        WaveCanvas.Children.Add(l);
        WaveCanvas.Children.Add(r);
    }

    // --- ③ save + assign (C-25/C-26) ---------------------------------------------------

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_take.Length == 0) { SaveStatus.Text = "record or import something first (①)"; return; }
        var label = TxtLabel.Text.Trim();
        if (label.Length == 0) { SaveStatus.Text = "give the clip a name first"; TxtLabel.Focus(); return; }
        var collectionIds = CheckedCollectionIds();
        if (collectionIds.Count == 0) { SaveStatus.Text = "tick at least one collection (or create one below)"; return; }

        int startFrame = (int)(SlStart.Value * Normalizer.SampleRate);
        int endFrame = (int)(SlEnd.Value * Normalizer.SampleRate);
        if (endFrame - startFrame < Normalizer.SampleRate / 10) { SaveStatus.Text = "selection is too short"; return; }

        BtnSave.IsEnabled = false;
        SaveStatus.Text = "normalizing…";
        try
        {
            double target = _cfg.MusicTargetLufs;
            var lib = _lib;
            var clipSeconds = await Task.Run(() =>
            {
                var slice = new float[(endFrame - startFrame) * 2];
                Array.Copy(_take, startFrame * 2, slice, 0, slice.Length);
                Normalizer.NormalizeInPlace(slice, target); // C-25
                var fileName = $"clip-{Guid.NewGuid():N}.wav";
                Normalizer.EncodeWav16(System.IO.Path.Combine(lib.AudioDir, fileName), slice);
                double seconds = slice.Length / 2.0 / Normalizer.SampleRate;
                lib.AddClip(label, fileName, seconds, collectionIds);
                return seconds;
            });
            SaveStatus.Text = $"saved “{label}” ({clipSeconds:0.0} s) ✓ — it's on the soundboard now";
            TxtLabel.Text = "";
        }
        catch (Exception ex)
        {
            SaveStatus.Text = $"save failed: {ex.Message}";
        }
        finally
        {
            BtnSave.IsEnabled = true;
        }
    }

    private void AddCollection_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtNewCol.Text.Trim();
        if (name.Length == 0) { SaveStatus.Text = "type a name for the new collection first"; TxtNewCol.Focus(); return; }
        int mode = CmbNewColMode.SelectedIndex + 1; // C-16: exactly one mode
        var col = _lib.AddCollection(name, mode);
        TxtNewCol.Text = "";
        RefreshLibraryViews();
        foreach (var child in PanelAssign.Children)
            if (child is CheckBox { Tag: string id } cb && id == col.Id) cb.IsChecked = true;
    }

    private List<string> CheckedCollectionIds() =>
        PanelAssign.Children.OfType<CheckBox>()
            .Where(cb => cb.IsChecked == true)
            .Select(cb => (string)cb.Tag)
            .ToList();

    // --- library management (C-39): right-click menu ------------------------------------

    private void Library_RightDown(object sender, MouseButtonEventArgs e)
    {
        // Make right-click select the row under the cursor before the menu opens.
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not ListBoxItem) dep = VisualTreeHelper.GetParent(dep);
        if (dep is ListBoxItem item) item.IsSelected = true;
    }

    private void Library_MenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (LvLibrary.SelectedItem is not ClipRow row) { e.Handled = true; return; }
        var clip = _lib.Clips.FirstOrDefault(c => c.Id == row.Id);
        if (clip is null) { e.Handled = true; return; }

        var menu = LvLibrary.ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = $"“{row.Label}”  — collections:", IsEnabled = false });
        foreach (var col in _lib.Collections.OrderBy(c => c.Mode).ThenBy(c => c.Name))
        {
            bool member = clip.Collections.Contains(col.Id);
            var mi = new MenuItem { Header = $"{(member ? "✓" : "　")}  {col.Name}   (mode {col.Mode})" };
            string clipId = clip.Id, colId = col.Id;
            mi.Click += (_, _) => _lib.ToggleMembership(clipId, colId); // C-39
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "🗑 Delete clip from library…" };
        delete.Click += (_, _) =>
        {
            if (MessageBox.Show($"Delete “{row.Label}” from the library? This removes the audio file.",
                    "Delete clip", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                _lib.DeleteClip(row.Id);
        };
        menu.Items.Add(delete);
    }

    private void RefreshLibraryViews()
    {
        // ③ assign checkboxes for the NEW clip (kept across refreshes)
        var checkedBefore = CheckedCollectionIds().ToHashSet();
        PanelAssign.Children.Clear();
        foreach (var col in _lib.Collections.OrderBy(c => c.Mode).ThenBy(c => c.Name))
        {
            PanelAssign.Children.Add(new CheckBox
            {
                Content = $"{col.Name}  ·  mode {col.Mode}",
                Tag = col.Id,
                IsChecked = checkedBefore.Contains(col.Id),
                Foreground = (Brush)FindResource("TextBrush"),
                FontSize = 18,
                Margin = new Thickness(4, 4, 26, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
            });
        }
        if (PanelAssign.Children.Count == 0)
        {
            PanelAssign.Children.Add(new TextBlock
            {
                Text = "no collections yet — create your first one below ↓",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 18,
                Margin = new Thickness(4),
            });
        }

        // library list
        var byId = _lib.Collections.ToDictionary(c => c.Id, c => c.Name);
        LvLibrary.ItemsSource = _lib.Clips
            .OrderBy(c => c.Label)
            .Select(c => new ClipRow(
                c.Id, c.Label,
                TimeSpan.FromSeconds(c.DurationSeconds).ToString(@"m\:ss"),
                string.Join("  ·  ", c.Collections.Where(byId.ContainsKey).Select(id => byId[id]))))
            .ToList();
    }
}
