namespace IndustrialIoT.Infrastructure.Messaging;

using System.Text.Json;
using IndustrialIoT.Messaging;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

public sealed class MqttOutputOptions
{
    public const string SectionName = "DataOutput:Mqtt";

    public bool Enabled { get; set; } = true;
    public string OutboxDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "App_Data", "mqtt-outbox");
    public int ConnectTimeoutSeconds { get; set; } = 5;
    public int PublishTimeoutSeconds { get; set; } = 10;
    public string BrokerHost { get; set; } = "localhost";
    public int BrokerPort { get; set; } = 1883;
    public string ClientId { get; set; } = $"industrial-iot-{Environment.MachineName}";
    public string TopicPattern { get; set; } = "industrial-iot/devices/{deviceId}/data";
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class MqttDataOutput : DurableMqttOutbox, IDataOutput, IAsyncDisposable
{
    private readonly MqttOutputOptions _options;
    private readonly ILogger<MqttDataOutput> _logger;
    private readonly IMqttClient _client;
    private readonly JsonSerializerOptions _jsonOptions;
    private int _disposeStarted;

    public string Name => "MQTT";
    protected override bool Enabled => _options.Enabled;
    protected override bool IsConnected => _client.IsConnected;

    public MqttDataOutput(IOptions<MqttOutputOptions> options, ILogger<MqttDataOutput> logger)
        : base(options.Value.OutboxDirectory, logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new MqttClientFactory().CreateMqttClient();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    public Task InitializeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task WriteAsync(CollectedDataBatch batch, CancellationToken ct)
    {
        if (!Enabled) return;
        var messageId = Guid.NewGuid().ToString("N");
        var topic = _options.TopicPattern.Replace("{deviceId}", batch.DeviceId);
        var payload = JsonSerializer.Serialize(new { messageId, batch.DeviceId, batch.GroupName,
            batch.Values, batch.CollectedAt, batch.CollectionDuration }, _jsonOptions);
        var result = await EnqueueAsync(batch.DeviceId, topic, payload, 1, messageId, ct);
        if (!result.Queued) throw new IOException(result.Reason ?? "MQTT batch could not be persisted.");
    }

    public Task FlushAsync(CancellationToken ct)
    {
        // WriteAsync flushes each accepted batch to disk before returning.
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        base.Dispose();
        _client.DisconnectedAsync -= OnDisconnectedAsync;
        _client.Dispose();
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
            .WithClientId(_options.ClientId)
            .WithCleanSession(true);

        if (!string.IsNullOrEmpty(_options.Username))
        {
            optionsBuilder.WithCredentials(_options.Username, _options.Password);
        }

        var mqttOptions = optionsBuilder.Build();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ConnectTimeoutSeconds)));
        var result = await _client.ConnectAsync(mqttOptions, timeout.Token);
        if ((int)result.ResultCode >= 128) throw new IOException($"MQTT connection rejected: {result.ResultCode}");
    }

    protected override async Task DeliverAsync(MqttOutboxMessage message, CancellationToken ct)
    {
        if (!_client.IsConnected)
        {
            _logger.LogInformation("MQTT reconnecting to {Host}:{Port}", _options.BrokerHost, _options.BrokerPort);
            await ConnectAsync(ct);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.PublishTimeoutSeconds)));
        var mqttMessage = new MqttApplicationMessageBuilder().WithTopic(message.Topic)
            .WithPayload(message.Payload)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)message.QualityOfService).Build();
        var result = await _client.PublishAsync(mqttMessage, timeout.Token);
        if ((int)result.ReasonCode >= 128) throw new IOException($"MQTT publish rejected: {result.ReasonCode}");
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        if (args.ClientWasConnected)
        {
            _logger.LogWarning("MQTT disconnected (reason: {Reason}). Queued batches will retry in the background.",
                args.Reason);
        }

        await Task.CompletedTask;
    }
}
