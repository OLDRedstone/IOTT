using System.Collections.Concurrent;
using System.Text.Json;
using IndustrialIoT.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

internal static class MqttOutboxRegressionTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iott-mqtt-outbox-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await RecoveryAndOwnershipAsync(directory);
            await CorruptionAsync(directory);
            await RuntimeDisableAsync(Path.Combine(directory, "runtime-disable"));
            using var disabled = new TestOutbox(Path.Combine(directory, "disabled")) { IsEnabled = false };
            var rejected = await disabled.EnqueueAsync("device", "topic", "{}", 1, "disabled", default);
            TestSupport.Require(!rejected.Queued && !Directory.Exists(Path.Combine(directory, "disabled")),
                "Disabled forwarding must not claim to enqueue a message");
            var blockedPath = Path.Combine(directory, "not-a-directory");
            await File.WriteAllTextAsync(blockedPath, "file");
            using var blocked = new TestOutbox(blockedPath);
            var failed = await blocked.EnqueueAsync("device", "topic", "{}", 1, "blocked", default);
            TestSupport.Require(!failed.Queued && blocked.GetStatus().FailedCount == 1
                && !string.IsNullOrWhiteSpace(blocked.GetStatus().LastError), "Disk failures must be visible");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static async Task RecoveryAndOwnershipAsync(string directory)
    {
        const string messageId = "stable-message-id";
        const string payload = "{\"messageId\":\"stable-message-id\",\"deviceId\":\"device\",\"values\":[]}";
        using var first = new TestOutbox(directory);
        var result = await first.EnqueueAsync("device", "topic", payload, 1, messageId, default);
        TestSupport.Require(result.Queued && result.MessageId == messageId && first.GetStatus().PendingCount == 1,
            "Acceptance must mean a message exists on disk");
        var stored = JsonSerializer.Deserialize<MqttOutboxMessage>(await File.ReadAllTextAsync(
            Directory.EnumerateFiles(directory, "*.json").Single()))!;
        TestSupport.Require(stored.MessageId == messageId && stored.Payload == payload, "Stored envelope changed");
        var duplicate = await first.EnqueueAsync("device", "topic", payload, 1, messageId, default);
        TestSupport.Require(duplicate.Queued && first.GetStatus().PendingCount == 1, "Duplicate enqueue must be idempotent");
        using (var competing = new TestOutbox(directory))
        {
            var conflict = await competing.EnqueueAsync("device", "topic", "{}", 1, "other", default);
            TestSupport.Require(!conflict.Queued && competing.GetStatus().FailedCount == 1,
                "Two instances must not own the same outbox directory");
        }
        await first.StartAsync(default);
        await WaitUntilAsync(() => first.GetStatus().FailedCount > 0);
        TestSupport.Require(first.GetStatus().PendingCount == 1 && first.GetStatus().SentCount == 0,
            "Failed delivery must retain the persisted message");
        await first.StopAsync(default);
        first.Dispose();
        first.Dispose();
        using var restored = new TestOutbox(directory) { CanDeliver = true };
        await restored.StartAsync(default);
        await WaitUntilAsync(() => restored.GetStatus().SentCount == 1);
        TestSupport.Require(restored.GetStatus().PendingCount == 0 && restored.GetStatus().FailedCount == 0
            && restored.Delivered.Single().MessageId == messageId
            && restored.Delivered.Single().Payload == payload, "Restart must restore and deliver without a new enqueue");
        TestSupport.Require(restored.GetStatus().LastSentAt.HasValue && restored.GetStatus().LastAttemptAt.HasValue,
            "Status must expose delivery attempt and acknowledgement times");
        await restored.StopAsync(default);
    }

    private static async Task CorruptionAsync(string directory)
    {
        var corruptPath = Path.Combine(directory, "corrupt.json");
        await File.WriteAllTextAsync(corruptPath, "{broken");
        using var outbox = new TestOutbox(directory) { CanDeliver = true };
        await outbox.StartAsync(default);
        await WaitUntilAsync(() => outbox.GetStatus().FailedCount > 0);
        var valid = await outbox.EnqueueAsync("device", "topic", "{}", 1, "after-corrupt", default);
        await WaitUntilAsync(() => outbox.GetStatus().SentCount == 1);
        TestSupport.Require(valid.Queued && File.Exists(corruptPath) && outbox.GetStatus().PendingCount == 1
            && !string.IsNullOrWhiteSpace(outbox.GetStatus().LastError),
            "A corrupt item must remain visible without blocking valid messages");
        await outbox.StopAsync(default);
    }

    private static async Task RuntimeDisableAsync(string directory)
    {
        using var outbox = new TestOutbox(directory) { CanDeliver = true };
        foreach (var topic in new[] { "invalid/#", "invalid/+", "invalid\0topic", new string('x', 65536) })
        {
            var invalid = await outbox.EnqueueAsync("device", topic, "{}", 1, "invalid", default);
            TestSupport.Require(!invalid.Queued && outbox.GetStatus().PendingCount == 0,
                "Invalid publish topics must not enter the queue");
        }
        await outbox.EnqueueAsync("device", "valid", "{}", 1, "first", default);
        await outbox.EnqueueAsync("device", "valid", "{}", 1, "second", default);
        outbox.AfterDelivery = () => outbox.IsEnabled = false;
        await outbox.StartAsync(default);
        await WaitUntilAsync(() => outbox.GetStatus().SentCount == 1);
        TestSupport.Require(outbox.GetStatus().PendingCount == 1 && !outbox.GetStatus().Enabled,
            "Disabling during drain must retain the next batch");
        await outbox.StopAsync(default);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class TestOutbox(string directory) : DurableMqttOutbox(directory, NullLogger.Instance)
    {
        public bool IsEnabled { get; set; } = true;
        public bool CanDeliver { get; init; }
        public Action? AfterDelivery { get; set; }
        public ConcurrentQueue<MqttOutboxMessage> Delivered { get; } = new();
        protected override bool Enabled => IsEnabled;
        protected override bool IsConnected => CanDeliver;
        protected override Task DeliverAsync(MqttOutboxMessage message, CancellationToken ct)
        {
            if (!CanDeliver) throw new IOException("Simulated unavailable transport for storage regression.");
            Delivered.Enqueue(message);
            AfterDelivery?.Invoke();
            return Task.CompletedTask;
        }
    }
}
