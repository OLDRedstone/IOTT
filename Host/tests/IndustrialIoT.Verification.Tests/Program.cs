using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Host.Controllers;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

await VerificationReadRegression.RunAll();
await MachineConnectionApi.Tests.EvaluationPipelineRegressionTests.RunReadContractOnly();
Console.WriteLine("PASS: Host frozen-read fixtures and Gateway HTTP read/directory/alias contract (fake only; no transfers or live devices).");

internal static class VerificationReadRegression
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TagReadRequest[] Tags = [new() { Address = "fixture:1", DataType = DataType.Int32 }];
    private static int _cases;

    public static async Task RunAll()
    {
        FingerprintContract();
        await StructuredEvidence();
        await SimulationCannotMasquerade();
        await ConfigChangedBeforeRead();
        await ConfigChangedDuringConnect();
        await ConcurrentTargetChanges();
        await CredentialsAndExtendedPropertiesAreFrozen();
        await FreshRepositoryScopes();
        await DirectoryContract();
        await ControllerAndLegacyDevelopmentRead();
        await GatewayToHostContract();
        await Cancellation();
        Console.WriteLine($"PASS: {_cases} Host verification assertions.");
    }

    private static Device Device(string id = "fixture-machine", string host = "192.0.2.11", ProtocolType protocol = ProtocolType.ModbusTCP) => new()
    {
        Id = id, Name = "fixture", Type = DeviceType.CNC, Brand = "fixture-brand", Model = "fixture-model", Protocol = protocol,
        ConnectionConfig = new() { Host = host, Port = 502, Username = "fixture-user", Password = "not-a-real-secret",
            ExtendedProperties = new() { ["second"] = "two", ["first"] = "one" } },
    };
    private static VerificationExpectation Expected(Device device, string? revision = null) =>
        new() { ConfigurationFingerprint = VerificationConfiguration.Fingerprint(device), Revision = revision };
    private static Task<VerificationExecution<IReadOnlyList<TagValue>>> Read(VerificationDriverExecutor executor, Device device,
        VerificationExpectation? expected = null, CancellationToken ct = default) => executor.ExecuteAsync(device.Id,
        expected ?? Expected(device), false, "application-read", _ => true,
        driver => driver.Capabilities.HasFlag(DriverCapabilities.Read), driver => driver.ReadTagsAsync(Tags, ct), ct);
    private static void Assert(bool value, string message)
    {
        _cases++;
        if (!value) throw new InvalidOperationException(message);
    }

    private static void FingerprintContract()
    {
        var device = Device();
        device.ConnectionConfig = device.ConnectionConfig with { Transfer = new()
        {
            Protocol = ProtocolType.FTP, Host = "192.0.2.12", Port = 21, Username = "file-user", Password = "file-secret",
            ExtendedProperties = new() { ["z"] = "last", ["a"] = "first" },
        } };
        var config = device.ConnectionConfig;
        var transfer = config.Transfer!;
        var target = new VerifyMeasurementTarget
        {
            Id = device.Id, Brand = device.Brand, Model = device.Model, Host = config.Host, Port = config.Port,
            Protocol = device.Protocol.ToString(), Username = config.Username, ConnectTimeoutMs = (int)config.ConnectTimeout.TotalMilliseconds,
            ReadTimeoutMs = (int)config.ReadTimeout.TotalMilliseconds,
            ExtendedProperties = config.ExtendedProperties.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value),
            Transfer = new() { Protocol = transfer.Protocol.ToString(), Host = transfer.Host, Port = transfer.Port, Username = transfer.Username,
                ConnectTimeoutMs = (int)transfer.ConnectTimeout.TotalMilliseconds, ReadTimeoutMs = (int)transfer.ReadTimeout.TotalMilliseconds,
                ExtendedProperties = transfer.ExtendedProperties.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value) },
        };
        Assert(VerificationConfiguration.Fingerprint(device) == HttpVerifyMeasurementAdapter.ConfigurationFingerprint(target),
            "Host/Gateway fingerprint contract differs, including property order/inline transfer fields");
        var revision = VerificationConfiguration.Revision(device, device, false);
        var changed = VerificationConfiguration.Snapshot(device);
        changed.ConnectionConfig = changed.ConnectionConfig with { Password = "different-secret" };
        Assert(VerificationConfiguration.Fingerprint(device) == VerificationConfiguration.Fingerprint(changed)
            && revision != VerificationConfiguration.Revision(changed, changed, false), "Secret changes were not privately revision-bound");
        target.VerificationReadRevision = revision;
        Assert(!JsonSerializer.Serialize(target, Json).Contains(revision), "Opaque revision leaked into run DTO serialization");
    }

    private static async Task StructuredEvidence()
    {
        var device = Device();
        var repo = new Repository(device);
        var driver = new FixtureDriver();
        var factory = new Factory(() => driver);
        var executor = new VerificationDriverExecutor(repo, factory);
        var result = await Read(executor, device);
        Assert(result.StatusCode == 200 && result.Evidence is { OperationAttempted: true, ConfigurationMatched: true, Simulated: false }, "Actual fixture read lacked explicit evidence");
        Assert(driver.Reads == 1 && driver.Disposed && driver.Config?.Host == device.ConnectionConfig.Host, "Read did not use/close its frozen private driver");
        driver = new FixtureDriver { ConnectSuccess = false };
        result = await Read(executor, device);
        Assert(result.StatusCode == 502 && !result.Evidence.OperationAttempted && result.Evidence.Code == "initialization-failed", "Connect failure counted as a device read");
        driver = new FixtureDriver { ReadError = new IOException("Read failed: password=not-a-real-secret") };
        result = await Read(executor, device);
        Assert(result.StatusCode == 502 && result.Evidence.OperationAttempted && result.Evidence.Code == "operation-failed", "Executed read failure lost its operation marker");
        Assert(!JsonSerializer.Serialize(result, Json).Contains("not-a-real-secret"), "Raw SDK exception leaked credentials");
        driver = new FixtureDriver { ReadError = new NotSupportedException("fixture") };
        result = await Read(executor, device);
        Assert(result.StatusCode == 422 && result.Evidence.Code == "unsupported", "Unsupported read became a measured failure");
        factory.ThrowOnCreate = true;
        result = await Read(executor, device);
        Assert(result.StatusCode == 502 && !result.Evidence.OperationAttempted, "SDK initialization exception became read evidence");
    }

    private static async Task SimulationCannotMasquerade()
    {
        var device = Device(protocol: ProtocolType.Simulator);
        var repo = new Repository(device);
        var driver = new FixtureDriver { Protocol = ProtocolType.Simulator };
        var factory = new Factory(() => driver);
        var executor = new VerificationDriverExecutor(repo, factory);
        var result = await Read(executor, device);
        Assert(result.StatusCode == 422 && result.Evidence.Simulated && factory.Creates == 0, "Configured simulator was contacted for verification");
        device = Device();
        repo.Put(device);
        result = await Read(executor, device);
        Assert(result.Evidence.Simulated && !result.Evidence.OperationAttempted && driver.Connects == 0, "Simulator registered as Modbus supplied real evidence");
        driver = new SimulatedFixtureDriver { Protocol = ProtocolType.ModbusTCP };
        result = await Read(executor, device);
        Assert(result.Evidence.Simulated && driver.Connects == 0, "Simulated driver hid behind a real Protocol property");
        driver = new FixtureDriver { Protocol = ProtocolType.OpcUa };
        result = await Read(executor, device);
        Assert(result.Evidence.Code == "driver-protocol-mismatch" && !result.Evidence.OperationAttempted, "Unrelated driver protocol was accepted");
    }

    private static async Task ConfigChangedBeforeRead()
    {
        var initial = Device();
        var repo = new Repository(Device(host: "192.0.2.22"));
        var factory = new Factory(() => new FixtureDriver());
        var executor = new VerificationDriverExecutor(repo, factory);
        var result = await Read(executor, initial);
        Assert(result.StatusCode == 409 && factory.Creates == 0 && !result.Evidence.OperationAttempted, "Endpoint edit was not rejected before driver creation");
        repo.Put(Device(protocol: ProtocolType.OpcUa));
        result = await Read(executor, initial);
        Assert(result.StatusCode == 409 && factory.Creates == 0, "Protocol edit was not rejected before driver creation");
        result = await Read(executor, initial, new() { ConfigurationFingerprint = "" });
        Assert(result.StatusCode == 400 && factory.Creates == 0, "Missing config constraint fell back to pooled/ID-only read");
    }

    private static async Task ConfigChangedDuringConnect()
    {
        var initial = Device();
        var repo = new Repository(initial);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new FixtureDriver { OnConnect = async _ => { entered.SetResult(); await release.Task; } };
        var executor = new VerificationDriverExecutor(repo, new Factory(() => driver));
        var pending = Read(executor, initial);
        await entered.Task;
        repo.Put(Device(host: "192.0.2.99"));
        release.SetResult();
        var result = await pending;
        Assert(driver.Config?.Host == "192.0.2.11" && driver.Reads == 0, "TOCTOU during connect redirected or performed a read after edit");
        Assert(result.StatusCode == 409 && !result.Evidence.ConfigurationMatched && !result.Evidence.OperationAttempted, "Connect-time edit did not invalidate verification");
    }

    private static async Task ConcurrentTargetChanges()
    {
        var first = Device("first", "192.0.2.11");
        var second = Device("second", "192.0.2.12");
        var repo = new Repository(first, second);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driverOne = new FixtureDriver { OnRead = async _ => { started.SetResult(); await release.Task; } };
        var driverTwo = new FixtureDriver();
        var executorOne = new VerificationDriverExecutor(repo, new Factory(() => driverOne));
        var executorTwo = new VerificationDriverExecutor(repo, new Factory(() => driverTwo));
        var pendingOne = Read(executorOne, first);
        await started.Task;
        var pendingTwo = Read(executorTwo, second);
        // The first logical ID now points at the second physical endpoint while its read is in flight.
        repo.Put(Device("first", "192.0.2.12"));
        release.SetResult();
        var results = await Task.WhenAll(pendingOne, pendingTwo);
        Assert(results[0].StatusCode == 409 && results[0].Evidence.OperationAttempted && !results[0].Evidence.ConfigurationMatched,
            "Concurrent endpoint edit was scored as the original target");
        Assert(results[1].StatusCode == 200 && driverOne.Config?.Host == "192.0.2.11" && driverTwo.Config?.Host == "192.0.2.12",
            "Concurrent distinct targets mixed driver configurations");
        var count = driverOne.Reads;
        var retry = await Read(executorOne, first);
        Assert(retry.StatusCode == 409 && driverOne.Reads == count, "Next sample continued on the changed physical endpoint");
    }

    private static async Task CredentialsAndExtendedPropertiesAreFrozen()
    {
        var device = Device();
        var repo = new Repository(device);
        var driver = new FixtureDriver();
        var executor = new VerificationDriverExecutor(repo, new Factory(() => driver));
        var initial = await Read(executor, device);
        var expected = Expected(device, initial.Evidence.Revision);
        device.ConnectionConfig = device.ConnectionConfig with { Password = "changed-secret" };
        var next = await Read(executor, device, expected);
        Assert(next.StatusCode == 409 && !next.Evidence.OperationAttempted, "Password changed between samples without invalidating revision");
        var snapshot = VerificationConfiguration.Snapshot(device);
        driver = new FixtureDriver { OnConnect = _ =>
        {
            device.ConnectionConfig.ExtendedProperties["first"] = "mutated-after-bind";
            return Task.CompletedTask;
        } };
        next = await Read(executor, snapshot);
        Assert(next.StatusCode == 409 && driver.Config!.ExtendedProperties["first"] == "one" && driver.Reads == 0,
            "In-place configuration mutation altered the driver snapshot");
    }

    private static async Task FreshRepositoryScopes()
    {
        var device = Device();
        var store = new Repository(device);
        var instances = 0;
        var services = new ServiceCollection();
        services.AddScoped<IDeviceRepository>(_ => { Interlocked.Increment(ref instances); return new TrackingRepository(store); });
        using var provider = services.BuildServiceProvider();
        var driver = new FixtureDriver { OnRead = _ => { store.Put(Device(host: "192.0.2.25")); return Task.CompletedTask; } };
        var executor = new VerificationDriverExecutor(new TrackingRepository(store), new Factory(() => driver), provider.GetRequiredService<IServiceScopeFactory>());
        var result = await Read(executor, device);
        Assert(result.StatusCode == 409 && instances >= 3, "Post-read validation reused a stale tracked repository entity");
    }

    private static async Task DirectoryContract()
    {
        var device = Device(protocol: ProtocolType.FTP);
        var repo = new Repository(device);
        var driver = new FixtureDriver { Protocol = ProtocolType.FTP };
        var factory = new Factory(() => driver);
        var controller = Controller(repo, factory);
        var response = await controller.ReadVerificationDirectory(device.Id, new() { Directory = "/test", Verification = Expected(device) }, default);
        Assert(response is OkObjectResult && driver.Browses == 1 && driver.Reads == 0 && driver.Writes == 0, "Read-only directory did not execute exactly one browse");
        response = await controller.ReadVerificationDirectory(device.Id, new() { Directory = "/../unsafe", Verification = Expected(device) }, default);
        Assert(response is BadRequestObjectResult && factory.Creates == 1, "Unsafe directory contacted the driver");
        device = Device(protocol: ProtocolType.NFS);
        repo.Put(device);
        driver = new FixtureDriver { Protocol = ProtocolType.NFS };
        response = await controller.ReadVerificationDirectory(device.Id, new() { Directory = "/", Verification = Expected(device) }, default);
        var nfsBody = JsonSerializer.SerializeToElement(((ObjectResult)response).Value, Json);
        Assert(response is ObjectResult { StatusCode: 422 } && factory.Creates == 1 && driver.Connects == 0 && driver.Browses == 0
            && nfsBody.GetProperty("evidence").GetProperty("code").GetString() == "nfs-directory-cache"
            && !nfsBody.GetProperty("evidence").GetProperty("operationAttempted").GetBoolean(),
            "Potentially cached NFS directories performed an operation or produced protocol evidence");
        var beforeStaticRoot = factory.Creates;
        device = Device(protocol: ProtocolType.FOCAS);
        repo.Put(device);
        response = await controller.ReadVerificationDirectory(device.Id, new() { Directory = "/", Verification = Expected(device) }, default);
        Assert(response is ObjectResult { StatusCode: 422 } && factory.Creates == beforeStaticRoot, "Static FOCAS root invoked a driver");
        driver = new FixtureDriver { Protocol = ProtocolType.FOCAS, EmptyDirectory = true };
        response = await controller.ReadVerificationDirectory(device.Id, new() { Directory = "/Programs", Verification = Expected(device) }, default);
        Assert(response is ObjectResult { StatusCode: 422 }, "FOCAS collapsed empty/error result was scored");

        var source = Device();
        var linked = Device("linked", "192.0.2.33", ProtocolType.FTP);
        source.ConnectionConfig.ExtendedProperties["transferDeviceId"] = linked.Id;
        repo.Put(source);
        repo.Put(linked);
        driver = new FixtureDriver { Protocol = ProtocolType.FTP, OnBrowse = _ => { repo.Put(Device("linked", "192.0.2.44", ProtocolType.FTP)); return Task.CompletedTask; } };
        response = await controller.ReadVerificationDirectory(source.Id, new() { Directory = "/", Verification = Expected(source) }, default);
        Assert(response is ObjectResult { StatusCode: 409 } && driver.Config?.Host == "192.0.2.33", "Linked transfer target was re-resolved or mixed during browse");
    }

    private static async Task ControllerAndLegacyDevelopmentRead()
    {
        var device = Device();
        var repo = new Repository(device);
        var driver = new FixtureDriver { TagError = "password=not-a-real-secret", Quality = TagQuality.Bad };
        var controller = Controller(repo, new Factory(() => driver));
        var read = await controller.ReadTags(device.Id, new() { Tags = [new() { Address = "fixture:1", DataType = "Int32" }], Verification = Expected(device) }, default);
        var response = (ReadTagsResponse)((OkObjectResult)read.Result!).Value!;
        Assert(response.Evidence is { ConfigurationMatched: true, OperationAttempted: true } && response.Tags[0].Quality == "Bad", "Controller lost real bad-quality read evidence");
        Assert(!JsonSerializer.Serialize(response, Json).Contains("not-a-real-secret"), "SDK tag error leaked credentials through verification response");
        var simulator = new FixtureDriver { Protocol = ProtocolType.Simulator };
        controller = new(new FixtureAccessor(simulator), repo, NullLogger<DataReadWriteController>.Instance);
        read = await controller.ReadTags(device.Id, new() { Tags = [new() { Address = "fixture:1", DataType = "Int32" }] }, default);
        response = (ReadTagsResponse)((OkObjectResult)read.Result!).Value!;
        Assert(response.Tags[0].Quality == "Good" && response.Evidence is { Simulated: true, ConfigurationMatched: false },
            "Normal developer simulation was broken or returned unmarked data");
        var unavailable = new FixtureAccessor(simulator) { InitializationFailure = true };
        controller = new(unavailable, repo, NullLogger<DataReadWriteController>.Instance);
        read = await controller.ReadTags(device.Id, new() { Tags = [new() { Address = "fixture:1", DataType = "Int32" }] }, default);
        var body = JsonSerializer.SerializeToElement(((ObjectResult)read.Result!).Value, Json);
        Assert(!body.GetProperty("evidence").GetProperty("operationAttempted").GetBoolean(), "Pooled initialization failure claimed a read attempt");
    }

    private static async Task GatewayToHostContract()
    {
        var device = Device();
        var repo = new Repository(device);
        var driver = new FixtureDriver();
        var factory = new Factory(() => driver);
        var controller = Controller(repo, factory);
        using var handler = new ControllerHttpFixture(controller);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://verification-fixture.invalid/") };
        var adapter = new HttpVerifyMeasurementAdapter(new HttpFixtureFactory(client));
        VerifyMeasurementTarget Target() => new()
        {
            Id = device.Id, Protocol = device.Protocol.ToString(), Brand = device.Brand, Model = device.Model,
            Host = device.ConnectionConfig.Host, Port = device.ConnectionConfig.Port, Username = device.ConnectionConfig.Username,
            ConnectTimeoutMs = (int)device.ConnectionConfig.ConnectTimeout.TotalMilliseconds,
            ReadTimeoutMs = (int)device.ConnectionConfig.ReadTimeout.TotalMilliseconds,
            ExtendedProperties = new(device.ConnectionConfig.ExtendedProperties),
        };
        var target = Target();
        var observation = await adapter.ReadAsync(target, "fixture:1", "Int32", 3000, default);
        Assert(observation is { Supported: true, HasEvidence: true, Success: true } && driver.Reads == 1,
            "Real Gateway/Host wire contract did not accept a bound fake read");
        repo.Put(Device(host: "192.0.2.99"));
        observation = await adapter.ReadAsync(target, "fixture:1", "Int32", 3000, default);
        Assert(!observation.HasEvidence && factory.Creates == 1, "HTTP path read a changed target instead of rejecting its expectation");
        var requests = handler.Requests;
        await adapter.ReadAsync(target, "fixture:1", "Int32", 3000, default);
        Assert(handler.Requests == requests, "HTTP conflict did not stop the run target from further device access");
        repo.Put(device);
        driver = new FixtureDriver { ConnectSuccess = false };
        observation = await adapter.ReadAsync(Target(), "fixture:1", "Int32", 3000, default);
        Assert(!observation.HasEvidence && driver.Reads == 0, "Gateway scored actual Host initialization 502 as failed read");
        driver = new FixtureDriver { ReadError = new IOException("fixture read failed") };
        observation = await adapter.ReadAsync(Target(), "fixture:1", "Int32", 3000, default);
        Assert(observation.HasEvidence && !observation.Success && driver.Reads == 1, "Gateway lost actual Host callback failure evidence");
        driver = new FixtureDriver { Protocol = ProtocolType.Simulator };
        observation = await adapter.ReadAsync(Target(), "fixture:1", "Int32", 3000, default);
        Assert(!observation.HasEvidence && driver.Reads == 0 && driver.Connects == 0, "Protocol-disguised Host simulator passed the Gateway");
    }

    private static async Task Cancellation()
    {
        var device = Device();
        var factory = new Factory(() => new FixtureDriver());
        var executor = new VerificationDriverExecutor(new Repository(device), factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await Read(executor, device, ct: cancellation.Token); throw new InvalidOperationException("Cancellation swallowed"); }
        catch (OperationCanceledException) { Assert(factory.Creates == 0, "Canceled request created a driver"); }
    }

    private static DataReadWriteController Controller(IDeviceRepository repo, IProtocolDriverFactory factory) =>
        new(new RejectAccessor(), repo, NullLogger<DataReadWriteController>.Instance, factory);

    private class Repository(params Device[] devices) : IDeviceRepository
    {
        private readonly ConcurrentDictionary<string, Device> _devices = new(devices.ToDictionary(device => device.Id));
        public void Put(Device device) => _devices[device.Id] = device;
        public virtual Task<Device?> GetByIdAsync(string deviceId, CancellationToken ct = default) => Task.FromResult(_devices.GetValueOrDefault(deviceId));
        public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(Device device, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Device device, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string deviceId, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class TrackingRepository(Repository store) : Repository
    {
        private Device? _tracked;
        public override async Task<Device?> GetByIdAsync(string deviceId, CancellationToken ct = default) =>
            _tracked ??= await store.GetByIdAsync(deviceId, ct);
    }
    private sealed class Factory(Func<FixtureDriver> create) : IProtocolDriverFactory
    {
        public int Creates { get; private set; }
        public bool ThrowOnCreate { get; set; }
        public IProtocolDriver Create(ProtocolType protocol, string brand, string? model = null)
        {
            Creates++;
            if (ThrowOnCreate) throw new InvalidOperationException("SDK password=not-a-real-secret");
            return create();
        }
    }
    private class FixtureDriver : IProtocolDriver, IAddressSpaceBrowser, INCProgramTransfer
    {
        public ProtocolType Protocol { get; init; } = ProtocolType.ModbusTCP;
        public ConnectionState State => ConnectionState.Connected;
        public DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Browse | DriverCapabilities.FileTransfer;
        public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public bool ConnectSuccess { get; init; } = true;
        public Exception? ReadError { get; init; }
        public string? TagError { get; init; }
        public TagQuality Quality { get; init; } = TagQuality.Good;
        public bool EmptyDirectory { get; init; }
        public DeviceConnectionConfig? Config { get; private set; }
        public Func<CancellationToken, Task>? OnConnect { get; init; }
        public Func<CancellationToken, Task>? OnRead { get; init; }
        public Func<CancellationToken, Task>? OnBrowse { get; init; }
        public int Connects { get; private set; }
        public int Reads { get; private set; }
        public int Browses { get; private set; }
        public int Writes { get; private set; }
        public bool Disposed { get; private set; }
        public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
        {
            Connects++; Config = config;
            if (OnConnect is not null) await OnConnect(ct);
            return new() { Success = ConnectSuccess, ErrorMessage = ConnectSuccess ? null : "password=not-a-real-secret" };
        }
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> PingAsync(CancellationToken ct = default) => throw new InvalidOperationException("Handshake not allowed in verification fixture");
        public Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
        {
            Reads++;
            if (OnRead is not null) await OnRead(ct);
            if (ReadError is not null) throw ReadError;
            return requests.Select(request => new TagValue { Address = request.Address, DataType = request.DataType,
                Value = 42, Quality = Quality, Timestamp = DateTimeOffset.UtcNow, ErrorMessage = TagError }).ToList();
        }
        public Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
        { Writes++; throw new InvalidOperationException("Writes forbidden"); }
        public async Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
        {
            Browses++;
            if (OnBrowse is not null) await OnBrowse(ct);
            return EmptyDirectory ? [] : [new() { Path = "/fixture.nc", DisplayName = "fixture.nc", NodeType = AddressNodeType.Variable }];
        }
        public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default) => throw new NotSupportedException();
        public bool SupportsResume => false;
        public Task<TransferProgressResult> UploadProgramAsync(Stream source, NCProgramMetadata metadata, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) => throw new InvalidOperationException("Transfers forbidden");
        public Task<TransferProgressResult> DownloadProgramAsync(string remotePath, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) => throw new InvalidOperationException("Transfers forbidden");
        public Task<TransferProgressResult> ResumeUploadAsync(string transferId, string remotePath, Stream source, long offset, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) => throw new InvalidOperationException("Transfers forbidden");
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class SimulatedFixtureDriver : FixtureDriver;
    private sealed class HttpFixtureFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class ControllerHttpFixture(DataReadWriteController controller) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/data/fixture-machine/read",
                "End-to-end fixture attempted unexpected device I/O");
            var input = JsonSerializer.Deserialize<ReadTagsRequest>(await request.Content!.ReadAsStringAsync(ct), Json)!;
            var result = await controller.ReadTags("fixture-machine", input, ct);
            var action = (ObjectResult)result.Result!;
            return new HttpResponseMessage((HttpStatusCode)(action.StatusCode ?? 200))
            { Content = new StringContent(JsonSerializer.Serialize(action.Value, Json), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class RejectAccessor : IPooledDriverAccessor
    {
        public Task<PooledDriverResult<T>> ExecuteAsync<T>(string deviceId, Func<IProtocolDriver, Task<T>> action, CancellationToken ct = default) =>
            throw new InvalidOperationException("Verification must not borrow an ID-only pooled driver");
    }
    private sealed class FixtureAccessor(IProtocolDriver driver) : IPooledDriverAccessor
    {
        public bool InitializationFailure { get; init; }
        public async Task<PooledDriverResult<T>> ExecuteAsync<T>(string deviceId, Func<IProtocolDriver, Task<T>> action, CancellationToken ct = default) =>
            InitializationFailure ? new(false, default, "SDK initialization failed") : new(true, await action(driver), null);
    }
}
