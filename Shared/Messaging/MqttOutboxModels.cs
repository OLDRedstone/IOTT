namespace IndustrialIoT.Messaging;

public sealed record MqttOutboxMessage(
    string MessageId,
    string DeviceId,
    string Topic,
    string Payload,
    int QualityOfService,
    DateTimeOffset EnqueuedAt);

public sealed record MqttEnqueueResult(bool Queued, string? MessageId, string? Reason = null);

public sealed record MqttForwardingStatus(
    bool Enabled,
    bool IsConnected,
    long PendingCount,
    long SentCount,
    long FailedCount,
    string? LastError,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSentAt,
    DateTimeOffset CounterStartedAt);
