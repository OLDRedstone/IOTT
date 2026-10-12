namespace IndustrialIoT.Messaging;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public abstract partial class DurableMqttOutbox
{
    private readonly object _storageLock = new();
    private readonly SemaphoreSlim _enqueueLock = new(1, 1);
    private FileStream? _directoryLock;
    private int _disposed;

    public async Task<MqttEnqueueResult> EnqueueAsync(string deviceId, string topic, string payload,
        int qos, string messageId, CancellationToken ct)
    {
        if (!Enabled) return new(false, null, "MQTT forwarding is disabled.");
        string? temporaryPath = null;
        await _enqueueLock.WaitAsync(ct);
        try
        {
            EnsureDirectoryOwned();
            var message = new MqttOutboxMessage(messageId, deviceId, topic, payload, qos, DateTimeOffset.UtcNow);
            Validate(message);
            var fileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId))) + ".json";
            var targetPath = Path.Combine(_directory, fileName);
            if (File.Exists(targetPath))
            {
                var existing = JsonSerializer.Deserialize<MqttOutboxMessage>(await File.ReadAllTextAsync(targetPath, ct));
                if (existing is null || existing.MessageId != messageId || existing.Payload != payload
                    || existing.Topic != topic || existing.DeviceId != deviceId || existing.QualityOfService != qos)
                    throw new IOException("The MQTT messageId already belongs to a different persisted message.");
            }
            else
            {
                temporaryPath = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, message, cancellationToken: ct);
                    await stream.FlushAsync(ct);
                    stream.Flush(flushToDisk: true);
                }
                ct.ThrowIfCancellationRequested();
                lock (_fileMutationLock)
                {
                    File.Move(temporaryPath, targetPath);
                    lock (_statusLock) _pendingCount++;
                }
                temporaryPath = null;
            }
            Wakeup();
            return new(true, messageId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            RecordFailure(error);
            return new(false, null, error.Message);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _enqueueLock.Release();
        }
    }

    private void EnsureDirectoryOwned()
    {
        lock (_storageLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_directoryLock is not null) return;
            Directory.CreateDirectory(_directory);
            _directoryLock = new FileStream(Path.Combine(_directory, ".lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            RefreshPendingCount(force: true);
        }
    }

    private static void Validate(MqttOutboxMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId) || string.IsNullOrWhiteSpace(message.DeviceId)
            || string.IsNullOrWhiteSpace(message.Topic) || string.IsNullOrWhiteSpace(message.Payload)
            || message.QualityOfService is < 1 or > 2)
            throw new InvalidDataException("MQTT outbox message requires messageId, deviceId, topic, payload and QoS 1 or 2.");
        if (message.Topic.IndexOfAny(new[] { '+', '#', '\0' }) >= 0
            || Encoding.UTF8.GetByteCount(message.Topic) > ushort.MaxValue)
            throw new InvalidDataException("MQTT publish topic must not contain wildcards or NUL and must fit within 65535 UTF-8 bytes.");
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        base.Dispose();
        try { ExecuteTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally
        {
            _enqueueLock.Wait();
            try
            {
                lock (_storageLock) _directoryLock?.Dispose();
            }
            finally { _enqueueLock.Release(); }
        }
    }
}
