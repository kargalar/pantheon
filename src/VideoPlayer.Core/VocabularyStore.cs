using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoPlayer.Core;

/// <summary>A word the user marked as unknown while watching.</summary>
public sealed record VocabularyEntry
{
    public string Word { get; init; } = "";
    public string Display { get; init; } = "";
    public string Translation { get; init; } = "";
    public List<string> Alternatives { get; init; } = [];
    public string Context { get; init; } = "";
    public string Source { get; init; } = "";
    public DateTime AddedAt { get; init; } = DateTime.Now;
    public int SeenCount { get; init; } = 1;
}

public sealed record VocabularySettings
{
    public bool Enabled { get; init; }
    public string TargetLanguage { get; init; } = "tr";
    public bool PauseOnHover { get; init; } = true;
    public bool ShowSentenceTranslation { get; init; }
}

public sealed class VocabularyData
{
    public VocabularySettings Settings { get; set; } = new();
    public List<VocabularyEntry> Words { get; set; } = [];
}

/// <summary>Persists the unknown-word list and the interactive subtitle settings as JSON.</summary>
public sealed class VocabularyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _path;
    private readonly Dictionary<string, VocabularyEntry> _words = new(StringComparer.Ordinal);
    private bool _dirty;

    public VocabularyStore(string path)
    {
        _path = path;
        Load();
    }

    public VocabularySettings Settings { get; private set; } = new();
    public IReadOnlyCollection<VocabularyEntry> Words => _words.Values;
    public int Count => _words.Count;
    public event EventHandler? Changed;

    public bool Contains(string key) => _words.ContainsKey(key);
    public VocabularyEntry? Get(string key) => _words.GetValueOrDefault(key);

    public void UpdateSettings(VocabularySettings settings)
    {
        if (Settings == settings) return;
        Settings = settings;
        _dirty = true;
        Flush();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(VocabularyEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Word)) return;
        _words[entry.Word] = entry;
        _dirty = true;
        Flush();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string key)
    {
        if (!_words.Remove(key)) return false;
        _dirty = true;
        Flush();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        if (_words.Count == 0) return;
        _words.Clear();
        _dirty = true;
        Flush();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Increments the encounter counter without raising <see cref="Changed"/> or hitting the disk.</summary>
    public void RecordSeen(string key)
    {
        if (!_words.TryGetValue(key, out var entry)) return;
        _words[key] = entry with { SeenCount = entry.SeenCount + 1 };
        _dirty = true;
    }

    public void FlushIfDirty()
    {
        if (_dirty) Flush();
    }

    /// <summary>Tab-separated export (word, translation, context) that Anki and spreadsheets import directly.</summary>
    public string ExportTsv()
    {
        var builder = new StringBuilder();
        foreach (var entry in _words.Values.OrderBy(entry => entry.Word, StringComparer.Ordinal))
        {
            builder.Append(Clean(entry.Display.Length > 0 ? entry.Display : entry.Word)).Append('\t')
                .Append(Clean(entry.Translation)).Append('\t')
                .Append(Clean(entry.Context)).AppendLine();
        }
        return builder.ToString();

        static string Clean(string value) => value.Replace('\t', ' ').Replace("\r", "").Replace('\n', ' ');
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var data = JsonSerializer.Deserialize<VocabularyData>(File.ReadAllText(_path, Encoding.UTF8), JsonOptions);
            if (data is null) return;
            Settings = data.Settings ?? new VocabularySettings();
            foreach (var entry in data.Words ?? [])
                if (!string.IsNullOrWhiteSpace(entry.Word)) _words[entry.Word] = entry;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file must not stop the player; start with an empty list.
        }
    }

    private void Flush()
    {
        _dirty = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var data = new VocabularyData { Settings = Settings, Words = [.. _words.Values.OrderBy(entry => entry.AddedAt)] };
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _dirty = true;
        }
    }
}
