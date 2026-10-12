using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Host.Controllers;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.FileTransfer;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using IndustrialIoT.Protocols.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Independent entry point: integration may call RunAsync without changing any existing test logic.
internal static partial class VerificationTransferRegressionTests
{
    public static async Task RunAsync()
    {
        await RealFtpFourSizesAndSpeedAsync();
        await RealFtpCollisionAndCorruptionAsync();
        await PermissionAndInputGatesAsync();
        await FileProtocolCoverageAsync();
        await FrozenConfigurationAsync();
        await InlineAndLinkedTransferConfigurationAsync();
        await VirtualMaximumSizesAsync();
        await CancellationAndCleanupAsync();
        await HttpStreamingBoundaryAsync();
        Console.WriteLine("Verification transfer regressions passed (production Host service/controller; FTP loopback + fake protocol; no real devices).");
    }

    private static async Task RealFtpFourSizesAndSpeedAsync()
    {
        await using var peer = new VerificationFtpPeer();
        var repository = new Repository(DeviceFor(peer.Port));
        using var dependencies = new ServiceCollection().AddLogging().BuildServiceProvider();
        var registry = new DriverRegistry();
        registry.Register(typeof(FtpTransferDriver), ProtocolType.FTP, ["FTP", "*"]);
        var factory = new ProtocolDriverFactory(registry, dependencies, NullLogger<ProtocolDriverFactory>.Instance);
        var service = new VerificationTransferService(factory, repository, NullLogger.Instance);
        var snapshot = await service.GetSnapshotAsync(repository.Device.Id, default);
        Require(snapshot?.Supported == true && peer.Commands.IsEmpty, "Snapshot must resolve the real FTP capability without network writes.");
        foreach (var bytes in new long[] { 104858, 1024 * 1024, 10 * 1024 * 1024, 20 * 1024 * 1024 })
        {
            using var source = new PatternStream(bytes);
            var request = await RequestAsync(service, repository.Device.Id, source);
            var result = await service.ExecuteAsync(repository.Device.Id, request, source, default);
            Require(result.Success && result.HasEvidence && result.IntegrityVerified && result.UploadedBytes == bytes
                && result.DownloadedBytes == bytes && result.SourceSha256 == result.ReadBackSha256 && result.UploadSeconds > 0,
                $"Real FTP {bytes} byte round trip failed: {result.Detail}; commands={string.Join(" | ", peer.Commands)}");
            Require(result.CleanupSucceeded == true && peer.RemainingFiles == 0 && peer.RemainingDirectories == 0, "FTP cleanup left owned objects.");
            if (bytes == 10 * 1024 * 1024)
                Require(double.IsFinite(bytes / 1048576d / result.UploadSeconds), "This-run 10 MiB speed must have finite measured upload time.");
        }
        Require(peer.Commands.Count(x => x.StartsWith("STOR ")) == 4 && peer.Commands.Count(x => x.StartsWith("RETR ")) == 4,
            "All four sizes must actually upload and read back, not return fabricated observations.");
        Require(!peer.Commands.Any(x => x.StartsWith("LIST ") || x.StartsWith("NLST ") || x.StartsWith("APPE ")),
            "Safe upload must not use list-then-overwrite or resume.");
    }

    private static async Task RealFtpCollisionAndCorruptionAsync()
    {
        await using var peer = new VerificationFtpPeer { RejectMkdir = true };
        var repository = new Repository(DeviceFor(peer.Port));
        var service = new VerificationTransferService(new Factory(() => new FtpTransferDriver(NullLogger<FtpTransferDriver>.Instance)), repository, NullLogger.Instance);
        using var source = new PatternStream(104858);
        var request = await RequestAsync(service, repository.Device.Id, source);
        var collision = await service.ExecuteAsync(repository.Device.Id, request, source, default);
        Require(!collision.HasEvidence && !collision.Success && !peer.Commands.Any(x => x.StartsWith("STOR ") || x.StartsWith("DELE ") || x.StartsWith("RMD ")),
            "MKD collision/failure must never reuse, overwrite or delete the pre-existing namespace.");
        peer.RejectMkdir = false;
        peer.CorruptReadBack = true;
        source.Position = 0;
        var corrupted = await service.ExecuteAsync(repository.Device.Id, request, source, default);
        Require(corrupted.HasEvidence && !corrupted.Success && !corrupted.IntegrityVerified && corrupted.CleanupSucceeded == true,
            "Same-size read-back corruption must fail integrity and still clean up.");
        peer.CorruptReadBack = false;
        peer.AppendReadBackByte = true;
        source.Position = 0;
        var extraByte = await service.ExecuteAsync(repository.Device.Id, request, source, default);
        Require(extraByte.HasEvidence && !extraByte.Success && !extraByte.IntegrityVerified,
            "FTP read-back longer than SIZE/source must not be silently truncated and scored as intact.");
        peer.AppendReadBackByte = false;
        peer.RejectCleanup = true;
        source.Position = 0;
        var cleanupFailed = await service.ExecuteAsync(repository.Device.Id, request, source, default);
        Require(cleanupFailed.Success && cleanupFailed.CleanupSucceeded == false && cleanupFailed.Detail.Contains("清理未确认"),
            "Cleanup failure must be retained in evidence, not misreported as deletion success.");
    }

    private static async Task PermissionAndInputGatesAsync()
    {
        var factory = new Factory(() => new FakeDriver());
        var repository = new Repository(DeviceFor(21));
        var service = new VerificationTransferService(factory, repository, NullLogger.Instance);
        using var source = new PatternStream(100);
        var denied = await service.ExecuteAsync(repository.Device.Id, new() { AllowWrites = false }, source, default);
        Require(!denied.HasEvidence && factory.Created == 0 && repository.Reads == 0, "No permission must mean zero driver/repository activity or writes.");
        var valid = await RequestAsync(service, repository.Device.Id, source);
        var created = factory.Created;
        foreach (var directory in new[] { "../programs", "/test/../user", "/programs\r\nDELE user.nc", "C:/user", "//host/share", "/user%2f.." })
        {
            var invalid = CopyRequest(valid, directory: directory);
            var result = await service.ExecuteAsync(repository.Device.Id, invalid, source, default);
            Require(!result.HasEvidence && factory.Created == created, "Invalid directory must not reach the driver: " + directory);
        }
        foreach (var name in new[] { "O0001.nc", "verify-" + Guid.NewGuid().ToString("N") + ".exe", "../verify.nc" })
        {
            var result = await service.ExecuteAsync(repository.Device.Id, CopyRequest(valid, name: name), source, default);
            Require(!result.HasEvidence && factory.Created == created, "Invalid verification filename must not reach the driver.");
        }
        var wrongHash = CopyRequest(valid, hash: new string('0', 64));
        var hashResult = await service.ExecuteAsync(repository.Device.Id, wrongHash, source, default);
        Require(!hashResult.HasEvidence && factory.Created == created, "Actual source hash must be checked before connecting.");
        source.Position = 0;
        var wrongLength = await service.ExecuteAsync(repository.Device.Id, CopyRequest(valid, bytes: 101), source, default);
        Require(!wrongLength.HasEvidence && factory.Created == created, "Actual length mismatch must cause zero writes.");
        foreach (var protocol in new[] { ProtocolType.FOCAS, ProtocolType.NFS })
        {
            repository.Device.Protocol = protocol;
            source.Position = 0;
            var unsupportedRequest = await RequestAsync(service, repository.Device.Id, source);
            var unsupported = await service.ExecuteAsync(repository.Device.Id, unsupportedRequest, source, default);
            Require(!unsupported.Supported && !unsupported.HasEvidence && factory.Created == created, "Unimplemented/unsafe protocol must fail closed.");
        }
    }

    private static async Task FrozenConfigurationAsync()
    {
        var repository = new Repository(DeviceFor(21));
        var used = new List<FakeDriver>();
        var factory = new Factory(() => { var driver = new FakeDriver(); used.Add(driver); return driver; });
        var service = new VerificationTransferService(factory, repository, NullLogger.Instance);
        using var source = new PatternStream(100);
        var request = await RequestAsync(service, repository.Device.Id, source);
        var before = factory.Created;
        repository.Device.ConnectionConfig = repository.Device.ConnectionConfig with { Password = "changed-fixture-password" };
        var rejected = await service.ExecuteAsync(repository.Device.Id, request, source, default);
        Require(!rejected.HasEvidence && factory.Created == before, "Credential change after snapshot must not write using a new configuration.");

        source.Position = 0;
        request = await RequestAsync(service, repository.Device.Id, source);
        var capturedHost = repository.Device.ConnectionConfig.Host;
        factory.OnCreated = driver => ((FakeDriver)driver).OnConnect = _ =>
        {
            repository.Device.ConnectionConfig.ExtendedProperties["fixture"] = "changed-after-connect";
            repository.Device.ConnectionConfig = repository.Device.ConnectionConfig with { Host = "must-not-connect.invalid" };
        };
        var reads = repository.Reads;
        var result = await service.ExecuteAsync(repository.Device.Id, request, source, default);
        var executionDriver = used.Last();
        Require(result.Success && executionDriver.ConnectedConfig?.Host == capturedHost
            && !executionDriver.ConnectedConfig.ExtendedProperties.ContainsKey("fixture")
            && repository.Reads == reads + 1 && executionDriver.Lease.Cleaned,
            "One operation must retain the same deep-copied configuration/driver for create, upload, read-back and cleanup.");
        source.Position = 0;
        var stale = await service.ExecuteAsync(repository.Device.Id, request, source, default);
        Require(!stale.HasEvidence, "The next file in the same run must not silently accept a changed endpoint.");
    }

    private static async Task InlineAndLinkedTransferConfigurationAsync()
    {
        var primary = DeviceFor(8193);
        primary.Protocol = ProtocolType.FOCAS;
        primary.ConnectionConfig = primary.ConnectionConfig with
        {
            Transfer = new()
            {
                Protocol = ProtocolType.FTP, Host = "127.0.0.1", Port = 2121,
                Username = "transfer-user", Password = "transfer-fixture-secret",
                ExtendedProperties = new() { ["EncryptionMode"] = "Explicit" },
            },
        };
        var repository = new Repository(primary);
        FakeDriver? actual = null;
        var factory = new Factory(() => actual = new FakeDriver());
        var service = new VerificationTransferService(factory, repository, NullLogger.Instance);
        using var source = new PatternStream(100);
        var request = await RequestAsync(service, primary.Id, source);
        var result = await service.ExecuteAsync(primary.Id, request, source, default);
        Require(result.Success && actual?.ConnectedConfig?.Port == 2121 && actual.ConnectedConfig.Username == "transfer-user"
            && actual.ConnectedConfig.Password == "transfer-fixture-secret" && actual.ConnectedConfig.ExtendedProperties["EncryptionMode"] == "Explicit",
            "Inline FTP transfer must use its complete captured credentials/options, not the primary FOCAS connection.");

        var linked = DeviceFor(2221);
        linked.Id = "linked-transfer-fixture";
        repository.OtherDevices[linked.Id] = linked;
        primary.ConnectionConfig = primary.ConnectionConfig with
        {
            Transfer = null, ExtendedProperties = new() { ["transferDeviceId"] = linked.Id },
        };
        source.Position = 0;
        request = await RequestAsync(service, primary.Id, source);
        var linkedResult = await service.ExecuteAsync(primary.Id, request, source, default);
        Require(linkedResult.Success && actual?.ConnectedConfig?.Port == 2221, "Linked transfer device must be captured and used consistently.");
        linked.ConnectionConfig = linked.ConnectionConfig with { Password = "changed-linked-secret" };
        source.Position = 0;
        var created = factory.Created;
        var stale = await service.ExecuteAsync(primary.Id, request, source, default);
        Require(stale.ConfigurationChanged && !stale.HasEvidence && factory.Created == created,
            "A linked device credential/configuration change must invalidate the existing verification token before any write.");
    }

    private static async Task VirtualMaximumSizesAsync()
    {
        var fake = new FakeDriver();
        var factory = new Factory(() => fake = new FakeDriver());
        var repository = new Repository(DeviceFor(21));
        var service = new VerificationTransferService(factory, repository, NullLogger.Instance);
        foreach (var bytes in new long[] { 1024 * 1024, 10 * 1024 * 1024, 200L * 1024 * 1024 })
        {
            using var source = new PatternStream(bytes);
            var request = await RequestAsync(service, repository.Device.Id, source);
            var result = await service.ExecuteAsync(repository.Device.Id, request, source, default);
            Require(result.Success && result.UploadedBytes == bytes && fake.Lease.Uploaded == bytes && source.MaxRead <= 131072,
                "Maximum-file staircase must stream all actual bytes with bounded buffers.");
        }
        using var oversized = new PatternStream(200L * 1024 * 1024 + 1);
        var invalid = new VerificationTransferRequest { AllowWrites = true, ExpectedBytes = oversized.Length };
        var created = factory.Created;
        Require(!(await service.ExecuteAsync(repository.Device.Id, invalid, oversized, default)).HasEvidence && factory.Created == created,
            "200 MiB + 1 byte must be rejected before driver creation.");
        using var huge = new PatternStream(5L * 1024 * 1024 * 1024);
        Require(!(await service.ExecuteAsync(repository.Device.Id, new() { AllowWrites = true, ExpectedBytes = huge.Length }, huge, default)).HasEvidence,
            "Length checks must use Int64, not wrap at 2/4 GiB.");
        using var small = new PatternStream(100);
        var requestSmall = await RequestAsync(service, repository.Device.Id, small);
        factory.OnCreated = d => ((FakeDriver)d).Lease.ReadBackDelta = 1;
        var tooLong = await service.ExecuteAsync(repository.Device.Id, requestSmall, small, default);
        Require(tooLong.HasEvidence && !tooLong.Success && tooLong.CleanupSucceeded == true, "Longer read-back must hit the bound and clean up.");
        small.Position = 0;
        factory.OnCreated = d => ((FakeDriver)d).Lease.ReadBackDelta = -1;
        var tooShort = await service.ExecuteAsync(repository.Device.Id, requestSmall, small, default);
        Require(tooShort.HasEvidence && !tooShort.Success && tooShort.CleanupSucceeded == true, "Shorter read-back must not pass integrity.");
    }

    private static async Task CancellationAndCleanupAsync()
    {
        using var cancellation = new CancellationTokenSource();
        FakeDriver? driver = null;
        var factory = new Factory(() => driver = new FakeDriver());
        var repository = new Repository(DeviceFor(21));
        var service = new VerificationTransferService(factory, repository, NullLogger.Instance);
        using var source = new PatternStream(100);
        var request = await RequestAsync(service, repository.Device.Id, source);
        factory.OnCreated = created => ((FakeDriver)created).Lease.CancelOnUpload = cancellation;
        try { await service.ExecuteAsync(repository.Device.Id, request, source, cancellation.Token); throw new Exception("Cancellation was swallowed."); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Require(driver?.Lease.Cleaned == true && driver.Lease.CleanupHadLiveToken && driver.Disposed,
            "Caller cancellation must propagate while owned-file cleanup gets an independent live bounded token.");
        var count = factory.Created;
        source.Position = 0;
        try { await service.ExecuteAsync(repository.Device.Id, request, source, cancellation.Token); }
        catch (OperationCanceledException) { }
        Require(factory.Created == count, "Already-cancelled requests must not create anything.");
    }

    private static async Task HttpStreamingBoundaryAsync()
    {
        var method = typeof(VerificationTransferController).GetMethod(nameof(VerificationTransferController.RoundTrip))!;
        Require((long)method.CustomAttributes.Single(a => a.AttributeType == typeof(RequestSizeLimitAttribute)).ConstructorArguments[0].Value! > VerificationTransferPolicy.MaximumFileBytes,
            "Request limit must include multipart overhead above 200 MiB.");
        var formLimit = method.GetCustomAttribute<RequestFormLimitsAttribute>()!;
        Require(formLimit.MultipartBodyLengthLimit > VerificationTransferPolicy.MaximumFileBytes && formLimit.MemoryBufferThreshold <= 65536,
            "Large multipart forms must be bounded and disk-backed, not buffered in full memory.");
        var repository = new Repository(DeviceFor(21));
        var factory = new Factory(() => new FakeDriver());
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IDeviceRepository>(repository);
        builder.Services.AddSingleton<IProtocolDriverFactory>(factory);
        builder.Services.AddControllers().AddApplicationPart(typeof(VerificationTransferController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromMinutes(3) };
        try
        {
            var snapshot = await client.GetFromJsonAsync<VerificationTransferSnapshot>($"api/program-transfer/{repository.Device.Id}/verification/snapshot");
            Require(snapshot?.Supported == true, "Production MVC controller must be reachable using its existing registered dependencies.");
            using var source = new PatternStream(VerificationTransferPolicy.MaximumFileBytes);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(source));
            source.Position = 0;
            using var form = HttpForm(snapshot!.Token, source, hash);
            using var response = await client.PostAsync($"api/program-transfer/{repository.Device.Id}/verification/round-trip", form);
            Require(response.IsSuccessStatusCode, "Streaming 200 MiB multipart request failed: " + response.StatusCode + " " + await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<VerificationTransferResult>();
            Require(result?.Success == true && result.UploadedBytes == VerificationTransferPolicy.MaximumFileBytes && result.CleanupSucceeded == true,
                "HTTP 200 MiB must execute the production service, not merely accept the request.");
        }
        finally { await app.StopAsync(); }
    }

    private static MultipartFormDataContent HttpForm(string snapshot, Stream source, string hash)
    {
        var name = "verify-" + Guid.NewGuid().ToString("N") + ".nc";
        var form = new MultipartFormDataContent();
        form.Add(new StringContent("true"), "allowWrites");
        form.Add(new StringContent(snapshot), "snapshotToken");
        form.Add(new StringContent("/verification"), "directory");
        form.Add(new StringContent(name), "remoteName");
        form.Add(new StringContent(source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)), "expectedBytes");
        form.Add(new StringContent(hash), "expectedSha256");
        form.Add(new StreamContent(source), "file", name);
        return form;
    }

    private static async Task<VerificationTransferRequest> RequestAsync(VerificationTransferService service, string id, Stream source)
    {
        var snapshot = await service.GetSnapshotAsync(id, default);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source));
        source.Position = 0;
        return new()
        {
            AllowWrites = true, SnapshotToken = snapshot!.Token, Directory = "/verification",
            RemoteName = "verify-" + Guid.NewGuid().ToString("N") + ".nc", ExpectedBytes = source.Length, ExpectedSha256 = hash,
        };
    }
    private static VerificationTransferRequest CopyRequest(VerificationTransferRequest request, string? directory = null, string? name = null, string? hash = null, long? bytes = null) => new()
    {
        AllowWrites = request.AllowWrites, SnapshotToken = request.SnapshotToken, Directory = directory ?? request.Directory,
        RemoteName = name ?? request.RemoteName, ExpectedSha256 = hash ?? request.ExpectedSha256, ExpectedBytes = bytes ?? request.ExpectedBytes,
    };
    private static Device DeviceFor(int port) => new()
    {
        Id = "verification-fixture", Name = "loopback-only", Brand = "FTP", Model = "fixture", Type = DeviceType.CNC,
        Protocol = ProtocolType.FTP, ConnectionConfig = new() { Host = "127.0.0.1", Port = port, Username = "fixture", Password = "fixture-only", ReadTimeout = TimeSpan.FromSeconds(3) },
    };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Factory(Func<IProtocolDriver> create) : IProtocolDriverFactory
    {
        public int Created { get; private set; }
        public Action<IProtocolDriver>? OnCreated { get; set; }
        public IProtocolDriver Create(ProtocolType protocol, string brand, string? model = null)
        { Created++; var driver = create(); OnCreated?.Invoke(driver); return driver; }
    }
    private sealed class Repository(Device device) : IDeviceRepository
    {
        public Device Device { get; } = device;
        public Dictionary<string, Device> OtherDevices { get; } = new(StringComparer.Ordinal);
        public int Reads { get; private set; }
        public Task<Device?> GetByIdAsync(string id, CancellationToken ct = default)
        { Reads++; return Task.FromResult<Device?>(id == Device.Id ? Device : OtherDevices.GetValueOrDefault(id)); }
        public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Device>>([Device]);
        public Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default) => GetAllAsync(ct);
        public Task AddAsync(Device value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Device value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class FakeDriver(ProtocolType protocol = ProtocolType.FTP) : IProtocolDriver, IVerificationFileTransfer
    {
        public FakeLease Lease { get; } = new();
        public DeviceConnectionConfig? ConnectedConfig { get; private set; }
        public Action<DeviceConnectionConfig>? OnConnect { get; set; }
        public Exception? AcquisitionError { get; set; }
        public bool Disposed { get; private set; }
        public ProtocolType Protocol => protocol;
        public ConnectionState State => ConnectionState.Connected;
        public DriverCapabilities Capabilities => DriverCapabilities.FileTransfer;
        public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
        { ConnectedConfig = config; OnConnect?.Invoke(config); return Task.FromResult(new ConnectionResult { Success = true }); }
        public Task<IVerificationFileLease> CreateVerificationFileAsync(string directory, string name, CancellationToken ct)
        {
            if (AcquisitionError is not null) throw AcquisitionError;
            Lease.RemotePath = directory + "/" + name;
            return Task.FromResult<IVerificationFileLease>(Lease);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<TagValue> ReadTagAsync(string a, DataType t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WriteResult> WriteTagAsync(string a, DataType t, object value, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class FakeLease : IVerificationFileLease
    {
        public string RemotePath { get; set; } = "";
        public long Uploaded { get; private set; }
        public int ReadBackDelta { get; set; }
        public bool CorruptReadBack { get; set; }
        public CancellationTokenSource? CancelOnUpload { get; set; }
        public bool Cleaned { get; private set; }
        public bool CleanupHadLiveToken { get; private set; }
        public async Task<long> UploadAsync(Stream source, long length, CancellationToken ct)
        {
            CancelOnUpload?.Cancel(); ct.ThrowIfCancellationRequested();
            await source.CopyToAsync(Stream.Null, 81920, ct);
            Uploaded = source.Position;
            return Uploaded;
        }
        public async Task DownloadAsync(Stream destination, CancellationToken ct)
        { using var source = new PatternStream(Uploaded + ReadBackDelta, CorruptReadBack ? (byte)0x5b : (byte)0x5a); await source.CopyToAsync(destination, 81920, ct); }
        public Task CleanupAsync(CancellationToken ct)
        { CleanupHadLiveToken = ct.CanBeCanceled && !ct.IsCancellationRequested; ct.ThrowIfCancellationRequested(); Cleaned = true; return Task.CompletedTask; }
    }
    private sealed class PatternStream(long length, byte value = 0x5a) : Stream
    {
        public int MaxRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            MaxRead = Math.Max(MaxRead, buffer.Length);
            var read = (int)Math.Min(buffer.Length, length - Position);
            buffer[..read].Fill(value);
            Position += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count)); }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => length + offset };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
