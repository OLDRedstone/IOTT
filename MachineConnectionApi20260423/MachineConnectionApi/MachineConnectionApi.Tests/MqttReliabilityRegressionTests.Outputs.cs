namespace MachineConnectionApi.Tests;

using System.Text.Json;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Options;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class MqttReliabilityRegressionTests
{
    private static async Task StorageIndependenceAsync(string root)
    {
        await using var broker = new BrokerFixture();
        await broker.StartAsync();
        using var publisher = Publisher(Path.Combine(root, "storage"), broker.Port);
        await publisher.StartAsync(CancellationToken.None);
        var writer = new TestWriter(fail: true);
        var controller = Controller(publisher, writer, storage: true);
        var response = await controller.WriteBatch(Request(), CancellationToken.None);
        var result = Result(response, 200);
        Check(result.GetProperty("storageError").GetString() == "fixture storage failure", "Storage failure not reported");
        Check(result.GetProperty("mqttQueued").GetBoolean(), "Storage failure prevented MQTT enqueue");
        Check(!result.GetProperty("mqttPublished").GetBoolean(), "Enqueue incorrectly reported as delivery");
        var received = await broker.NextAsync();
        Check(received.Payload.GetProperty("messageId").GetString() == result.GetProperty("mqttMessageId").GetString(), "Storage failure blocked real delivery");
        controller = Controller(publisher, writer, storage: false);
        result = Result(await controller.WriteBatch(Request(), CancellationToken.None), 200);
        Check(!result.GetProperty("storageEnabled").GetBoolean() && result.GetProperty("written").GetInt32() == 0, "Disabled storage result wrong");
        Check(result.GetProperty("mqttQueued").GetBoolean() && writer.Calls == 1, "Disabled storage interfered with MQTT");
        await broker.NextAsync();
        await UntilAsync(() => publisher.GetStatus().PendingCount == 0);
        using var disabledWriter = new InfluxTelemetryWriter(new FixedOptions<InfluxDbOptions>(new() { Enabled = false }),
            NullLogger<InfluxTelemetryWriter>.Instance);
        controller = Controller(publisher, disabledWriter, storage: true);
        result = Result(await controller.WriteBatch(Request(), CancellationToken.None), 200);
        Check(result.GetProperty("written").GetInt32() == 0, "Changed storage options falsely reported a write");
        await broker.NextAsync();
        await UntilAsync(() => publisher.GetStatus().PendingCount == 0);
        await publisher.StopAsync(CancellationToken.None);
        var blockedPath = Path.Combine(root, "blocked-outbox");
        await File.WriteAllTextAsync(blockedPath, "This file prevents creation of a queue directory.");
        using var blocked = Publisher(blockedPath, broker.Port);
        controller = Controller(blocked, new TestWriter(fail: false), storage: true);
        result = Result(await controller.WriteBatch(Request(), CancellationToken.None), 200);
        Check(result.GetProperty("written").GetInt32() == 1 && !result.GetProperty("mqttQueued").GetBoolean(), "MQTT disk failure blocked independent storage");
        Check(!string.IsNullOrEmpty(result.GetProperty("mqttError").GetString()), "MQTT disk failure hidden");
        controller = Controller(blocked, writer, storage: false);
        Result(await controller.WriteBatch(Request(), CancellationToken.None), 502);
    }
    private static TelemetryInfluxController Controller(IMqttTelemetryPublisher publisher, IInfluxTelemetryWriter writer, bool storage) => new(
        writer, publisher, new FixedOptions<InfluxDbOptions>(new() { Enabled = storage }),
        new FixedOptions<MqttOptions>(new() { Enabled = true }), new UnusedHttpClients(),
        NullLogger<TelemetryInfluxController>.Instance);

    private static InfluxTelemetryBatchRequest Request() => new()
    {
        DeviceId = "storage-failure-device", CollectedAt = DateTimeOffset.UtcNow,
        Points = [new() { Name = "temperature", Path = "test/temperature", DataType = "Double",
            Value = 37.5, Quality = "Good", Timestamp = "2026-10-13T00:00:00Z", Status = "成功" }]
    };
    private static JsonElement Result(IActionResult action, int status)
    {
        Check(action is ObjectResult { } response && response.StatusCode == status, $"Unexpected HTTP result, wanted {status}");
        return JsonSerializer.SerializeToElement(((ObjectResult)action).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    private sealed class TestWriter(bool fail) : IInfluxTelemetryWriter
    {
        public int Calls { get; private set; }
        public Task WriteBatchAsync(string deviceId, DateTimeOffset batchTime, IReadOnlyList<InfluxTelemetryPoint> points, CancellationToken ct = default)
        {
            Calls++;
            return fail ? Task.FromException(new IOException("fixture storage failure")) : Task.CompletedTask;
        }
    }
    private sealed class UnusedHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Batch API should not perform HTTP queries");
    }
}
