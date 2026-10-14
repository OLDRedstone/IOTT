using System.Reflection;
using System.Data.Common;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Host.Controllers;
using IndustrialIoT.Infrastructure.Persistence;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ConnectionVerificationRegressionTests
{
    public static async Task RunAsync()
    {
        var devices = Enumerable.Range(1, 100).Select(index => new Device
        {
            Id = $"batch-{index}", Name = "Batch fixture", Type = DeviceType.CNC,
            Brand = "fixture", Model = "fixture", Protocol = ProtocolType.ModbusTCP,
            ConnectionConfig = new() { Host = $"192.0.2.{index}", Port = 502 },
        }).ToArray();
        var repository = new SequentialRepository(devices);
        var factory = new BatchFactory();
        var controller = new ConnectionVerificationController(factory, repository,
            NullLogger<ConnectionVerificationController>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await controller.StartBatchTest(new()
        {
            DeviceIds = devices.Select(device => device.Id).Concat(["batch-1", "missing"]).ToArray(),
            Concurrency = 100,
        }, deadline.Token);
        var result = (BatchTestResult)((OkObjectResult)response.Result!).Value!;
        Require(repository.Queries == 101, "Duplicate IDs were queried or missing IDs changed batch semantics");
        Require(result.TotalDevices == 100 && result.SuccessCount == 100, "100 selected devices did not complete");
        Require(factory.Started == 100 && factory.Disposed == 100, "The 100 overlapping drivers were not all disposed");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await controller.StartBatchTest(new() { DeviceIds = ["batch-1"] }, cancelled.Token);
            throw new InvalidOperationException("Cancelled device lookup was accepted");
        }
        catch (OperationCanceledException) { Require(factory.Started == 100, "Cancellation started another driver"); }
        await RealDbContextDoesNotOverlap();
    }

    private static async Task RealDbContextDoesNotOverlap()
    {
        var interceptor = new BlockConnectionOpen();
        var options = new DbContextOptionsBuilder<IoTDbContext>()
            .UseMySql("Server=127.0.0.1;Database=no_network_fixture;User ID=fixture;Password=fixture",
                new MySqlServerVersion(new Version(8, 4, 0))).AddInterceptors(interceptor).Options;
        await using var database = new IoTDbContext(options);
        var factory = new BatchFactory();
        var controller = new ConnectionVerificationController(factory, new EfDeviceRepository(database),
            NullLogger<ConnectionVerificationController>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = controller.StartBatchTest(new()
        {
            DeviceIds = Enumerable.Range(1, 100).Select(index => $"batch-{index}").ToArray(), Concurrency = 100,
        }, cancellation.Token);
        await interceptor.Entered.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        try { await pending; throw new InvalidOperationException("Database cancellation was ignored"); }
        catch (OperationCanceledException) { Require(factory.Started == 0, "Drivers started before selection completed"); }
    }

    private sealed class BlockConnectionOpen : DbConnectionInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return result;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SequentialRepository(Device[] devices) : IDeviceRepository
    {
        private int _active;
        public int Queries { get; private set; }
        public async Task<Device?> GetByIdAsync(string deviceId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _active) != 1)
                throw new InvalidOperationException("Scoped repository queries overlapped");
            try
            {
                Queries++;
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                return devices.SingleOrDefault(device => device.Id == deviceId);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(Device device, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Device device, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string deviceId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class BatchFactory : IProtocolDriverFactory
    {
        private readonly TaskCompletionSource _allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Started;
        public int Disposed;
        public IProtocolDriver Create(ProtocolType protocol, string brand, string? model = null)
        {
            var driver = DispatchProxy.Create<IProtocolDriver, BatchDriver>();
            ((BatchDriver)driver).Factory = this;
            return driver;
        }
        public async Task<ConnectionResult> ConnectAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref Started) == 100) _allStarted.TrySetResult();
            await _allStarted.Task.WaitAsync(ct);
            return new() { Success = true };
        }
    }

    public class BatchDriver : DispatchProxy
    {
        private BatchFactory? _factory;
        internal object Factory { set => _factory = (BatchFactory)value; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IProtocolDriver.ConnectAsync): return _factory!.ConnectAsync((CancellationToken)args![1]!);
                case nameof(IProtocolDriver.PingAsync): return Task.FromResult(true);
                case nameof(IAsyncDisposable.DisposeAsync): Interlocked.Increment(ref _factory!.Disposed); return ValueTask.CompletedTask;
                default: throw new InvalidOperationException($"Unexpected fixture operation: {method.Name}");
            }
        }
    }
}
