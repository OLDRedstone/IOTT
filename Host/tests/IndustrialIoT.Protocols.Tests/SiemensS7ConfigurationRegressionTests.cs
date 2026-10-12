using System.Net;
using System.Net.Sockets;
using IndustrialIoT.Application.Commands;
using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Infrastructure.BackgroundServices;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Registration;
using IndustrialIoT.Protocols.SiemensS7;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

internal static class SiemensS7ConfigurationRegressionTests
{
    public static async Task RunAsync()
    {
        AliasesAndValidation();
        await PersistenceAsync();
        await WireAsync();
        await LegacyConnectionEntriesAsync();
        await LegacyVerificationReadAsync();
    }

    private static void AliasesAndValidation()
    {
        (string Alias, string Cpu, byte Slot)[] cases =
        [
            ("S7-1200", "S1200", 0), ("s7_1500", "S1500", 0), ("S7-300", "S300", 2),
            ("S7-400", "S400", 3), (" s7-200 SMART ", "S200Smart", 0), ("S7-200", "S200", 0),
        ];
        foreach (var sample in cases)
        {
            var properties = SiemensS7ConnectionOptions.Normalize(ProtocolType.SiemensS7, sample.Alias, []);
            var options = SiemensS7ConnectionOptions.Resolve(properties);
            TestSupport.Require(options == new SiemensS7ConnectionOptions(sample.Cpu, 0, sample.Slot),
                $"Top-level model {sample.Alias} lost its S7 family or default slot");
            var explicitCpu = SiemensS7ConnectionOptions.Normalize(ProtocolType.SiemensS7, "CPU order number",
                new() { ["cPuTyPe"] = sample.Alias });
            TestSupport.Require(explicitCpu["PlcType"] == sample.Cpu && !explicitCpu.ContainsKey("cPuTyPe"),
                "Explicit CPU must override free-text model and normalize legacy property casing");
        }
        TestSupport.Require(SiemensS7ConnectionOptions.Resolve(new Dictionary<string, string>()).PlcType == "S1200",
            "A configuration with no CPU or model must retain the documented S1200 default");
        Dictionary<string, string>[] invalid =
        [
            new() { ["PlcType"] = "S7301" }, new() { ["PlcType"] = "999" }, new() { ["PlcType"] = "" },
            new() { ["Rack"] = "-1" }, new() { ["Rack"] = "8" }, new() { ["Slot"] = "32" },
            new() { ["Slot"] = "1.5" }, new() { ["PlcType"] = "S200Smart", ["Slot"] = "1" },
            new() { ["Model"] = "Unknown CPU" },
        ];
        foreach (var properties in invalid)
        {
            try { SiemensS7ConnectionOptions.Resolve(properties); throw new InvalidOperationException("Invalid S7 options accepted"); }
            catch (ArgumentException) { }
        }
    }

    private static async Task PersistenceAsync()
    {
        var repository = new DeviceRepository();
        var properties = new Dictionary<string, string> { ["deviceCode"] = "S7-1" };
        var created = await new CreateDeviceCommandHandler(repository).Handle(
            new CreateDeviceCommand(Request("S7-300", properties)), CancellationToken.None);
        TestSupport.Require(created.ExtendedProperties["Model"] == "S7-300" && !properties.ContainsKey("Model"),
            "Creation must carry Model into the driver config without mutating the request dictionary");
        var handler = new UpdateDeviceCommandHandler(repository);
        var updated = await handler.Handle(new UpdateDeviceCommand(created.Id, new() { Model = "S7-400" }), CancellationToken.None);
        TestSupport.Require(SiemensS7ConnectionOptions.Resolve(updated.ExtendedProperties).Slot == 3
            && !updated.ExtendedProperties.ContainsKey("Slot"), "Changing inferred CPU must not preserve an old default slot");
        updated = await handler.Handle(new UpdateDeviceCommand(created.Id, new()
        {
            Model = "CPU order number", ExtendedProperties = new()
                { ["Series"] = "S7-300", ["Rack"] = "1", ["Slot"] = "5", ["StringLength"] = "40" },
        }), CancellationToken.None);
        TestSupport.Require(SiemensS7ConnectionOptions.Resolve(updated.ExtendedProperties) == new SiemensS7ConnectionOptions("S300", 1, 5)
            && updated.ExtendedProperties["StringLength"] == "40", "Explicit CPU and custom settings must survive editing");
        var writes = repository.Writes;
        try
        {
            await handler.Handle(new UpdateDeviceCommand(created.Id, new()
                { Model = "changed", Name = "changed", ExtendedProperties = new() { ["PlcType"] = "bad" } }), CancellationToken.None);
            throw new InvalidOperationException("Invalid update accepted");
        }
        catch (ArgumentException) { }
        TestSupport.Require(repository.Writes == writes && repository.Stored.Model == "CPU order number" && repository.Stored.Name == "PLC",
            "Rejected S7 update must not mutate a tracked device or persist changes");
        await handler.Handle(new UpdateDeviceCommand(created.Id, new() { Protocol = ProtocolType.ModbusTCP }), CancellationToken.None);
        TestSupport.Require(!repository.Stored.ConnectionConfig.ExtendedProperties.ContainsKey("PlcType"),
            "Switching away from S7 must clear inherited CPU settings");
        repository.Stored.ConnectionConfig.ExtendedProperties["Series"] = "H3U";
        updated = await handler.Handle(new UpdateDeviceCommand(created.Id, new()
            { Protocol = ProtocolType.SiemensS7, Model = "S7-200Smart" }), CancellationToken.None);
        TestSupport.Require(SiemensS7ConnectionOptions.Resolve(updated.ExtendedProperties).PlcType == "S200Smart",
            "Switching into S7 must not interpret the previous protocol's Series as a CPU");
        var legacy = new DeviceConnectionConfig { Host = "127.0.0.1", Port = 102 };
        var resolved = SiemensS7ConnectionOptions.ForConnection(ProtocolType.SiemensS7, "S7-400", legacy);
        TestSupport.Require(SiemensS7ConnectionOptions.Resolve(resolved.ExtendedProperties).Slot == 3 && legacy.ExtendedProperties.Count == 0,
            "Legacy stored devices must receive Model at connection time without modifying the stored configuration");
    }

    private static async Task LegacyConnectionEntriesAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var repository = new DeviceRepository();
        await new CreateDeviceCommandHandler(repository).Handle(new(Request("S7-400")
            with { Port = ((IPEndPoint)listener.LocalEndpoint).Port }), deadline.Token);
        repository.Stored.ConnectionConfig = repository.Stored.ConnectionConfig with { ExtendedProperties = [] };
        var factory = new S7DriverFactory();
        var captured = CaptureHandshakeAsync(listener, deadline.Token);
        var result = await new TestConnectionCommandHandler(repository, factory).Handle(new(repository.Stored.Id), deadline.Token);
        TestSupport.Require(result.Success && (await captured)[^1] == 3,
            "The single-connection test must use a legacy device's top-level model");
        using var provider = new ServiceCollection().AddSingleton<IDeviceRepository>(repository).BuildServiceProvider();
        using var pool = new ConnectionPoolService(factory, provider, NullLogger<ConnectionPoolService>.Instance);
        captured = CaptureHandshakeAsync(listener, deadline.Token);
        try
        {
            await pool.GetOrCreateAsync(repository.Stored.Id, deadline.Token);
            TestSupport.Require((await captured)[^1] == 3, "Pooled connections must also normalize legacy S7 devices");
        }
        finally { await pool.ReleaseAsync(repository.Stored.Id, deadline.Token); }
        TestSupport.Require(repository.Stored.ConnectionConfig.ExtendedProperties.Count == 0,
            "Connection-time normalization must not mutate the stored device");
        repository.Stored.Model = "Unknown CPU";
        result = await new TestConnectionCommandHandler(repository, factory).Handle(new(repository.Stored.Id), deadline.Token);
        TestSupport.Require(!result.Success && result.ErrorMessage!.Contains("Unknown S7 CPU") && !listener.Pending(),
            "Invalid stored CPU must return a clear connection-test failure before opening a socket");
    }

    private static async Task LegacyVerificationReadAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var repository = new DeviceRepository();
        await new CreateDeviceCommandHandler(repository).Handle(new(Request("S7-400")
            with { Port = ((IPEndPoint)listener.LocalEndpoint).Port }), deadline.Token);
        repository.Stored.ConnectionConfig = repository.Stored.ConnectionConfig with { ExtendedProperties = [] };
        var fingerprint = VerificationConfiguration.Fingerprint(repository.Stored);
        var revision = VerificationConfiguration.Revision(repository.Stored, repository.Stored, false);
        var expected = new VerificationExpectation { ConfigurationFingerprint = fingerprint, Revision = revision };
        var captured = CaptureHandshakeAsync(listener, deadline.Token, 42);
        var result = await new VerificationDriverExecutor(repository, new S7DriverFactory()).ExecuteAsync(
            repository.Stored.Id, expected, false, "read-tag", protocol => protocol == ProtocolType.SiemensS7,
            driver => driver.Capabilities.HasFlag(DriverCapabilities.Read),
            driver => driver.ReadTagAsync("MB0", DataType.UInt8, deadline.Token), deadline.Token);
        var packet = await captured;
        TestSupport.Require(result.StatusCode == 200 && result.Value is { Quality: TagQuality.Good, Value: byte actual }
            && actual == 42 && packet[^1] == 3,
            "Automatic verification must read a legacy S7-400 through its model-derived slot");
        TestSupport.Require(result.Evidence.ConfigurationMatched && result.Evidence.OperationAttempted
            && result.Evidence.ConfigurationFingerprint == fingerprint && result.Evidence.Revision == revision,
            "Verification evidence must retain the original frozen configuration identity");
        TestSupport.Require(repository.Stored.ConnectionConfig.ExtendedProperties.Count == 0
            && VerificationConfiguration.Fingerprint(repository.Stored) == fingerprint
            && VerificationConfiguration.Revision(repository.Stored, repository.Stored, false) == revision,
            "Verification-time S7 normalization must not mutate the stored configuration or its digests");
    }

    private sealed class S7DriverFactory : IProtocolDriverFactory
    {
        public IProtocolDriver Create(ProtocolType protocol, string brand, string? model = null) =>
            new SiemensS7Driver(NullLogger<SiemensS7Driver>.Instance);
    }

    private static async Task WireAsync()
    {
        foreach (var model in new[] { "S7-300", "S7-400", "S7-200Smart" })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var repository = new DeviceRepository();
            var request = Request(model, model == "S7-300" ? new() { ["Rack"] = "1", ["Slot"] = "5" } : null)
                with { Port = ((IPEndPoint)listener.LocalEndpoint).Port };
            await new CreateDeviceCommandHandler(repository).Handle(new(request), deadline.Token);
            var captured = CaptureHandshakeAsync(listener, deadline.Token);
            await using var driver = new SiemensS7Driver(NullLogger<SiemensS7Driver>.Instance);
            var connected = await driver.ConnectAsync(repository.Stored.ConnectionConfig, deadline.Token);
            TestSupport.Require(connected.Success, connected.ErrorMessage ?? "S7 handshake failed");
            var packet = await captured;
            var correct = model == "S7-200Smart" ? packet[15] == 0xC2 && packet[17] == 3 && packet[18] == 0
                : packet[^1] == (model == "S7-300" ? 0x25 : 3);
            TestSupport.Require(correct, $"Stored {model} configuration produced the wrong COTP target TSAP");
        }
    }

    private static async Task<byte[]> CaptureHandshakeAsync(TcpListener listener, CancellationToken ct, byte? readValue = null)
    {
        using var socket = await listener.AcceptTcpClientAsync(ct);
        using var stream = socket.GetStream();
        var cotp = await ReadPacketAsync(stream, ct);
        await stream.WriteAsync(new byte[] { 3, 0, 0, 22, 17, 0xD0, 0, 1, 0, 1, 0, 0xC0, 1, 10, 0xC1, 2, 1, 2, 0xC2, 2, 1, 0 }, ct);
        await ReadPacketAsync(stream, ct);
        await stream.WriteAsync(new byte[] { 3, 0, 0, 27, 2, 0xF0, 0x80, 0x32, 3, 0, 0, 0, 1, 0, 8, 0, 0, 0, 0, 0xF0, 0, 0, 1, 0, 1, 1, 0xE0 }, ct);
        if (readValue.HasValue)
        {
            var request = await ReadPacketAsync(stream, ct);
            TestSupport.Require(request.Length == 31 && request[17] == 4 && request[18] == 1,
                "Verification must perform a single S7 variable read after initialization");
            byte[] response =
            [
                3, 0, 0, 26, 2, 0xF0, 0x80, 0x32, 3, 0, 0, request[11], request[12],
                0, 2, 0, 5, 0, 0, 4, 1, 0xFF, 4, 0, 8, readValue.Value,
            ];
            await stream.WriteAsync(response, ct);
        }
        return cotp;
    }

    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var packet = new byte[(header[2] << 8) | header[3]];
        header.CopyTo(packet, 0);
        await stream.ReadExactlyAsync(packet.AsMemory(4), ct);
        return packet;
    }

    private static CreateDeviceRequest Request(string model, Dictionary<string, string>? properties = null) => new()
    {
        Name = "PLC", Type = DeviceType.PLC, Brand = "Siemens", Model = model, Protocol = ProtocolType.SiemensS7,
        Host = "127.0.0.1", Port = 102, ConnectTimeoutMs = 2000, ReadTimeoutMs = 2000, ExtendedProperties = properties,
    };

    private sealed class DeviceRepository : IDeviceRepository
    {
        public Device Stored { get; private set; } = null!;
        public int Writes { get; private set; }
        public Task<Device?> GetByIdAsync(string id, CancellationToken ct = default) => Task.FromResult(Stored?.Id == id ? Stored : null);
        public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Device>>([Stored]);
        public Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default) => GetAllAsync(ct);
        public Task AddAsync(Device device, CancellationToken ct = default) { Stored = device; Writes++; return Task.CompletedTask; }
        public Task UpdateAsync(Device device, CancellationToken ct = default) { Stored = device; Writes++; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
