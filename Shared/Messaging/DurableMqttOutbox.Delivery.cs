namespace IndustrialIoT.Messaging;

using System.Text.Json;

public abstract partial class DurableMqttOutbox
{
    private async Task DrainAsync(CancellationToken ct)
    {
        EnsureDirectoryOwned();
        RefreshPendingCount(force: true);
        foreach (var filePath in Directory.EnumerateFiles(_directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            if (!Enabled) break;
            MqttOutboxMessage message;
            lock (_statusLock) _lastAttemptAt = DateTimeOffset.UtcNow;
            try
            {
                await using var stream = File.OpenRead(filePath);
                message = await JsonSerializer.DeserializeAsync<MqttOutboxMessage>(stream, cancellationToken: ct)
                    ?? throw new InvalidDataException("Persisted MQTT outbox message is null.");
                Validate(message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                RecordFailure(new InvalidDataException($"MQTT outbox file '{Path.GetFileName(filePath)}' could not be read: {error.Message}", error));
                continue;
            }
            try
            {
                await DeliverAsync(message, ct);
                lock (_fileMutationLock)
                {
                    File.Delete(filePath);
                    lock (_statusLock)
                    {
                        _pendingCount = Math.Max(0, _pendingCount - 1);
                        _sentCount++;
                        _lastSentAt = DateTimeOffset.UtcNow;
                        if (_pendingCount == 0) _lastError = null;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                RecordFailure(error);
                break;
            }
        }
        RefreshPendingCount();
    }
}
