namespace MachineConnectionApi.Tests;

using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Fake-only regressions. Call RunAll from the evaluation-pipeline entry point during integration.</summary>
internal static class VerifyTaskProgressRegressionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAll()
    {
        await ProgressKeepsHistoryAndRejectsLateCallbacks();
        await BackgroundStartOutlivesRequestCancellation();
        await BackgroundBatchKeepsSevenReservationsAndIsolatesErrors();
        await ProgressPersistenceFailureDoesNotFailMeasurement();
        await ConcurrentGroupsReserveEveryDeviceAndTransferEndpoint();
        OptionsValidationAndCrudDoNotAcceptClientHistory();
        RestartClosesOnlyCurrentRun();
        ExportsPreserveZeroScoresNullsRulesAndEvidence();
        CallbackIsNotSerialized();
        await VerifyTaskRuntimeRegressionTests.RunAll();
    }

    private static VerifyTaskDto TaskRow(string id = "task", string device = "device") => new()
    {
        Id = id, Name = id, Type = "function", DeviceId = device, CreatedAt = DateTimeOffset.Now,
        MetricIds = ["communication-stability", "file-integrity"],
    };

    private static VerifyRunResponse Snapshot(double progress = 50) => new()
    {
        RunId = "engine-id", TaskId = "task", TaskName = "task", Status = "running", StartedAt = "2026-09-29T10:00:00Z",
        ProgressPercent = progress, CompletedMetricCount = 1, TotalMetricCount = 2, CurrentMetricId = "file-integrity",
        Metrics =
        [
            new() { MetricId = "communication-stability", Name = "稳定性", Measurement = 0, Unit = "min", Score = 0,
                MeasurementSource = "current-run", Status = "failed", ExecutionStatus = "completed", ProgressPercent = 100,
                ScoreReason = "真实零分", Evidence = ["本轮应用读失败"] },
            new() { MetricId = "file-integrity", Name = "文件完整性", Status = "unrated", ExecutionStatus = "running" },
        ],
    };

    private static async Task ProgressKeepsHistoryAndRejectsLateCallbacks()
    {
        const string history = "{\"runId\":\"previous\",\"status\":\"completed\",\"metrics\":[]}";
        var store = new MemoryStore(TaskRow() with { LastRunJson = history, Options = new() { AllowFileWrites = true, ConcurrentDeviceIds = ["peer"] } });
        var service = new ProgressService();
        var runner = new VerifyTaskRunner(store, service, new NullLog());
        var first = runner.RunTaskAsync("task", "手动", CancellationToken.None);
        var request = await service.Request.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(request.Options?.AllowFileWrites == true && request.Options.ConcurrentDeviceIds!.SequenceEqual(["peer"]), "Options forwarded");
        var initial = store.ReadAll().Single();
        Check(initial.LastRunJson == history && initial.Status == "running", "Begin retains last completed history");
        var snapshot = Snapshot();
        request.ReportProgress!(snapshot);
        var progress = store.ReadAll().Single();
        var current = JsonSerializer.Deserialize<VerifyRunResponse>(progress.CurrentRunJson, JsonOptions)!;
        Check(progress.LastRunJson == history && current.RunId == progress.ActiveRunId, "Progress has one stable active identity");
        Check(current.ProgressPercent == 50 && current.Metrics[0].Score == 0, "Real progress and zero score retained");
        snapshot.Metrics[0].Evidence.Add("mutated after callback");
        Check(!store.ReadAll().Single().CurrentRunJson.Contains("mutated after callback"), "Callback snapshot is synchronous and detached");
        request.ReportProgress!(Snapshot(20));
        Check(store.ReadAll().Single().CurrentRunJson == progress.CurrentRunJson, "Regressive progress ignored");
        var final = Snapshot(100);
        final.Status = "completed";
        final.Result = "不通过";
        final.Metrics[1].ExecutionStatus = "skipped";
        service.Completion.SetResult(final);
        var completed = (await first)!;
        Check(completed.Status == "completed" && completed.Result == "不通过", "Acceptance failure is not execution failure");
        Check(completed.ActiveRunId is null && completed.CurrentRunJson == "", "Completion clears active snapshot");
        Check(completed.LastRunJson != history, "History published at real completion");
        request.ReportProgress!(Snapshot(99));
        Check(store.ReadAll().Single().LastRunJson == completed.LastRunJson, "Late callback cannot overwrite final result");

        service.Reset();
        var second = runner.RunTaskAsync("task", "手动", CancellationToken.None);
        await service.Request.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = store.ReadAll().Single();
        request.ReportProgress!(Snapshot(100));
        Check(store.ReadAll().Single().CurrentRunJson == next.CurrentRunJson, "Old callback cannot overwrite a newer run");
        Check(next.ActiveRunId != initial.ActiveRunId, "New run gets new reservation identity");
        service.Completion.SetResult(new() { Status = "completed" });
        await second;
    }

    private static async Task BackgroundStartOutlivesRequestCancellation()
    {
        var store = new MemoryStore(TaskRow());
        var service = new ProgressService();
        var runner = new VerifyTaskRunner(store, service, new NullLog());
        var controller = new VerifyTasksController(store, runner);
        using var requestCancellation = new CancellationTokenSource();
        var response = await controller.Start("task", requestCancellation.Token);
        Check(response.Result is AcceptedResult { Value: VerifyTaskDto { Status: "running" } }, "Start returns 202 with reserved task");
        await service.Request.Task.WaitAsync(TimeSpan.FromSeconds(5));
        requestCancellation.Cancel();
        Check(!service.Token.IsCancellationRequested && store.ReadAll().Single().Status == "running", "Browser cancellation does not cancel background work");
        Check((await controller.Start("task", CancellationToken.None)).Result is ConflictObjectResult, "Duplicate background start rejected");
        service.Completion.SetResult(new() { Status = "completed" });
        await WaitUntil(() => store.ReadAll().Single().Status == "completed");
    }

    private static async Task BackgroundBatchKeepsSevenReservationsAndIsolatesErrors()
    {
        var ids = Enumerable.Range(1, 8).Select(index => "task-" + index).ToArray();
        var store = new MemoryStore(ids.Select(id => TaskRow(id, "device-" + id)).ToArray());
        var service = new ProgressService();
        var controller = new VerifyTasksController(store, new VerifyTaskRunner(store, service, new NullLog()));
        Check((await controller.StartBatch([ids[0], ids[0]], CancellationToken.None)).Result is BadRequestObjectResult, "Duplicate background batch rejected");
        var accepted = (AcceptedResult)(await controller.StartBatch(ids.Take(7).ToArray(), CancellationToken.None)).Result!;
        var results = (IReadOnlyList<VerifyTaskRunResult>)accepted.Value!;
        Check(results.Count == 7 && results.All(result => result.Task?.Status == "running"), "Seven background tasks accepted independently");
        Check((await controller.Start(ids[7], CancellationToken.None)).Result is ConflictObjectResult, "Eighth background task rejected by capacity");
        service.Completion.SetResult(new() { Status = "completed" });
        await WaitUntil(() => store.ReadAll().Count(task => task.Status == "completed") == 7);
        var mixed = (AcceptedResult)(await controller.StartBatch(["missing", ids[7]], CancellationToken.None)).Result!;
        var mixedResults = (IReadOnlyList<VerifyTaskRunResult>)mixed.Value!;
        Check(mixedResults[0].Task is null && mixedResults[0].Error == "验证任务不存在" && mixedResults[1].Task is not null,
            "Background batch isolates missing tasks and releases completed slots");
        await WaitUntil(() => store.ReadAll().All(task => task.Status == "completed"));
    }

    private static async Task ProgressPersistenceFailureDoesNotFailMeasurement()
    {
        var store = new MemoryStore(TaskRow());
        var service = new ProgressService();
        var runner = new VerifyTaskRunner(store, service, new NullLog(throwOnWrite: true));
        var running = runner.RunTaskAsync("task", "手动", CancellationToken.None);
        var request = await service.Request.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.FailNextUpdate = true;
        request.ReportProgress!(Snapshot()); // Neither persistence nor activity logging may escape this callback.
        request.ReportProgress!(Snapshot(60));
        Check(JsonSerializer.Deserialize<VerifyRunResponse>(store.ReadAll().Single().CurrentRunJson, JsonOptions)!.ProgressPercent == 60, "Subsequent progress retries successfully");
        service.Completion.SetResult(new() { Status = "completed", Result = "完成" });
        Check((await running)?.Status == "completed", "Progress-store failure cannot fake device failure");
    }

    private static async Task ConcurrentGroupsReserveEveryDeviceAndTransferEndpoint()
    {
        var devices = new DeviceMemoryStore(
            Device("main", "192.0.2.1") with { Transfer = new() { Host = "192.0.2.10", Port = 21, Protocol = "FTP" } },
            Device("peer", "192.0.2.2"), Device("transfer-alias", "192.0.2.10") with { Port = 21 });
        var store = new MemoryStore(TaskRow("group", "main") with { Options = new() { ConcurrentDeviceIds = ["peer"] } },
            TaskRow("peer-task", "peer"), TaskRow("file-task", "transfer-alias"));
        var service = new ProgressService();
        var runner = new VerifyTaskRunner(store, service, new NullLog(), devices: devices);
        var running = runner.RunTaskAsync("group", "手动", CancellationToken.None);
        await service.Request.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var id in new[] { "peer-task", "file-task" })
        {
            var rejected = false;
            try { await runner.RunTaskAsync(id, "手动", CancellationToken.None); }
            catch (VerifyTaskCapacityException) { rejected = true; }
            Check(rejected, "Concurrent target/transfer endpoint reservation: " + id);
        }
        service.Completion.SetResult(new() { Status = "completed" });
        await running;
        service.Reset();
        var peer = runner.RunTaskAsync("peer-task", "手动", CancellationToken.None);
        await service.Request.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Completion.SetResult(new() { Status = "completed" });
        await peer;
    }

    private static void OptionsValidationAndCrudDoNotAcceptClientHistory()
    {
        var store = new MemoryStore();
        var devices = new DeviceMemoryStore(Device("main", "192.0.2.1"), Device("alias", "192.0.2.1"));
        var controller = new VerifyTasksController(store, new VerifyTaskRunner(store, new ProgressService(), new NullLog()), devices);
        foreach (var invalid in new VerifyRunOptions[]
        {
            new() { ConcurrentDeviceIds = ["main", "main"] }, new() { ConcurrentDeviceIds = [" main"] },
            new() { ConcurrentDeviceIds = ["missing"] }, new() { ConcurrentDeviceIds = ["main", "alias"] },
            new() { ProbeTimeoutMs = 1 }, new() { MaxParallelTargets = 101 },
        }) Check(controller.Create(TaskRow() with { Options = invalid }).Result is BadRequestObjectResult, "Invalid options rejected on save");
        var malicious = TaskRow() with { Status = "running", CurrentRunJson = "client-current", LastRunJson = "client-history", ActiveRunId = "client-active" };
        var created = (VerifyTaskDto)((OkObjectResult)controller.Create(malicious).Result!).Value!;
        Check(created.Status == "pending" && created.CurrentRunJson == "" && created.LastRunJson == "" && created.ActiveRunId is null, "Create strips injected execution state");
        var updated = (VerifyTaskDto)((OkObjectResult)controller.Update(created.Id, malicious with { Id = created.Id }).Result!).Value!;
        Check(updated.Status == "pending" && updated.LastRunJson == "" && updated.ActiveRunId is null, "Update cannot forge history or status");
        Check(new VerifyRunOptions().AllowFileWrites == false, "Write permission defaults off");
    }

    private static void RestartClosesOnlyCurrentRun()
    {
        var current = Snapshot();
        current.RunId = "active";
        const string history = "{\"runId\":\"previous\",\"status\":\"completed\",\"metrics\":[]}";
        var store = new MemoryStore(TaskRow() with { Status = "running", ActiveRunId = "active", LastRunJson = history, CurrentRunJson = JsonSerializer.Serialize(current, JsonOptions) },
            TaskRow("legacy") with { Status = "running", LastRunJson = history });
        var scheduler = new VerifyTaskSchedulerHostedService(store, new VerifyTaskRunner(store, new ProgressService(), new NullLog()), NullLogger<VerifyTaskSchedulerHostedService>.Instance);
        typeof(VerifyTaskSchedulerHostedService).GetMethod("RecoverStaleRunningTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scheduler, null);
        var closed = store.ReadAll().Single(task => task.Id == "task");
        var recovered = JsonSerializer.Deserialize<VerifyRunResponse>(closed.LastRunJson, JsonOptions)!;
        Check(closed.Status == "failed" && closed.ActiveRunId is null && closed.CurrentRunJson == "" && closed.CompletedAt.HasValue, "Restart releases persistent active state");
        Check(recovered.RunId == "active" && recovered.Status == "failed" && recovered.TotalScore is null && recovered.CompletedAt.Length > 0, "Only interrupted run is finalized");
        Check(recovered.Metrics[0].Score == 0 && recovered.Metrics[1].ExecutionStatus == "error", "Recovered evidence preserved and unfinished metric marked error");
        Check(store.ReadAll().Single(task => task.Id == "legacy").LastRunJson == history, "Legacy previous history is not relabeled");
    }

    private static void ExportsPreserveZeroScoresNullsRulesAndEvidence()
    {
        var run = Snapshot(100);
        run.Status = "completed";
        run.CompletedAt = "2026-09-29T10:30:00Z";
        run.TotalScore = null;
        run.OptionsSnapshot = new() { AllowFileWrites = false, ConcurrentDeviceIds = ["main", "peer"] };
        run.EvaluationSnapshot = new()
        {
            Indicators = [new() { Children = [new() { Items = [new() { MetricId = "communication-stability", Automation = new()
            { Unit = "min", PassRule = null, ScoreBands = [new() { Min = 0, Score = 0 }], Test = new() { ReadAddress = "fake.address", ReadDataType = "String", TargetDirectory = "/isolated-test" } } }] }] }],
        };
        var store = new MemoryStore(TaskRow() with { LastRunJson = JsonSerializer.Serialize(run, JsonOptions) });
        var controller = new VerifyTasksController(store, new VerifyTaskRunner(store, new ProgressService(), new NullLog()));
        var file = (FileContentResult)controller.Export("task");
        using var zip = new ZipArchive(new MemoryStream(file.FileContents), ZipArchiveMode.Read);
        var xml = string.Join("\n", zip.Entries.Where(entry => entry.FullName.EndsWith(".xml")).Select(entry =>
        { using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd(); }));
        foreach (var text in new[] { "所选测试项均分，非综合评价分", "待评分", "current-run", "真实零分", "本轮应用读失败", "fake.address", "/isolated-test", "不能自动判通过" })
            Check(xml.Contains(text, StringComparison.Ordinal), "Export includes " + text);
    }

    private static void CallbackIsNotSerialized()
    {
        var json = JsonSerializer.Serialize(new VerifyRunRequest { ReportProgress = _ => throw new Exception("Should never serialize a delegate") }, JsonOptions);
        Check(!json.Contains("reportProgress", StringComparison.OrdinalIgnoreCase), "Callback has JsonIgnore");
    }

    private static MachineDeviceDto Device(string id, string host) => new()
    {
        Id = id, Name = id, Type = "CNC", Brand = "fake", Model = "fake", Protocol = "ModbusTcp", Status = "Offline",
        Host = host, Port = 502, CreatedAt = DateTimeOffset.Now,
    };
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }

    private sealed class ProgressService : IVerifyAutomationService
    {
        public TaskCompletionSource<VerifyRunRequest> Request { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<VerifyRunResponse> Completion { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public void Reset() { Request = new(TaskCreationOptions.RunContinuationsAsynchronously); Completion = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public async Task<VerifyRunResponse> RunAsync(VerifyRunRequest request, CancellationToken ct)
        {
            Token = ct;
            Request.TrySetResult(request);
            var response = await Completion.Task.WaitAsync(ct);
            return JsonSerializer.Deserialize<VerifyRunResponse>(JsonSerializer.Serialize(response, JsonOptions), JsonOptions)!;
        }
    }
    private sealed class MemoryStore(params VerifyTaskDto[] tasks) : IVerifyTaskStore
    {
        private readonly object _gate = new();
        private List<VerifyTaskDto> _tasks = [.. tasks];
        public bool FailNextUpdate { get; set; }
        public List<VerifyTaskDto> ReadAll() { lock (_gate) return [.. _tasks]; }
        public void WriteAll(IEnumerable<VerifyTaskDto> items) { lock (_gate) _tasks = [.. items]; }
        public TResult Update<TResult>(Func<List<VerifyTaskDto>, TResult> update)
        {
            lock (_gate)
            {
                if (FailNextUpdate) { FailNextUpdate = false; throw new IOException("Fake transient persistence failure"); }
                var copy = _tasks.ToList();
                var result = update(copy);
                _tasks = copy;
                return result;
            }
        }
    }
    private sealed class DeviceMemoryStore(params MachineDeviceDto[] devices) : IDeviceStore
    {
        public List<MachineDeviceDto> ReadAll() => [.. devices];
        public void WriteAll(IEnumerable<MachineDeviceDto> items) => throw new NotSupportedException();
        public TResult Update<TResult>(Func<List<MachineDeviceDto>, TResult> update) => throw new NotSupportedException();
    }
    private sealed class NullLog(bool throwOnWrite = false) : ISystemActivityLog
    {
        public List<SystemLogDto> ReadAll() => [];
        public SystemLogDto Append(SystemLogDto entry) => entry;
        public void Write(string type, string action, string detail, string user = "系统", string ip = "-")
        { if (throwOnWrite) throw new IOException("Fake log failure"); }
    }
}
