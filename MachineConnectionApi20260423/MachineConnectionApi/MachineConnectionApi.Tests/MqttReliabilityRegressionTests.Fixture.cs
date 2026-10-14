namespace MachineConnectionApi.Tests;

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Server;

internal static partial class MqttReliabilityRegressionTests
{
    private sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>, IOptionsSnapshot<T> where T : class
    {
        public T CurrentValue => value;
        public T Value => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
    private sealed record Received(string Topic, int Qos, JsonElement Payload);
    private sealed class BrokerFixture : IAsyncDisposable
    {
        private readonly MqttFactory _factory = new();
        private readonly MqttServer _server;
        private readonly IMqttClient _subscriber;
        private readonly Channel<Received> _received = Channel.CreateUnbounded<Received>();
        private bool _running;
        public int Port { get; }
        public BrokerFixture()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _server = _factory.CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
                .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
                .WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback).WithDefaultEndpointPort(Port).Build());
            _subscriber = _factory.CreateMqttClient();
            _subscriber.ApplicationMessageReceivedAsync += args =>
            {
                var message = args.ApplicationMessage;
                _received.Writer.TryWrite(new(message.Topic, (int)message.QualityOfServiceLevel,
                    JsonDocument.Parse(message.PayloadSegment).RootElement.Clone()));
                return Task.CompletedTask;
            };
        }
        public async Task StartAsync()
        {
            await _server.StartAsync();
            _running = true;
            await _subscriber.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", Port)
                .WithClientId("mqtt-regression-subscriber").Build());
            await _subscriber.SubscribeAsync(_factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter("machines/telemetry/#", MqttQualityOfServiceLevel.AtLeastOnce).Build());
        }
        public async Task StopAsync()
        {
            if (!_running) return;
            await _server.StopAsync(new MqttServerStopOptions());
            _running = false;
            await UntilAsync(() => !_subscriber.IsConnected);
        }
        public async Task<Received> NextAsync() =>
            await _received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(12));
        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _subscriber.Dispose();
            _server.Dispose();
        }
    }
}
