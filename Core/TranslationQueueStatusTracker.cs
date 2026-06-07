namespace OwTranslateLite.Core;

public sealed class TranslationQueueStatusTracker
{
    private readonly object _gate = new();
    private int _queuedCount;
    private int _activeCount;
    private long _lastApiLatencyMs;
    private string _lastFailure = "";
    private DateTime? _lastUpdatedAt;

    public void SetQueuedCount(int queuedCount)
    {
        lock (_gate)
        {
            _queuedCount = Math.Max(0, queuedCount);
            _lastUpdatedAt = DateTime.Now;
        }
    }

    public void BeginBatch(int count)
    {
        lock (_gate)
        {
            _activeCount += Math.Max(0, count);
            _lastUpdatedAt = DateTime.Now;
        }
    }

    public void CompleteBatch(int count, TimeSpan elapsed)
    {
        lock (_gate)
        {
            _activeCount = Math.Max(0, _activeCount - Math.Max(0, count));
            _lastApiLatencyMs = Math.Max(0, (long)elapsed.TotalMilliseconds);
            _lastFailure = "";
            _lastUpdatedAt = DateTime.Now;
        }
    }

    public void FailBatch(int count, string message)
    {
        lock (_gate)
        {
            _activeCount = Math.Max(0, _activeCount - Math.Max(0, count));
            _lastFailure = message.Trim();
            _lastUpdatedAt = DateTime.Now;
        }
    }

    public void CancelBatch(int count)
    {
        lock (_gate)
        {
            _activeCount = Math.Max(0, _activeCount - Math.Max(0, count));
            _lastUpdatedAt = DateTime.Now;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _queuedCount = 0;
            _activeCount = 0;
            _lastApiLatencyMs = 0;
            _lastFailure = "";
            _lastUpdatedAt = DateTime.Now;
        }
    }

    public TranslationQueueStatus Snapshot()
    {
        lock (_gate)
        {
            return new TranslationQueueStatus(
                _queuedCount,
                _activeCount,
                _lastApiLatencyMs,
                _lastFailure,
                _lastUpdatedAt);
        }
    }
}

public sealed record TranslationQueueStatus(
    int QueuedCount,
    int ActiveCount,
    long LastApiLatencyMs,
    string LastFailure,
    DateTime? LastUpdatedAt);

public sealed record RuntimeDiagnosticsSnapshot(
    bool IsRunning,
    int RunGeneration,
    int OverlayRecordCount,
    TranslationQueueStatus TranslationQueue);
