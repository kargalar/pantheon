using System.Text.Json;

namespace VideoPlayer.Core;

public readonly record struct PlaybackProgress(
    double PositionSeconds,
    double SubtitleDelaySeconds = 0,
    double? PlaybackSpeed = null,
    string? SubtitlePath = null,
    int? SubtitleTrackId = null);

public static class PlaybackProgressRules
{
    public const double MinimumPositionChangeSeconds = 1.0;

    public static bool HasMeaningfulChange(PlaybackProgress? previous, PlaybackProgress current) =>
        previous is not { } previousProgress
        || Math.Abs(previousProgress.PositionSeconds - current.PositionSeconds) >= MinimumPositionChangeSeconds
        || previousProgress.SubtitleDelaySeconds != current.SubtitleDelaySeconds
        || previousProgress.PlaybackSpeed != current.PlaybackSpeed
        || !string.Equals(previousProgress.SubtitlePath, current.SubtitlePath, StringComparison.OrdinalIgnoreCase)
        || previousProgress.SubtitleTrackId != current.SubtitleTrackId;
}

public sealed class PlaybackProgressStore
{
    public const int MaximumEntryCount = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _storePath;
    private Dictionary<string, PlaybackProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _progressOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _progressOrderNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private long _version;
    private long _persistedVersion;
    private Task? _persistTask;
    private Task? _loadTask;

    private sealed record PersistSnapshot(long Version, Dictionary<string, PlaybackProgress> Progress);

    private sealed record LoadedHistory(
        Dictionary<string, PlaybackProgress> Progress,
        IReadOnlyList<string> RecencyOrder,
        bool WasTrimmed);

    private sealed class HistoryStreamReader
    {
        private const int MaximumEncodedPathLength = 32_767 * 6;
        private const int InitialBufferSize = 64 * 1024;
        private const int MaximumBufferSize = 256 * 1024;
        private const byte Utf8ByteOrderMarkFirstByte = 0xEF;
        private const byte Utf8ByteOrderMarkSecondByte = 0xBB;
        private const byte Utf8ByteOrderMarkThirdByte = 0xBF;

        private readonly Dictionary<string, PlaybackProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _recencyOrder = new();
        private bool _rootStarted;
        private bool _rootEnded;
        private bool _hasCurrentEntry;
        private bool _currentEntryHasProgressObject;
        private bool _wasTrimmed;
        private bool _readingProgress;
        private int _progressDepth;
        private int? _skippingArrayAtDepth;
        private string? _currentPath;
        private string? _progressProperty;
        private double _positionSeconds;
        private double _subtitleDelaySeconds;
        private double? _playbackSpeed;
        private string? _subtitlePath;
        private int? _subtitleTrackId;

        public LoadedHistory Read(Stream stream)
        {
            SkipUtf8ByteOrderMark(stream);
            var buffer = new byte[InitialBufferSize];
            var bufferedLength = 0;
            var readerState = new JsonReaderState();
            var isFinalBlock = false;
            while (true)
            {
                var reader = new Utf8JsonReader(buffer.AsSpan(0, bufferedLength), isFinalBlock, readerState);
                while (reader.Read()) ReadToken(ref reader);
                var consumedLength = checked((int)reader.BytesConsumed);
                readerState = reader.CurrentState;
                Buffer.BlockCopy(buffer, consumedLength, buffer, 0, bufferedLength - consumedLength);
                bufferedLength -= consumedLength;

                if (isFinalBlock) break;
                if (bufferedLength == buffer.Length)
                {
                    if (buffer.Length == MaximumBufferSize)
                        throw new JsonException("Playback progress contains an oversized JSON token.");
                    Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaximumBufferSize));
                }

                var readLength = stream.Read(buffer, bufferedLength, buffer.Length - bufferedLength);
                if (readLength == 0) isFinalBlock = true;
                else bufferedLength += readLength;
            }

            if (!_rootEnded) throw new JsonException("Playback progress object is incomplete.");
            return new LoadedHistory(_progress, _recencyOrder.ToArray(), _wasTrimmed);
        }

        private static void SkipUtf8ByteOrderMark(Stream stream)
        {
            Span<byte> prefix = stackalloc byte[3];
            var prefixLength = 0;
            while (prefixLength < prefix.Length)
            {
                var readLength = stream.Read(prefix[prefixLength..]);
                if (readLength == 0) break;
                prefixLength += readLength;
            }

            if (prefixLength == prefix.Length
                && prefix[0] == Utf8ByteOrderMarkFirstByte
                && prefix[1] == Utf8ByteOrderMarkSecondByte
                && prefix[2] == Utf8ByteOrderMarkThirdByte) return;
            stream.Position = 0;
        }

        private void ReadToken(ref Utf8JsonReader reader)
        {
            if (_rootEnded) throw new JsonException("Unexpected data follows playback progress.");
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    ReadStartObject(ref reader);
                    break;
                case JsonTokenType.EndObject:
                    ReadEndObject(ref reader);
                    break;
                case JsonTokenType.PropertyName:
                    ReadPropertyName(ref reader);
                    break;
                case JsonTokenType.StartArray:
                    if (_readingProgress && reader.CurrentDepth == _progressDepth + 1
                        && IsProgressProperty(_progressProperty))
                        _currentEntryHasProgressObject = false;
                    else if (_hasCurrentEntry && !_readingProgress && reader.CurrentDepth == 1)
                        _skippingArrayAtDepth = reader.CurrentDepth;
                    break;
                case JsonTokenType.EndArray:
                    if (_skippingArrayAtDepth == reader.CurrentDepth)
                    {
                        _skippingArrayAtDepth = null;
                        CompleteCurrentEntry();
                    }
                    break;
                case JsonTokenType.Number:
                    ReadNumber(ref reader);
                    break;
                case JsonTokenType.String:
                    ReadString(ref reader);
                    if (_hasCurrentEntry && !_readingProgress && reader.CurrentDepth == 1)
                        CompleteCurrentEntry();
                    else if (_readingProgress && reader.CurrentDepth == _progressDepth + 1
                        && IsNumericProgressProperty(_progressProperty))
                        _currentEntryHasProgressObject = false;
                    break;
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    if (_hasCurrentEntry && !_readingProgress && reader.CurrentDepth == 1)
                        CompleteCurrentEntry();
                    else if (_readingProgress && reader.CurrentDepth == _progressDepth + 1
                        && IsProgressProperty(_progressProperty)
                        && !(reader.TokenType == JsonTokenType.Null
                            && _progressProperty is nameof(PlaybackProgress.PlaybackSpeed)
                                or nameof(PlaybackProgress.SubtitlePath)
                                or nameof(PlaybackProgress.SubtitleTrackId)))
                        _currentEntryHasProgressObject = false;
                    break;
            }
        }

        private void ReadStartObject(ref Utf8JsonReader reader)
        {
            if (!_rootStarted)
            {
                if (reader.CurrentDepth != 0) throw new JsonException("Playback progress root is invalid.");
                _rootStarted = true;
                return;
            }

            if (_readingProgress && reader.CurrentDepth == _progressDepth + 1
                && IsProgressProperty(_progressProperty))
                _currentEntryHasProgressObject = false;

            if (_hasCurrentEntry && !_readingProgress && reader.CurrentDepth == 1)
            {
                _readingProgress = true;
                _currentEntryHasProgressObject = true;
                _progressDepth = reader.CurrentDepth;
                _progressProperty = null;
                _positionSeconds = 0;
                _subtitleDelaySeconds = 0;
                _playbackSpeed = null;
                _subtitlePath = null;
                _subtitleTrackId = null;
            }
        }

        private void ReadEndObject(ref Utf8JsonReader reader)
        {
            if (_readingProgress && reader.CurrentDepth == _progressDepth)
            {
                _readingProgress = false;
                CompleteCurrentEntry();
                return;
            }

            if (_rootStarted && reader.CurrentDepth == 0) _rootEnded = true;
        }

        private void ReadPropertyName(ref Utf8JsonReader reader)
        {
            if (_readingProgress && reader.CurrentDepth == _progressDepth + 1)
            {
                _progressProperty = reader.GetString();
                return;
            }

            if (_rootStarted && !_readingProgress && reader.CurrentDepth == 1)
            {
                var encodedPathLength = reader.HasValueSequence
                    ? reader.ValueSequence.Length
                    : reader.ValueSpan.Length;
                _currentPath = encodedPathLength <= MaximumEncodedPathLength ? reader.GetString() : null;
                _hasCurrentEntry = true;
                _currentEntryHasProgressObject = false;
            }
        }

        private void ReadNumber(ref Utf8JsonReader reader)
        {
            if (_hasCurrentEntry && !_readingProgress && reader.CurrentDepth == 1)
            {
                CompleteCurrentEntry();
                return;
            }
            if (!_readingProgress || reader.CurrentDepth != _progressDepth + 1) return;
            if (!reader.TryGetDouble(out var number))
            {
                if (IsNumericProgressProperty(_progressProperty))
                    _currentEntryHasProgressObject = false;
                return;
            }
            if (string.Equals(_progressProperty, nameof(PlaybackProgress.PositionSeconds), StringComparison.Ordinal))
                _positionSeconds = number;
            else if (string.Equals(_progressProperty, nameof(PlaybackProgress.SubtitleDelaySeconds), StringComparison.Ordinal))
                _subtitleDelaySeconds = number;
            else if (string.Equals(_progressProperty, nameof(PlaybackProgress.PlaybackSpeed), StringComparison.Ordinal))
                _playbackSpeed = number;
            else if (string.Equals(_progressProperty, nameof(PlaybackProgress.SubtitleTrackId), StringComparison.Ordinal))
            {
                if (number < int.MinValue || number > int.MaxValue || number != Math.Truncate(number))
                    _currentEntryHasProgressObject = false;
                else
                    _subtitleTrackId = (int)number;
            }
        }

        private void ReadString(ref Utf8JsonReader reader)
        {
            if (_readingProgress && reader.CurrentDepth == _progressDepth + 1
                && string.Equals(_progressProperty, nameof(PlaybackProgress.SubtitlePath), StringComparison.Ordinal))
                _subtitlePath = reader.GetString();
        }

        private static bool IsNumericProgressProperty(string? property) =>
            property is nameof(PlaybackProgress.PositionSeconds)
                or nameof(PlaybackProgress.SubtitleDelaySeconds)
                or nameof(PlaybackProgress.PlaybackSpeed)
                or nameof(PlaybackProgress.SubtitleTrackId);

        private static bool IsProgressProperty(string? property) =>
            IsNumericProgressProperty(property) || property is nameof(PlaybackProgress.SubtitlePath);

        private void CompleteCurrentEntry()
        {
            if (!_hasCurrentEntry) return;
            _hasCurrentEntry = false;
            if (!_currentEntryHasProgressObject || string.IsNullOrWhiteSpace(_currentPath)
                || !double.IsFinite(_positionSeconds) || _positionSeconds < 0
                || !double.IsFinite(_subtitleDelaySeconds)
                || _playbackSpeed is { } speed && (!double.IsFinite(speed) || speed <= 0)) return;

            string fullPath;
            try { fullPath = Path.GetFullPath(_currentPath); }
            catch (Exception exception) when (exception is ArgumentException or IOException
                or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                return;
            }

            if (_progress.ContainsKey(fullPath)) return;
            if (_progress.Count == MaximumEntryCount)
            {
                _progress.Remove(_recencyOrder.Dequeue());
                _wasTrimmed = true;
            }
            string? fullSubtitlePath = null;
            if (!string.IsNullOrWhiteSpace(_subtitlePath))
            {
                try { fullSubtitlePath = Path.GetFullPath(_subtitlePath); }
                catch (Exception exception) when (exception is ArgumentException or IOException
                    or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
                { }
            }
            _progress.Add(fullPath, new PlaybackProgress(
                _positionSeconds, _subtitleDelaySeconds, _playbackSpeed, fullSubtitlePath, _subtitleTrackId));
            _recencyOrder.Enqueue(fullPath);
        }
    }

    public PlaybackProgressStore(string storePath, bool loadImmediately = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        _storePath = storePath;
        if (loadImmediately)
        {
            Load();
            _loadTask = Task.CompletedTask;
        }
    }

    public Task LoadAsync()
    {
        lock (_sync)
            return _loadTask ??= Task.Run(Load);
    }

    public PlaybackProgress Get(string videoPath)
    {
        var key = Path.GetFullPath(videoPath);
        EnsureLoadedSynchronously();
        lock (_sync)
        {
            if (!_progress.TryGetValue(key, out var progress)) return new PlaybackProgress(0);
            TouchProgressEntry(key);
            return progress;
        }
    }

    public void Save(string videoPath, PlaybackProgress progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        if (!IsValid(progress))
            throw new ArgumentOutOfRangeException(nameof(progress), UiText.PlaybackProgressMustContainValidNumbers);

        EnsureLoadedSynchronously();
        _ = UpdateState(videoPath, progress);
        PersistPendingSynchronously();
    }

    public async Task ScheduleSave(string videoPath, PlaybackProgress progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        if (!IsValid(progress))
            throw new ArgumentOutOfRangeException(nameof(progress), UiText.PlaybackProgressMustContainValidNumbers);

        await LoadAsync().ConfigureAwait(false);
        _ = UpdateState(videoPath, progress);
        await SchedulePersistIfPending().ConfigureAwait(false);
    }

    public void Remove(string videoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        EnsureLoadedSynchronously();
        _ = RemoveState(videoPath);
        PersistPendingSynchronously();
    }

    public async Task ScheduleRemove(string videoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        await LoadAsync().ConfigureAwait(false);
        _ = RemoveState(videoPath);
        await SchedulePersistIfPending().ConfigureAwait(false);
    }

    public async Task FlushAsync()
    {
        await LoadAsync().ConfigureAwait(false);
        while (true)
        {
            var persistTask = SchedulePersistIfPending();
            if (persistTask.IsCompletedSuccessfully) return;
            await persistTask.ConfigureAwait(false);
        }
    }

    private long? UpdateState(string videoPath, PlaybackProgress progress)
    {
        var path = Path.GetFullPath(videoPath);
        lock (_sync)
        {
            if (_progress.TryGetValue(path, out var previous)
                && !PlaybackProgressRules.HasMeaningfulChange(previous, progress)) return null;
            if (!_progress.ContainsKey(path) && _progress.Count == MaximumEntryCount)
                RemoveOldestProgressEntry();
            _progress[path] = progress;
            TouchProgressEntry(path);
            return ++_version;
        }
    }

    private static bool IsValid(PlaybackProgress progress) =>
        double.IsFinite(progress.PositionSeconds) && progress.PositionSeconds >= 0
        && double.IsFinite(progress.SubtitleDelaySeconds)
        && (progress.PlaybackSpeed is null
            || double.IsFinite(progress.PlaybackSpeed.Value) && progress.PlaybackSpeed.Value > 0);

    private long? RemoveState(string videoPath)
    {
        var path = Path.GetFullPath(videoPath);
        lock (_sync)
        {
            if (!_progress.Remove(path)) return null;
            RemoveProgressOrderEntry(path);
            return ++_version;
        }
    }

    private PersistSnapshot? CreatePendingSnapshot()
    {
        lock (_sync)
            return _persistedVersion >= _version
                ? null
                : new PersistSnapshot(
                    _version,
                    CreateOrderedProgressSnapshot());
    }

    private Task SchedulePersistIfPending()
    {
        lock (_sync)
        {
            if (_persistedVersion >= _version) return Task.CompletedTask;
            if (_persistTask is null || _persistTask.IsCompleted)
                _persistTask = Task.Run(PersistLatestSnapshot);
            return _persistTask;
        }
    }

    private async Task PersistLatestSnapshot()
    {
        try
        {
            while (true)
            {
                PersistSnapshot snapshot;
                lock (_sync) snapshot = new PersistSnapshot(_version, CreateOrderedProgressSnapshot());

                await _persistLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (IsCurrentVersion(snapshot.Version))
                    {
                        PersistFile(snapshot.Progress);
                        MarkPersisted(snapshot.Version);
                    }
                }
                finally
                {
                    _persistLock.Release();
                }

                lock (_sync)
                {
                    if (_version != snapshot.Version) continue;
                    _persistTask = null;
                    return;
                }
            }
        }
        catch
        {
            lock (_sync) _persistTask = null;
            throw;
        }
    }

    private void Persist(PersistSnapshot snapshot)
    {
        _persistLock.Wait();
        try
        {
            if (IsCurrentVersion(snapshot.Version))
            {
                PersistFile(snapshot.Progress);
                MarkPersisted(snapshot.Version);
            }
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private void PersistPendingSynchronously()
    {
        var snapshot = CreatePendingSnapshot();
        if (snapshot is not null) Persist(snapshot);
    }

    private bool IsCurrentVersion(long version)
    {
        lock (_sync) return version == _version;
    }

    private void MarkPersisted(long version)
    {
        lock (_sync) _persistedVersion = Math.Max(_persistedVersion, version);
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            LoadedHistory loaded;
            using (var stream = new FileStream(
                _storePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan))
                loaded = new HistoryStreamReader().Read(stream);

            lock (_sync)
            {
                _progress = loaded.Progress;
                _progressOrder.Clear();
                _progressOrderNodes.Clear();
                foreach (var path in loaded.RecencyOrder)
                {
                    var node = _progressOrder.AddLast(path);
                    _progressOrderNodes.Add(path, node);
                }
                if (loaded.WasTrimmed) _version++;
            }
            if (loaded.WasTrimmed) PersistPendingSynchronously();
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void EnsureLoadedSynchronously() => LoadAsync().GetAwaiter().GetResult();

    private void TouchProgressEntry(string path)
    {
        if (_progressOrderNodes.Remove(path, out var existingNode)) _progressOrder.Remove(existingNode);
        var node = _progressOrder.AddLast(path);
        _progressOrderNodes[path] = node;
    }

    private void RemoveOldestProgressEntry()
    {
        var oldestNode = _progressOrder.First;
        if (oldestNode is null) return;
        _progressOrder.RemoveFirst();
        _progressOrderNodes.Remove(oldestNode.Value);
        _progress.Remove(oldestNode.Value);
    }

    private void RemoveProgressOrderEntry(string path)
    {
        if (!_progressOrderNodes.Remove(path, out var node)) return;
        _progressOrder.Remove(node);
    }

    private Dictionary<string, PlaybackProgress> CreateOrderedProgressSnapshot()
    {
        var snapshot = new Dictionary<string, PlaybackProgress>(_progress.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var path in _progressOrder)
            if (_progress.TryGetValue(path, out var progress)) snapshot.Add(path, progress);
        return snapshot;
    }

    private void PersistFile(Dictionary<string, PlaybackProgress> progress)
    {
        var directory = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var tempPath = _storePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(progress, JsonOptions));
            File.Move(tempPath, _storePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
