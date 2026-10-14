namespace MachineConnectionApi.Tests;

using MachineConnectionApi.Models;
using MachineConnectionApi.Services;

internal static class VerifyTaskRunnerRegressionTests
{
    public static async Task RunAll()
    {
        await DeviceSelectionIsNormalizedAndConflictsFail();
        await ConcurrentExecutionIsRejected();
        await ExecutionFailureReleasesTaskForRetry();
        await DifferentDevicesOverlapAndKeepResults();
        await DeviceAndGlobalReservationsAreExclusive();
        await CapacityIsSevenAndRecovers();
        await FailureAndCancellationReleaseReservations();
        await ScheduledTasksOverlapAndBusyTasksRetry();
        await BatchStartsSevenAndIsolatesFailures();
    }

    private static async Task BatchStartsSevenAndIsolatesFailures()
    {
        var taskIds = Enumerable.Range(1, 8).Select(index => $"batch-{index}").ToArray();
        var store = new MemoryTaskStore(taskIds.Select(taskId => CreateTask(taskId, taskId)).ToArray());
        var service = new ControlledVerifyService(taskIds);
        var runner = new VerifyTaskRunner(store, service, new NullActivityLog());
        var controller = new MachineConnectionApi.Controllers.VerifyTasksController(store, runner);
        foreach (var invalid in new[] { Array.Empty<string>(), new[] { "" }, new[] { "batch-1", "batch-1" }, taskIds })
            AssertEqual(true, (await controller.RunBatch(invalid, CancellationToken.None)).Result
                is Microsoft.AspNetCore.Mvc.BadRequestObjectResult, "InvalidBatchRejected");
        var batch = controller.RunBatch(taskIds.Take(7).ToArray(), CancellationToken.None);
        await service.WaitStarted(taskIds.Take(7).ToArray());
        AssertEqual(7, store.ReadAll().Count(task => task.Status == "running"), "BatchSevenRunning");
        service.Fail(taskIds[0]);
        foreach (var taskId in taskIds.Skip(1).Take(6).Reverse()) service.Complete(taskId);
        var response = (await batch).Result as Microsoft.AspNetCore.Mvc.OkObjectResult;
        var results = response?.Value as IReadOnlyList<VerifyTaskRunResult>
            ?? throw new InvalidOperationException("Batch results missing");
        AssertEqual(7, results.Count, "BatchResultCount");
        AssertEqual("failed", results[0].Task?.Status, "BatchFailureRecorded");
        AssertEqual(6, results.Count(result => result.Task?.Status == "completed"), "BatchPeersComplete");
        foreach (var result in results)
            AssertEqual(result.TaskId, result.Task?.Id, "BatchResultsMatchTasks");
        service.Reset(taskIds[0]);
        var retry = controller.RunBatch(["missing", taskIds[0]], CancellationToken.None);
        await service.WaitStarted(taskIds[0]);
        service.Complete(taskIds[0]);
        var retried = (IReadOnlyList<VerifyTaskRunResult>)((Microsoft.AspNetCore.Mvc.OkObjectResult)(await retry).Result!).Value!;
        AssertEqual("验证任务不存在", retried[0].Error, "MissingBatchTaskIsolated");
        AssertEqual("completed", retried[1].Task?.Status, "ValidBatchTaskCompletes");
    }

    private static VerifyTaskDto CreateTask(string taskId, string deviceId) => new()
    {
        Id = taskId, Name = taskId, DeviceId = deviceId, Type = "function",
        Status = "pending", CreatedAt = DateTimeOffset.Now,
    };

    private static async Task AssertCapacityRejected(VerifyTaskRunner runner, string taskId)
    {
        try { await runner.RunTaskAsync(taskId, "manual", CancellationToken.None); }
        catch (VerifyTaskCapacityException) { return; }
        throw new InvalidOperationException($"Expected capacity rejection: {taskId}");
    }

    private static async Task DifferentDevicesOverlapAndKeepResults()
    {
        var store = new MemoryTaskStore(CreateTask("first", "device-1"), CreateTask("second", "device-2"));
        var service = new ControlledVerifyService("first", "second");
        var runner = new VerifyTaskRunner(store, service, new NullActivityLog());
        var first = runner.RunTaskAsync("first", "manual", CancellationToken.None);
        var second = runner.RunTaskAsync("second", "manual", CancellationToken.None);
        await service.WaitStarted("first", "second");
        AssertEqual(2, store.ReadAll().Count(task => task.Status == "running"), "OverlappingTasks");
        var controller = new MachineConnectionApi.Controllers.VerifyTasksController(store, runner);
        var running = store.ReadAll().Single(task => task.Id == "first");
        AssertEqual(true, controller.Update("first", running with { Name = "Changed" }).Result
            is Microsoft.AspNetCore.Mvc.ConflictObjectResult, "RunningTaskEditRejected");
        AssertEqual(true, controller.Delete("first") is Microsoft.AspNetCore.Mvc.ConflictObjectResult,
            "RunningTaskDeleteRejected");
        AssertEqual("first", store.ReadAll().Single(task => task.Id == "first").Name, "RunningTaskUnchanged");
        service.Complete("second");
        await second;
        AssertEqual(false, first.IsCompleted, "FirstStillRunning");
        service.Complete("first");
        await first;
        foreach (var task in store.ReadAll())
        {
            AssertEqual("result-" + task.Id, task.Result, "IndependentResult");
            using var json = System.Text.Json.JsonDocument.Parse(task.LastRunJson);
            AssertEqual(task.Id, json.RootElement.GetProperty("taskId").GetString(), "IndependentRunJson");
        }
    }

    public static async Task DeviceSelectionIsNormalizedAndConflictsFail()
    {
        foreach (var (deviceId, machineId, expected) in new[]
        {
            (" selected ", "selected", "selected"), ("", " selected ", "selected"),
            ("selected", "", "selected"), ("", "", ""),
        })
        {
            var task = new VerifyTaskDto
            {
                Id = "selection", Name = "Selection", Type = "function", CreatedAt = DateTimeOffset.Now,
                DeviceId = deviceId, MachineId = machineId,
            };
            var service = new BlockingVerifyService();
            service.Release.SetResult();
            var runner = new VerifyTaskRunner(new MemoryTaskStore(task), service, new NullActivityLog());
            await runner.RunTaskAsync(task.Id, "手动", CancellationToken.None);
            AssertEqual(expected, service.LastRequest?.DeviceId, "SelectedDevice");
        }
        var conflicting = new VerifyTaskDto
        {
            Id = "conflict", Name = "Conflict", Type = "function", CreatedAt = DateTimeOffset.Now,
            DeviceId = "first", MachineId = "selected",
        };
        var blockedService = new BlockingVerifyService();
        var blockedRunner = new VerifyTaskRunner(new MemoryTaskStore(conflicting), blockedService, new NullActivityLog());
        var failed = await blockedRunner.RunTaskAsync(conflicting.Id, "定时", CancellationToken.None);
        AssertEqual("failed", failed?.Status, "ConflictingSelectionStatus");
        AssertEqual<VerifyRunRequest?>(null, blockedService.LastRequest, "ConflictingSelectionNotExecuted");
    }

    public static async Task ExecutionFailureReleasesTaskForRetry()
    {
        var task = new VerifyTaskDto
        {
            Id = "task-1",
            Name = "机床实时验证",
            Type = "综合验证",
            Status = "pending",
            CreatedAt = DateTimeOffset.Now,
            MetricIds = ["file-integrity"],
        };
        var store = new MemoryTaskStore(task);
        var runner = new VerifyTaskRunner(store, new FailingVerifyService(), new NullActivityLog());

        var result = await runner.RunTaskAsync(task.Id, "手动", CancellationToken.None);

        AssertEqual("failed", result?.Status, "ReturnedStatus");
        AssertEqual("failed", store.ReadAll().Single().Status, "PersistedStatus");
        if (!store.ReadAll().Single().Detail.Contains("机床连接中断", StringComparison.Ordinal))
            throw new InvalidOperationException("Failure detail was not persisted");
    }

    public static async Task ConcurrentExecutionIsRejected()
    {
        var task = new VerifyTaskDto
        {
            Id = "task-2",
            Name = "并发保护验证",
            Type = "综合验证",
            CreatedAt = DateTimeOffset.Now,
        };
        var store = new MemoryTaskStore(task);
        var service = new BlockingVerifyService();
        var runner = new VerifyTaskRunner(store, service, new NullActivityLog());
        var firstRun = runner.RunTaskAsync(task.Id, "手动", CancellationToken.None);
        await service.Started.Task;

        var duplicateRejected = false;
        try { await runner.RunTaskAsync(task.Id, "手动", CancellationToken.None); }
        catch (InvalidOperationException) { duplicateRejected = true; }
        service.Release.TrySetResult();
        await firstRun;

        AssertEqual(true, duplicateRejected, "DuplicateRejected");
    }

    private static async Task DeviceAndGlobalReservationsAreExclusive()
    {
        foreach (var (firstDevice, secondDevice) in new[] { ("machine", " machine "), ("machine", ""), ("", "machine"), ("", "") })
        {
            var store = new MemoryTaskStore(CreateTask("first", firstDevice), CreateTask("second", secondDevice));
            var service = new ControlledVerifyService("first", "second");
            var runner = new VerifyTaskRunner(store, service, new NullActivityLog());
            var first = runner.RunTaskAsync("first", "manual", CancellationToken.None);
            await service.WaitStarted("first");
            await AssertCapacityRejected(runner, "second");
            AssertEqual("pending", store.ReadAll().Single(task => task.Id == "second").Status, "RejectedStatusPreserved");
            service.Complete("first");
            await first;
            var second = runner.RunTaskAsync("second", "manual", CancellationToken.None);
            await service.WaitStarted("second");
            service.Complete("second");
            AssertEqual("completed", (await second)?.Status, "ReservationReleased");
        }
    }

    private static async Task CapacityIsSevenAndRecovers()
    {
        var taskIds = Enumerable.Range(1, 8).Select(index => $"task-{index}").ToArray();
        var store = new MemoryTaskStore(taskIds.Select(taskId => CreateTask(taskId, taskId)).ToArray());
        var service = new ControlledVerifyService(taskIds);
        var runner = new VerifyTaskRunner(store, service, new NullActivityLog());
        var runs = taskIds.Take(7).Select(taskId => runner.RunTaskAsync(taskId, "manual", CancellationToken.None)).ToArray();
        await service.WaitStarted(taskIds.Take(7).ToArray());
        AssertEqual(7, store.ReadAll().Count(task => task.Status == "running"), "DefaultCapacity");
        await AssertCapacityRejected(runner, taskIds[7]);
        service.Complete(taskIds[0]);
        await runs[0];
        var eighth = runner.RunTaskAsync(taskIds[7], "manual", CancellationToken.None);
        await service.WaitStarted(taskIds[7]);
        AssertEqual(true, runs.Skip(1).All(run => !run.IsCompleted), "OtherTasksStillRunning");
        foreach (var taskId in taskIds.Skip(1)) service.Complete(taskId);
        await Task.WhenAll(runs.Append(eighth));
    }

    private static async Task FailureAndCancellationReleaseReservations()
    {
        foreach (var cancel in new[] { false, true })
        {
            var store = new MemoryTaskStore(CreateTask("retry", "machine"), CreateTask("peer", "machine"));
            var service = new ControlledVerifyService("retry", "peer");
            var runner = new VerifyTaskRunner(store, service, new NullActivityLog());
            using var cancellation = new CancellationTokenSource();
            var failed = runner.RunTaskAsync("retry", "manual", cancellation.Token);
            await service.WaitStarted("retry");
            if (cancel) cancellation.Cancel();
            else service.Fail("retry");
            AssertEqual("failed", (await failed)?.Status, "FailurePersisted");
            var peer = runner.RunTaskAsync("peer", "manual", CancellationToken.None);
            await service.WaitStarted("peer");
            service.Complete("peer");
            await peer;
            service.Reset("retry");
            var retried = runner.RunTaskAsync("retry", "manual", CancellationToken.None);
            await service.WaitStarted("retry");
            service.Complete("retry");
            AssertEqual("completed", (await retried)?.Status, "TaskRetryReleased");
        }
    }

    private static async Task ScheduledTasksOverlapAndBusyTasksRetry()
    {
        var tasks = new[] { CreateTask("first", "device-1"), CreateTask("second", "device-2"), CreateTask("busy", "device-1") }
            .Select(task => task with { ScheduleType = "daily", ScheduleTime = "00:00" }).ToArray();
        var store = new MemoryTaskStore(tasks);
        var service = new ControlledVerifyService("first", "second", "busy");
        var runner = new VerifyTaskRunner(store, service, new NullActivityLog());
        var scheduler = new VerifyTaskSchedulerHostedService(store, runner,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VerifyTaskSchedulerHostedService>.Instance);
        var method = typeof(VerifyTaskSchedulerHostedService).GetMethod("RunDueTasksAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Scheduler scan method missing");
        Task Scan() => (Task)method.Invoke(scheduler, [CancellationToken.None])!;
        var firstScan = Scan();
        await service.WaitStarted("first", "second");
        await firstScan.WaitAsync(TimeSpan.FromSeconds(5));
        var busy = store.ReadAll().Single(task => task.Id == "busy");
        AssertEqual("pending", busy.Status, "BusyScheduledStatus");
        AssertEqual<DateTimeOffset?>(null, busy.LastAutoRunAt, "BusyScheduledNotConsumed");
        service.Complete("first");
        service.Complete("second");
        await WaitForReservationsReleased();
        var secondScan = Scan();
        await service.WaitStarted("busy");
        service.Complete("busy");
        await secondScan;
        await WaitForReservationsReleased();
        AssertEqual(3, store.ReadAll().Count(task => task.LastAutoRunAt.HasValue), "AllScheduledTasksCompleted");
    }

    private static async Task WaitForReservationsReleased()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                using var lease = await VerifyExecutionLeaseService.Exclusive.AcquireAsync(null, null, timeout.Token);
                return;
            }
            catch (VerifyTaskCapacityException) { await Task.Delay(10, timeout.Token); }
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }

    private sealed class MemoryTaskStore(params VerifyTaskDto[] tasks) : IVerifyTaskStore
    {
        private readonly object _gate = new();
        private List<VerifyTaskDto> _tasks = [.. tasks];
        public List<VerifyTaskDto> ReadAll() { lock (_gate) return [.. _tasks]; }
        public void WriteAll(IEnumerable<VerifyTaskDto> items)
        {
            lock (_gate) _tasks = [.. items];
        }
        public TResult Update<TResult>(Func<List<VerifyTaskDto>, TResult> update)
        {
            lock (_gate)
            {
                var rows = _tasks.ToList();
                var result = update(rows);
                _tasks = rows;
                return result;
            }
        }
    }

    private sealed class FailingVerifyService : IVerifyAutomationService
    {
        public Task<VerifyRunResponse> RunAsync(VerifyRunRequest request, CancellationToken ct) =>
            Task.FromException<VerifyRunResponse>(new InvalidOperationException("机床连接中断"));
    }

    private sealed class BlockingVerifyService : IVerifyAutomationService
    {
        private int _calls;
        public VerifyRunRequest? LastRequest { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VerifyRunResponse> RunAsync(VerifyRunRequest request, CancellationToken ct)
        {
            LastRequest = request;
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return new VerifyRunResponse
            {
                TaskId = request.TaskId,
                TaskName = request.TaskName ?? "",
                Status = "completed",
                Result = "通过",
                Detail = "完成",
            };
        }
    }

    private sealed class ControlledVerifyService : IVerifyAutomationService
    {
        private readonly Dictionary<string, TaskCompletionSource> _started = new();
        private readonly Dictionary<string, TaskCompletionSource> _released = new();
        public ControlledVerifyService(params string[] taskIds)
        {
            foreach (var taskId in taskIds) Reset(taskId);
        }
        public void Reset(string taskId)
        {
            _started[taskId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _released[taskId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public Task WaitStarted(params string[] taskIds) =>
            Task.WhenAll(taskIds.Select(taskId => _started[taskId].Task)).WaitAsync(TimeSpan.FromSeconds(5));
        public void Complete(string taskId) => _released[taskId].TrySetResult();
        public void Fail(string taskId) => _released[taskId].TrySetException(new InvalidOperationException("Execution failed"));
        public async Task<VerifyRunResponse> RunAsync(VerifyRunRequest request, CancellationToken ct)
        {
            var taskId = request.TaskId ?? throw new InvalidOperationException("Task ID missing");
            _started[taskId].TrySetResult();
            await _released[taskId].Task.WaitAsync(ct);
            return new VerifyRunResponse
            {
                TaskId = request.TaskId, TaskName = request.TaskName ?? "", Status = "completed",
                Result = "result-" + request.TaskId, Detail = "detail-" + request.TaskId,
            };
        }
    }

    private sealed class NullActivityLog : ISystemActivityLog
    {
        public List<SystemLogDto> ReadAll() => [];
        public SystemLogDto Append(SystemLogDto entry) => entry;
        public void Write(string type, string action, string detail, string user = "系统", string ip = "-") { }
    }
}
