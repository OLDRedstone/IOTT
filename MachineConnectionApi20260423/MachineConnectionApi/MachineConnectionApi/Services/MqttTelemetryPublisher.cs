using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IndustrialIoT.Messaging;
using MachineConnectionApi.Options;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MachineConnectionApi.Services;

public interface IMqttTelemetryPublisher
{
    /// <summary>将采集批次持久化入队，后台收到 MQTT 确认后移除。</summary>
    Task<MqttEnqueueResult> PublishCollectionBatchAsync(
        string deviceId,
        DateTimeOffset collectedAt,
        IReadOnlyList<InfluxTelemetryPoint> points,
        CancellationToken ct = default);
}

public sealed class MqttTelemetryPublisher : DurableMqttOutbox, IMqttTelemetryPublisher
{
    private readonly IOptionsMonitor<MqttOptions> _options;
    private readonly ILogger<MqttTelemetryPublisher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IMqttClient _client;
    private string? _connectFingerprint;
    private int _disposed;
    protected override bool Enabled => _options.CurrentValue.Enabled;
    protected override bool IsConnected => _client.IsConnected;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MqttTelemetryPublisher(
        IOptionsMonitor<MqttOptions> options,
        ILogger<MqttTelemetryPublisher> logger)
        : base(options.CurrentValue.OutboxDirectory, logger)
    {
        _options = options;
        _logger = logger;
        _client = new MqttFactory().CreateMqttClient();
    }

    public async Task<MqttEnqueueResult> PublishCollectionBatchAsync(
        string deviceId,
        DateTimeOffset collectedAt,
        IReadOnlyList<InfluxTelemetryPoint> points,
        CancellationToken ct = default)
    {
        var opt = _options.CurrentValue;
        if (!opt.Enabled || points.Count == 0)
            return new(false, null, opt.Enabled ? "Empty batch" : "MQTT disabled");

        var messageId = Guid.NewGuid().ToString("N");
        var body = new MqttTelemetryBatchDto(
            messageId,
            deviceId,
            collectedAt,
            points.Select(static p => new MqttTelemetryPointDto(
                p.Name,
                p.Path,
                p.DataType,
                p.Value,
                p.Quality,
                p.Timestamp,
                p.Status,
                p.ErrorMessage)).ToList());

        var json = JsonSerializer.Serialize(body, JsonOptions);
        var topic = BuildTopic(opt.TopicPrefix, deviceId);
        var qos = ToQos(opt.QualityOfService);
        return await EnqueueAsync(deviceId, topic, json, (int)qos, messageId, ct);
    }

    protected override async Task DeliverAsync(MqttOutboxMessage pending, CancellationToken ct)
    {
        var opt = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(opt.Host))
            throw new InvalidOperationException("MQTT Host is empty; queued messages are retained.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, opt.PublishTimeoutSeconds)));
        ct = timeout.Token;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await EnsureConnectedAsync(opt, ct).ConfigureAwait(false);
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(pending.Topic)
                    .WithPayload(pending.Payload)
                    .WithQualityOfServiceLevel(ToQos(pending.QualityOfService))
                    .WithRetainFlag(false)
                    .Build();

                var result = await _client.PublishAsync(message, ct).ConfigureAwait(false);
                if ((int)result.ReasonCode >= 128)
                    throw new IOException($"MQTT publish rejected: {result.ReasonCode}");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "MQTT 发布失败，保留待发消息: messageId={MessageId}, deviceId={DeviceId}, topic={Topic}",
                    pending.MessageId, pending.DeviceId, pending.Topic);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(MqttOptions opt, CancellationToken ct)
    {
        var connectOptions = BuildConnectOptions(opt);
        var fingerprint = ConnectFingerprint(opt);
        if (_client.IsConnected && string.Equals(_connectFingerprint, fingerprint, StringComparison.Ordinal))
            return;

        if (_client.IsConnected)
            await _client.DisconnectAsync(cancellationToken: ct).ConfigureAwait(false);

        var timeout = TimeSpan.FromSeconds(Math.Max(3, opt.ConnectTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        await _client.ConnectAsync(connectOptions, linked.Token).ConfigureAwait(false);
        _connectFingerprint = fingerprint;
        _logger.LogInformation(
            "MQTT 已连接: {Host}:{Port}, TLS={Tls}, ClientId={ClientId}",
            opt.Host,
            opt.Port,
            opt.UseTls,
            opt.ClientId);
    }

    private static MqttClientOptions BuildConnectOptions(MqttOptions opt)
    {
        var clientId = string.IsNullOrWhiteSpace(opt.ClientId)
            ? "MachineConnectionApi"
            : opt.ClientId.Trim();

        var b = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithClientId(clientId);

        if (opt.UseTls)
            b = b.WithTlsOptions(o => o.UseTls());
        b = b.WithTcpServer(opt.Host.Trim(), opt.Port);

        if (!string.IsNullOrEmpty(opt.Username))
            b = b.WithCredentials(opt.Username, opt.Password ?? "");

        return b.Build();
    }

    private static string ConnectFingerprint(MqttOptions opt) =>
        $"{opt.Host}|{opt.Port}|{opt.UseTls}|{opt.Username}|{opt.Password}|{opt.ClientId}";

    private static MqttQualityOfServiceLevel ToQos(int q) =>
        q switch
        {
            2 => MqttQualityOfServiceLevel.ExactlyOnce,
            _ => MqttQualityOfServiceLevel.AtLeastOnce,
        };

    private static string BuildTopic(string prefix, string deviceId)
    {
        var p = (prefix ?? "machines/telemetry").Trim().TrimEnd('/');
        var id = SafeTopicSegment(deviceId);
        return $"{p}/{id}";
    }

    private static string SafeTopicSegment(string deviceId)
    {
        var s = (deviceId ?? "").Trim();
        if (s.Length == 0) return "_";
        Span<char> buffer = stackalloc char[Math.Min(s.Length, 160)];
        var n = 0;
        foreach (var ch in s)
        {
            if (n >= buffer.Length) break;
            buffer[n++] = ch is '/' or '\\' or '+' or '#' or ' ' or '\t' ? '_' : ch;
        }

        return new string(buffer[..n]);
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        base.Dispose();
        try
        {
            // 应用关停路径上的同步等待需设上限，broker 无响应时不能卡住整个进程退出
            if (_client.IsConnected &&
                !_client.DisconnectAsync().Wait(TimeSpan.FromSeconds(3)))
                _logger.LogDebug("MQTT 断开超时（3s），直接释放客户端");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MQTT 断开时异常（可忽略）");
        }

        _client.Dispose();
        _gate.Dispose();
    }
}

internal sealed record MqttTelemetryBatchDto(
    string MessageId,
    string DeviceId,
    DateTimeOffset CollectedAt,
    IReadOnlyList<MqttTelemetryPointDto> Points);

internal sealed record MqttTelemetryPointDto(
    string Name,
    string Path,
    string DataType,
    object? Value,
    string Quality,
    string Timestamp,
    string Status,
    string? ErrorMessage);
