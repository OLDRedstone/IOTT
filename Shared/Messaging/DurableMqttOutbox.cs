namespace IndustrialIoT.Messaging;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public abstract partial class DurableMqttOutbox : BackgroundService
{
    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly object _statusLock = new();
    private readonly object _fileMutationLock = new();
    private readonly SemaphoreSlim _wakeup = new(0, 1);
    private readonly DateTimeOffset _counterStartedAt = DateTimeOffset.UtcNow;
    private long _pendingCount;
    private long _sentCount;
    private long _failedCount;
    private string? _lastError;
    private DateTimeOffset? _lastAttemptAt;
    private DateTimeOffset? _lastSentAt;
    private DateTimeOffset _lastCountRefreshAt;

    protected DurableMqttOutbox(string directory, ILogger logger)
    {
        _directory = Path.GetFullPath(directory);
        _logger = logger;
    }

    protected abstract bool Enabled { get; }
    protected abstract bool IsConnected { get; }
    protected abstract Task DeliverAsync(MqttOutboxMessage message, CancellationToken ct);

    public MqttForwardingStatus GetStatus()
    {
        RefreshPendingCount();
        lock (_statusLock)
            return new(Enabled, IsConnected, _pendingCount, _sentCount, _failedCount,
                _lastError, _lastAttemptAt, _lastSentAt, _counterStartedAt);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Enabled) await DrainAsync(stoppingToken);
                await _wakeup.WaitAsync(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                RecordFailure(error);
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private void Wakeup()
    {
        try { _wakeup.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void RefreshPendingCount(bool force = false)
    {
        try
        {
            lock (_fileMutationLock)
            {
                if (!force && DateTimeOffset.UtcNow - _lastCountRefreshAt < TimeSpan.FromSeconds(2)) return;
                var count = Directory.Exists(_directory)
                    ? Directory.EnumerateFiles(_directory, "*.json").LongCount() : 0;
                lock (_statusLock) _pendingCount = count;
                _lastCountRefreshAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception error)
        {
            lock (_statusLock) _lastError = error.Message;
        }
    }

    private void RecordFailure(Exception error)
    {
        lock (_statusLock)
        {
            _failedCount++;
            _lastError = error.Message;
        }
        _logger.LogWarning(error, "MQTT outbox attempt failed; persisted messages will be retried");
    }
}
