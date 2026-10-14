namespace MachineConnectionApi.Tests;

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Options;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Server;

internal static partial class MqttReliabilityRegressionTests
{
    public static async Task RunAll()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iott-mqtt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await DurableRecoveryAsync(directory);
            await StorageIndependenceAsync(directory);
            Console.WriteLine("MQTT durability, recovery, independent outputs and status tests passed.");
        }
        finally
        {
            Check(Path.GetDirectoryName(Path.GetFullPath(directory)) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "Unsafe test cleanup path");
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MqttTelemetryPublisher Publisher(string directory, int port) => new(
        new FixedOptions<MqttOptions>(new MqttOptions { Enabled = true, Host = "127.0.0.1", Port = port,
            OutboxDirectory = directory, ClientId = "test-" + Guid.NewGuid().ToString("N"),
            ConnectTimeoutSeconds = 3, PublishTimeoutSeconds = 3 }), NullLogger<MqttTelemetryPublisher>.Instance);

    private static readonly InfluxTelemetryPoint[] Points =
        [new("temperature", "test/temperature", "Double", 37.5, "Good", "2026-10-13T00:00:00Z", "成功", null)];

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition()) await Task.Delay(40, timeout.Token);
    }
    private static async Task DurableRecoveryAsync(string root)
    {
        await using var broker = new BrokerFixture();
        var directory = Path.Combine(root, "recovery");
        string? messageId;
        using (var publisher = Publisher(directory, broker.Port))
        {
            await publisher.StartAsync(CancellationToken.None);
            var receipt = await publisher.PublishCollectionBatchAsync("restart-device", DateTimeOffset.UtcNow, Points);
            Check(receipt.Queued && receipt.MessageId is not null, "Offline data was not durably accepted");
            messageId = receipt.MessageId;
            await UntilAsync(() => publisher.GetStatus().FailedCount > 0);
            Check(publisher.GetStatus().PendingCount == 1, "Failed delivery removed pending data");
            Check(Directory.GetFiles(directory, "*.json").Length == 1, "Pending batch missing from disk");
            await publisher.StopAsync(CancellationToken.None);
        }
        await broker.StartAsync();
        using var recovered = Publisher(directory, broker.Port);
        await recovered.StartAsync(CancellationToken.None);
        var received = await broker.NextAsync();
        Check(received.Payload.GetProperty("messageId").GetString() == messageId, "Restart changed the message ID");
        Check(received.Topic == "machines/telemetry/restart-device" && received.Qos == 1, "Recovered topic or QoS changed");
        Check(received.Payload.GetProperty("points")[0].GetProperty("value").GetDouble() == 37.5, "Recovered payload changed");
        await UntilAsync(() => recovered.GetStatus().PendingCount == 0);
        await broker.StopAsync();
        var waiting = await recovered.PublishCollectionBatchAsync("outage-device", DateTimeOffset.UtcNow, Points);
        await UntilAsync(() => recovered.GetStatus().FailedCount > 0);
        await broker.StartAsync();
        received = await broker.NextAsync();
        Check(received.Payload.GetProperty("messageId").GetString() == waiting.MessageId, "Background retry lost stable ID");
        await UntilAsync(() => recovered.GetStatus().PendingCount == 0);
        await CheckConcurrentAsync(recovered, broker);
        Check(recovered.GetStatus().SentCount == 14, "Sent batch count is incorrect");
        await recovered.StopAsync(CancellationToken.None);
    }
    private static async Task CheckConcurrentAsync(MqttTelemetryPublisher publisher, BrokerFixture broker)
    {
        var receipts = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            publisher.PublishCollectionBatchAsync($"concurrent-{index}", DateTimeOffset.UtcNow, Points)));
        Check(receipts.All(receipt => receipt.Queued), "Concurrent enqueue failed");
        var expected = receipts.Select(receipt => receipt.MessageId).ToHashSet();
        var actual = new HashSet<string?>();
        foreach (var receipt in receipts)
        {
            var received = await broker.NextAsync();
            actual.Add(received.Payload.GetProperty("messageId").GetString());
            Check(received.Topic == "machines/telemetry/" + received.Payload.GetProperty("deviceId").GetString(), "Device routing changed");
        }
        Check(actual.SetEquals(expected), "Concurrent batches were dropped or duplicated");
        await UntilAsync(() => publisher.GetStatus().PendingCount == 0);
    }
}
