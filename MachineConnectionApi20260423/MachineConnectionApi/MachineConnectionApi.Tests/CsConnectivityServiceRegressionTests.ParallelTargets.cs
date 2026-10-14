namespace MachineConnectionApi.Tests;

using System.Net;
using System.Net.Sockets;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;

internal static partial class CsConnectivityServiceRegressionTests
{
    public static async Task ParallelTargetScheduling()
    {
        await ParallelTestsAreRejectedWhileBusy();
        await ParallelTestBudgetIsEnforced();
        await DifferentParallelTargetsRunTogether();
        await OverlappingParallelTargetsRemainExclusive();
        await ParallelTargetWaitHonorsTimeoutAndCancellation();
    }

    private static async Task DifferentParallelTargetsRunTogether()
    {
        using var firstListener = new TcpListener(IPAddress.Loopback, 0);
        using var secondListener = new TcpListener(IPAddress.Loopback, 0);
        firstListener.Start();
        secondListener.Start();
        var firstPort = ((IPEndPoint)firstListener.LocalEndpoint).Port;
        var secondPort = ((IPEndPoint)secondListener.LocalEndpoint).Port;
        using var service = Create(Path.Combine(Path.GetTempPath(), $"cs-targets-{Guid.NewGuid():N}.json"));
        using var stopFirst = new CancellationTokenSource();
        var first = service.RunParallelTestAsync(
            new CsParallelTestRequest("127.0.0.1", firstPort, 1, 1, 1000, 60_000), stopFirst.Token);
        try
        {
            using var firstClient = await firstListener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var second = service.RunSameTargetParallelTestAsync(
                new CsSameTargetParallelTestRequest("127.0.0.1", secondPort, 1, 1, 1000), CancellationToken.None);
            using var secondClient = await secondListener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            AssertEqual(false, first.IsCompleted, "FirstTargetStillRunning");
            AssertEqual(1, (await second.WaitAsync(TimeSpan.FromSeconds(5))).Success, "SecondTargetSuccess");
        }
        finally
        {
            stopFirst.Cancel();
            await DrainParallelTest(first);
        }
    }

    private static async Task OverlappingParallelTargetsRemainExclusive()
    {
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var service = Create(Path.Combine(Path.GetTempPath(), $"cs-targets-{Guid.NewGuid():N}.json"));
        using var stopFirst = new CancellationTokenSource();
        var first = service.RunParallelTestAsync(
            new CsParallelTestRequest("127.0.0.1", port, 2, 2, 1000, 60_000), stopFirst.Token);
        try
        {
            using var firstClient = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var secondClient = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await ExpectParallelTargetBusy(() => service.RunSameTargetParallelTestAsync(
                new CsSameTargetParallelTestRequest("127.0.0.2", port, 1, 1, 1000), CancellationToken.None));
            await ExpectParallelTargetBusy(() => service.RunParallelTestAsync(
                new CsParallelTestRequest("127.0.0.2", port, 2, 2, 1000), CancellationToken.None));
            var independent = await service.RunSameTargetParallelTestAsync(
                new CsSameTargetParallelTestRequest("127.0.0.3", port, 1, 1, 1000), CancellationToken.None);
            AssertEqual(1, independent.Success, "RejectedRangeDidNotReserveIndependentTarget");
            AssertEqual(false, first.IsCompleted, "OverlappingRangeStillRunning");
        }
        finally
        {
            stopFirst.Cancel();
            await DrainParallelTest(first);
        }
    }

    private static async Task ParallelTargetWaitHonorsTimeoutAndCancellation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var service = Create(Path.Combine(Path.GetTempPath(), $"cs-targets-{Guid.NewGuid():N}.json"));
        using var stopFirst = new CancellationTokenSource();
        var first = service.RunParallelTestAsync(
            new CsParallelTestRequest("127.0.0.1", port, 1, 1, 1000, 60_000), stopFirst.Token);
        try
        {
            using var firstClient = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var request = new CsSameTargetParallelTestRequest("127.0.0.1", port, 1, 1, 1000);
            await ExpectParallelTargetBusy(() => service.RunSameTargetParallelTestAsync(
                request, CancellationToken.None, TimeSpan.FromMilliseconds(30)));
            using var cancelWait = new CancellationTokenSource();
            var cancelled = service.RunSameTargetParallelTestAsync(request, cancelWait.Token, Timeout.InfiniteTimeSpan);
            AssertEqual(false, cancelled.IsCompleted, "CancelledWaiterInitiallyWaiting");
            cancelWait.Cancel();
            try
            {
                await cancelled.WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("Cancelled target waiter completed normally");
            }
            catch (OperationCanceledException) when (cancelWait.IsCancellationRequested) { }
            var waiting = service.RunSameTargetParallelTestAsync(request, CancellationToken.None, TimeSpan.FromSeconds(5));
            AssertEqual(false, waiting.IsCompleted, "TargetWaiterWaitingForRelease");
            stopFirst.Cancel();
            await DrainParallelTest(first);
            using var nextClient = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            AssertEqual(1, (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Success, "ReleasedTargetReused");
        }
        finally
        {
            stopFirst.Cancel();
            await DrainParallelTest(first);
        }
    }

    private static async Task ExpectParallelTargetBusy(Func<Task<CsParallelTestResult>> run)
    {
        try
        {
            await run().WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Overlapping target test was accepted");
        }
        catch (CsParallelTestBusyException) { }
    }

    private static async Task DrainParallelTest(Task<CsParallelTestResult> task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
    }
}
