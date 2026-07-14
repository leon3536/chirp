// rgas-source library — SPEC C-16..C-19, C-21.
// library/ next to the exe: 48 kHz 16-bit stereo WAVs + library.json holding
// clips, collections (each owned by exactly one mode 1-3), memberships,
// selected-state, and the per-mode shuffle/sequential toggle. All of it stays
// out of git (C-21); this store is the only writer.
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RgasSoundboard.Library;

public sealed class Clip
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("duration_s")] public double DurationSeconds { get; set; }
    [JsonPropertyName("collections")] public List<string> Collections { get; set; } = new(); // C-16: many
}

public sealed class Collection
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("mode")] public int Mode { get; set; } // C-16: exactly one of 1..3
    [JsonPropertyName("selected")] public bool Selected { get; set; } = true; // C-17, persisted
}

public sealed class LibraryData
{
    [JsonPropertyName("clips")] public List<Clip> Clips { get; set; } = new();
    [JsonPropertyName("collections")] public List<Collection> Collections { get; set; } = new();
    // C-10: per-mode order toggle, persisted — "shuffle" | "sequential"
    [JsonPropertyName("mode_order")] public Dictionary<string, string> ModeOrder { get; set; } = new();
    // C-32: last chosen speaker mode, persisted across restarts (null: config default)
    [JsonPropertyName("speaker_mode")] public string? SpeakerMode { get; set; }
}

public sealed class LibraryStore
{
    private readonly object _gate = new();
    private readonly string _jsonPath;
    private LibraryData _data = new();

    public string AudioDir { get; }
    public event Action? Changed;

    public LibraryStore(string baseDir)
    {
        AudioDir = Path.Combine(baseDir, "library");
        Directory.CreateDirectory(AudioDir);
        _jsonPath = Path.Combine(AudioDir, "library.json");
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_jsonPath)) return;
        try { _data = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(_jsonPath)) ?? new LibraryData(); }
        catch { _data = new LibraryData(); }
        // Drop entries whose WAV vanished (someone tidied the folder by hand).
        _data.Clips.RemoveAll(c => !File.Exists(Path.Combine(AudioDir, c.File)));
    }

    private void Save()
    {
        // Atomic: write temp then move, so a crash can't corrupt the library (C-19).
        var tmp = _jsonPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _jsonPath, overwrite: true);
    }

    // --- reads -----------------------------------------------------------------
    public IReadOnlyList<Clip> Clips { get { lock (_gate) return _data.Clips.ToList(); } }
    public IReadOnlyList<Collection> Collections { get { lock (_gate) return _data.Collections.ToList(); } }

    public List<Collection> CollectionsForMode(int mode)
    {
        lock (_gate) return _data.Collections.Where(c => c.Mode == mode).ToList();
    }

    /// <summary>C-18: active pool = union of the mode's selected collections.</summary>
    public List<Clip> Pool(int mode)
    {
        lock (_gate)
        {
            var selected = _data.Collections
                .Where(c => c.Mode == mode && c.Selected)
                .Select(c => c.Id)
                .ToHashSet();
            return _data.Clips.Where(c => c.Collections.Any(selected.Contains)).ToList();
        }
    }

    public string GetOrder(int mode)
    {
        lock (_gate)
            return _data.ModeOrder.TryGetValue(mode.ToString(), out var v) && v == "sequential"
                ? "sequential" : "shuffle";
    }

    public string WavPath(Clip clip) => Path.Combine(AudioDir, clip.File);

    // --- writes ----------------------------------------------------------------
    public void SetOrder(int mode, string order)
    {
        lock (_gate) { _data.ModeOrder[mode.ToString()] = order; Save(); }
        Changed?.Invoke();
    }

    /// <summary>C-17: refuses to deselect the last selected collection of a mode.</summary>
    public bool SetSelected(string collectionId, bool selected)
    {
        lock (_gate)
        {
            var col = _data.Collections.FirstOrDefault(c => c.Id == collectionId);
            if (col is null) return false;
            if (!selected)
            {
                int stillSelected = _data.Collections.Count(c => c.Mode == col.Mode && c.Selected && c.Id != col.Id);
                if (stillSelected == 0) return false; // never leave a populated mode empty
            }
            col.Selected = selected;
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    public Collection AddCollection(string name, int mode)
    {
        var col = new Collection { Id = Guid.NewGuid().ToString("N")[..8], Name = name.Trim(), Mode = mode };
        lock (_gate) { _data.Collections.Add(col); Save(); }
        Changed?.Invoke();
        return col;
    }

    /// <summary>Registers a clip whose WAV was already written into AudioDir (C-26).</summary>
    public Clip AddClip(string label, string fileName, double durationSeconds, IEnumerable<string> collectionIds)
    {
        var clip = new Clip
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            File = fileName,
            Label = label.Trim(),
            DurationSeconds = durationSeconds,
            Collections = collectionIds.Distinct().ToList(),
        };
        lock (_gate) { _data.Clips.Add(clip); Save(); }
        Changed?.Invoke();
        return clip;
    }

    public void AssignClip(string clipId, IEnumerable<string> collectionIds)
    {
        lock (_gate)
        {
            var clip = _data.Clips.FirstOrDefault(c => c.Id == clipId);
            if (clip is null) return;
            clip.Collections = collectionIds.Distinct().ToList();
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>C-39: add/remove a clip's membership in one collection.</summary>
    public void ToggleMembership(string clipId, string collectionId)
    {
        lock (_gate)
        {
            var clip = _data.Clips.FirstOrDefault(c => c.Id == clipId);
            if (clip is null) return;
            if (!clip.Collections.Remove(collectionId)) clip.Collections.Add(collectionId);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>C-39: delete a collection; member clips stay in the library. If the
    /// mode still has collections but none selected, one is auto-selected (C-17).</summary>
    public void DeleteCollection(string collectionId)
    {
        lock (_gate)
        {
            var col = _data.Collections.FirstOrDefault(c => c.Id == collectionId);
            if (col is null) return;
            _data.Collections.Remove(col);
            foreach (var clip in _data.Clips) clip.Collections.Remove(collectionId);
            var sameMode = _data.Collections.Where(c => c.Mode == col.Mode).ToList();
            if (sameMode.Count > 0 && !sameMode.Any(c => c.Selected)) sameMode[0].Selected = true;
            Save();
        }
        Changed?.Invoke();
    }

    public void DeleteClip(string clipId)
    {
        string? wav = null;
        lock (_gate)
        {
            var clip = _data.Clips.FirstOrDefault(c => c.Id == clipId);
            if (clip is null) return;
            wav = Path.Combine(AudioDir, clip.File);
            _data.Clips.Remove(clip);
            Save();
        }
        try { if (wav is not null && File.Exists(wav)) File.Delete(wav); } catch { }
        Changed?.Invoke();
    }

    // --- C-32: persisted speaker mode ---------------------------------------------
    public string? GetSpeakerMode() { lock (_gate) return _data.SpeakerMode; }

    public void SetSpeakerMode(string mode)
    {
        lock (_gate) { _data.SpeakerMode = mode; Save(); }
        // No Changed event: purely an output-routing preference, not library content.
    }
}
