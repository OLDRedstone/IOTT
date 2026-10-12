namespace MachineConnectionApi.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>In-memory services, injected HTTP responses and temporary journals only; no real targets.</summary>
internal static class VerifyTaskRuntimeRegressionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAll()
    {
        await DirectAndTaskEntrypointsShareCapacity();
        await DirectGroupsAndErrorsReleaseLeases();
        TargetResolutionRejectsInvalidGraphsAndHonorsInlinePriority();
        await LinkedTargetsAndBothRegistriesAreReserved();
        await ExplicitTransferTargetsReserveEndpoints();
        await FinalWriteRetriesWithoutRepeatingMeasurements();
        await PersistentFailureRecoversAndFencesOldRuns();
        await RestartReplaysFullJournalBeforeInterruptingRuns();
        await SchedulerDoesNotAwaitLongRunningMeasurements();
        DailyScheduleRemainsConsumedAfterTimeEdit();
    }

    private static async Task ExplicitTransferTargetsReserveEndpoints()
    {
        var main = Device("coverage-main", 2801);
        var transfer = Device("coverage-transfer", 2802) with
        { Transfer = new() { Host = "127.0.0.1", Port = 2803, Protocol = "FTP" } };
        var alias = Device("coverage-alias", 2803);
        var devices = new Devices(main, transfer, alias);
        var leases = new VerifyExecutionLeaseService(devices: devices);
        using (await leases.AcquireAsync(main.Id, new() { TransferDeviceIds = [transfer.Id] }, CancellationToken.None))
        {
            await Throws<VerifyTaskCapacityException>(() => leases.AcquireAsync(transfer.Id, null, CancellationToken.None));
            await Throws<VerifyTaskCapacityException>(() => leases.AcquireAsync(alias.Id, null, CancellationToken.None));
        }
        using (await leases.AcquireAsync(alias.Id, null, CancellationToken.None)) { }
        foreach (var ids in new[] { new[] { "missing" }, new[] { transfer.Id, transfer.Id }, new[] { " coverage-transfer" } })
            await Throws<ArgumentException>(() => leases.AcquireAsync(main.Id, new() { TransferDeviceIds = ids }, CancellationToken.None));
        var linked = main with { ExtendedProperties = new() { ["transferDeviceId"] = alias.Id } };
        var linkedLeases = new VerifyExecutionLeaseService(devices: new Devices(transfer, linked, alias));
        using (await linkedLeases.AcquireAsync(transfer.Id, new() { TransferDeviceIds = [linked.Id] }, CancellationToken.None))
            await Throws<VerifyTaskCapacityException>(() => linkedLeases.AcquireAsync(alias.Id, null, CancellationToken.None));
    }

    private static async Task DirectAndTaskEntrypointsShareCapacity()
    {
        var devices = new Devices(Enumerable.Range(1, 9).Select(i => Device("capacity-" + i, 2100 + i)).ToArray());
        var rows = Enumerable.Range(1, 9).Select(i => Row("capacity-task-" + i, "capacity-" + i)).ToArray();
        var store = new Store(rows);
        var engine = new Engine();
        var leases = new VerifyExecutionLeaseService(devices: devices);
        var runner = new VerifyTaskRunner(store, engine, new Log(), devices: devices, leases: leases);
        var tasks = new VerifyTasksController(store, runner);
        // A distinct service instance must still use the same process-wide table.
        var direct = new VerifyController(engine, new VerifyExecutionLeaseService(devices: devices));
        var running = direct.Run(new() { TaskId = "direct-7", DeviceId = "capacity-7" }, CancellationToken.None);
        await engine.Started("direct-7");
        var batch = (AcceptedResult)(await tasks.StartBatch(rows.Take(6).Select(row => row.Id).ToArray(), CancellationToken.None)).Result!;
        Check(((IReadOnlyList<VerifyTaskRunResult>)batch.Value!).All(result => result.Task?.Status == "running"), "Six background entries plus direct reserve seven slots");
        Check((await direct.Run(new() { DeviceId = "capacity-8" }, CancellationToken.None)).Result is ConflictObjectResult, "Direct eighth rejected");
        Check((await tasks.Start("capacity-task-8", CancellationToken.None)).Result is ConflictObjectResult, "Start eighth rejected");
        Check((await tasks.Run("capacity-task-8", CancellationToken.None)).Result is ConflictObjectResult, "Synchronous task eighth rejected");
        var syncBatch = (OkObjectResult)(await tasks.RunBatch(["capacity-task-8"], CancellationToken.None)).Result!;
        Check(((IReadOnlyList<VerifyTaskRunResult>)syncBatch.Value!).Single().Task is null, "Synchronous batch cannot bypass leases");
        // The compatibility constructor has no registry: it must be exclusive, never unprotected.
        Check((await new VerifyController(engine).Run(new() { DeviceId = "unknown-metadata" }, CancellationToken.None)).Result is ConflictObjectResult,
            "Legacy direct constructor fails closed while another run is active");
        engine.Finish("direct-7");
        await running;
        foreach (var row in rows.Take(6)) { await engine.Started(row.Id); engine.Finish(row.Id); }
        await WaitUntil(() => store.ReadAll().Count(row => row.Status == "completed") == 6);
        await WaitForFreeLease(leases, "capacity-1");
    }

    private static async Task DirectGroupsAndErrorsReleaseLeases()
    {
        var devices = new Devices(Device("group-main", 2201), Device("group-peer", 2202));
        var store = new Store(Row("group-peer-task", "group-peer"));
        var engine = new Engine();
        var leases = new VerifyExecutionLeaseService(devices: devices);
        var runner = new VerifyTaskRunner(store, engine, new Log(), devices: devices, leases: leases);
        var controller = new VerifyController(engine, leases);
        using var cancel = new CancellationTokenSource();
        var direct = controller.Run(new() { TaskId = "group-direct", DeviceId = "group-main", Options = new() { ConcurrentDeviceIds = ["group-peer"] } }, cancel.Token);
        await engine.Started("group-direct");
        await Throws<VerifyTaskCapacityException>(() => runner.RunTaskAsync("group-peer-task", "手动", CancellationToken.None));
        cancel.Cancel();
        await Throws<OperationCanceledException>(async () => await direct);
        using (await leases.AcquireAsync("group-peer", null, CancellationToken.None)) { }
        var failed = controller.Run(new() { TaskId = "direct-error", DeviceId = "group-main" }, CancellationToken.None);
        await engine.Started("direct-error");
        engine.Fail("direct-error");
        await Throws<InvalidOperationException>(async () => await failed);
        using (await leases.AcquireAsync("group-main", null, CancellationToken.None)) { }
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        await Throws<OperationCanceledException>(() => leases.AcquireAsync("group-main", null, alreadyCancelled.Token));
        using (await leases.AcquireAsync("group-main", null, CancellationToken.None)) { }
    }

    private static void TargetResolutionRejectsInvalidGraphsAndHonorsInlinePriority()
    {
        var a = Device("link-a", 2301) with { ExtendedProperties = new() { ["transferDeviceId"] = "link-b" } };
        var b = Device("link-b", 2302);
        var keys = VerifyExecutionLeaseService.ResolveTargetKeys([a.Id], [a, b], "fake registry");
        Check(keys.Contains("device:link-b") && keys.Contains("endpoint:127.0.0.1:2302"), "Linked device and its primary endpoint reserved");
        foreach (var rows in new MachineDeviceDto[][]
        {
            [a], [a, b, b], [a, b with { Id = "LINK-B" }, b],
            [a, b with { ExtendedProperties = new() { ["transferDeviceId"] = "link-a" } }],
            [a with { ExtendedProperties = new() { ["transferDeviceId"] = "link-a" } }],
        })
        {
            var rejected = false;
            try { VerifyExecutionLeaseService.ResolveTargetKeys([a.Id], rows, "fake registry"); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Missing, ambiguous and cyclic links rejected");
        }
        var inline = a with { Transfer = new() { Host = "127.0.0.1", Port = 2303, Protocol = "FTP" } };
        keys = VerifyExecutionLeaseService.ResolveTargetKeys([a.Id], [inline], "fake registry");
        Check(keys.Contains("endpoint:127.0.0.1:2303") && !keys.Contains("device:link-b"), "Inline wins over dangling association");
        var linkedWithInline = b with { Transfer = new() { Host = "127.0.0.1", Port = 2304, Protocol = "FTP" } };
        keys = VerifyExecutionLeaseService.ResolveTargetKeys([a.Id], [a, linkedWithInline], "fake registry");
        Check(keys.Contains("endpoint:127.0.0.1:2302") && !keys.Contains("endpoint:127.0.0.1:2304"),
            "Host's one-hop primary endpoint is reserved, not linked row's unused inline transfer");
    }

    private static async Task LinkedTargetsAndBothRegistriesAreReserved()
    {
        var main = Device("two-store-main", 2401) with { ExtendedProperties = new() { ["transferDeviceId"] = "two-store-file" }, UpstreamSynced = true };
        var file = Device("two-store-file", 2402);
        var localAlias = Device("two-store-local-alias", 2402);
        var remoteAlias = Device("two-store-remote-alias", 2403);
        var local = new Devices(main, file, localAlias, remoteAlias);
        var upstream = new[] { main, file with { Port = 2403 }, localAlias, remoteAlias };
        using var http = new FakeHttp(upstream);
        var leases = new VerifyExecutionLeaseService(devices: local, httpClientFactory: http);
        using (await leases.AcquireAsync(main.Id, null, CancellationToken.None))
        {
            foreach (var id in new[] { file.Id, localAlias.Id, remoteAlias.Id })
                await Throws<VerifyTaskCapacityException>(() => leases.AcquireAsync(id, null, CancellationToken.None));
        }
        Check(http.Requests >= 4, "Actual upstream registry checked independently, despite upstreamSynced=true");
        http.Rows = [main];
        await Throws<ArgumentException>(() => leases.AcquireAsync(main.Id, null, CancellationToken.None));
        http.Rows = upstream;
        using (await leases.AcquireAsync(main.Id, null, CancellationToken.None)) { }
    }

    private static async Task FinalWriteRetriesWithoutRepeatingMeasurements()
    {
        var store = new Store(Row("one-write", "one-write-device")) { FailFinalWrites = 1 };
        var engine = new Engine();
        var runner = new VerifyTaskRunner(store, engine, new Log());
        var running = runner.RunTaskAsync("one-write", "手动", CancellationToken.None);
        await engine.Started("one-write");
        engine.Finish("one-write", Evidence());
        var completed = (await running)!;
        Check(completed.Status == "completed" && completed.ActiveRunId is null, "Transient final rename retried and persistent running cleared");
        Check(engine.Calls == 1 && store.FinalAttempts == 2, "Only persistence retried, not measurement");
        Check(completed.LastRunJson.Contains("fake-evidence-0"), "Complete result retained on final retry");
    }

    private static async Task PersistentFailureRecoversAndFencesOldRuns()
    {
        using var temp = new TempJournal();
        var store = new Store(Row("persistent", "persistent-device")) { FailFinalWrites = int.MaxValue };
        var engine = new Engine();
        var runner = new VerifyTaskRunner(store, engine, new Log(), completionJournal: temp.Journal);
        var running = runner.RunTaskAsync("persistent", "手动", CancellationToken.None);
        await engine.Started("persistent");
        engine.Finish("persistent", Evidence());
        await Throws<VerifyTaskPersistenceException>(async () => await running);
        Check(store.FinalAttempts == 3 && store.ReadAll().Single().Status == "running", "Final retries bounded and pending state remains explicit");
        var pending = temp.Journal.ReadAll().Single();
        Check(pending.Task.LastRunJson.Contains("fake-evidence-0"), "Full pending result durably journaled");
        using (await new VerifyExecutionLeaseService().AcquireAsync(null, null, CancellationToken.None)) { }
        var controller = new VerifyTasksController(store, runner);
        Check(await controller.RecoverResults(CancellationToken.None) is ObjectResult { StatusCode: 503 }, "Continuous failure exposes retryable recovery status");
        Check(engine.Calls == 1, "Recovery never repeats a device test");
        store.FailFinalWrites = 0;
        Check(await controller.RecoverResults(CancellationToken.None) is NoContentResult, "Same-process recovery succeeds without restart");
        Check(store.ReadAll().Single().ActiveRunId is null && temp.Journal.ReadAll().Count == 0, "Recovery clears running and journal");
        engine.Reset("persistent");
        var next = runner.RunTaskAsync("persistent", "手动", CancellationToken.None);
        await engine.Started("persistent");
        var newer = store.ReadAll().Single();
        temp.Journal.Save(pending); // simulate a delayed old write/uncleared journal
        await runner.RetryPendingResultsAsync(CancellationToken.None);
        Check(store.ReadAll().Single().ActiveRunId == newer.ActiveRunId && newer.ActiveRunId != pending.RunId,
            "Stale recovery cannot overwrite a newer run");
        engine.Finish("persistent", Evidence());
        await next;
    }

    private static async Task RestartReplaysFullJournalBeforeInterruptingRuns()
    {
        using var temp = new TempJournal();
        var store = new Store(Row("restart-journal", "restart-journal-device") with { ScheduleType = "daily", ScheduleTime = "00:00" })
        { FailFinalWrites = int.MaxValue };
        var engine = new Engine();
        var runner = new VerifyTaskRunner(store, engine, new Log(), completionJournal: temp.Journal);
        var run = runner.RunTaskAsync("restart-journal", "定时", CancellationToken.None);
        await engine.Started("restart-journal");
        var acceptedAt = store.ReadAll().Single().LastAutoRunAt;
        engine.Finish("restart-journal", Evidence());
        await Throws<VerifyTaskPersistenceException>(async () => await run);
        store.FailFinalWrites = 0;
        var restartedEngine = new Engine();
        var restarted = new VerifyTaskRunner(store, restartedEngine, new Log(), completionJournal: temp.Journal);
        using var scheduler = new VerifyTaskSchedulerHostedService(store, restarted, NullLogger<VerifyTaskSchedulerHostedService>.Instance);
        await scheduler.StartAsync(CancellationToken.None);
        await scheduler.StopAsync(CancellationToken.None);
        var row = store.ReadAll().Single();
        var result = JsonSerializer.Deserialize<VerifyRunResponse>(row.LastRunJson, JsonOptions)!;
        Check(row.Status == "completed" && result.Metrics.Single().Score == 0 && result.Metrics.Single().Evidence.Contains("fake-evidence-0"),
            "Restart recovers full measured result instead of marking it interrupted");
        Check(row.LastAutoRunAt == acceptedAt && restartedEngine.Calls == 0, "Restart preserves daily acceptance and never reruns measurement");
    }

    private static async Task SchedulerDoesNotAwaitLongRunningMeasurements()
    {
        var rows = new[] { Row("scheduled-A", "scheduled-device-A"), Row("scheduled-B", "scheduled-device-B"), Row("scheduled-C", "scheduled-device-C") }
            .Select(row => row with { ScheduleType = "daily", ScheduleTime = "00:00" }).ToArray();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["VerifyTasks:MaxConcurrentTasks"] = "2" }).Build();
        var store = new Store(rows);
        var engine = new Engine();
        var runner = new VerifyTaskRunner(store, engine, new Log(), config);
        using var scheduler = new VerifyTaskSchedulerHostedService(store, runner, NullLogger<VerifyTaskSchedulerHostedService>.Instance);
        await Scan(scheduler).WaitAsync(TimeSpan.FromSeconds(3));
        await engine.Started("scheduled-A");
        await engine.Started("scheduled-B");
        Check(store.ReadAll().Single(row => row.Id == "scheduled-C").LastAutoRunAt is null, "Capacity refusal doesn't consume C's scheduled day");
        Check(store.ReadAll().Where(row => row.Id != "scheduled-C").All(row => row.LastAutoRunAt.HasValue), "Day consumed at background acceptance");
        engine.Finish("scheduled-B");
        await WaitUntil(() => store.ReadAll().Single(row => row.Id == "scheduled-B").Status == "completed");
        await Task.Delay(20); // allow the background finally to release B's slot
        await Scan(scheduler).WaitAsync(TimeSpan.FromSeconds(3));
        await engine.Started("scheduled-C");
        Check(store.ReadAll().Single(row => row.Id == "scheduled-A").Status == "running", "C starts on a later scan while long A is still running");
        await Scan(scheduler);
        Check(engine.Calls == 3, "No same-day duplicate after short B completes");
        engine.Finish("scheduled-A");
        engine.Finish("scheduled-C");
        await WaitUntil(() => store.ReadAll().All(row => row.Status == "completed"));
        await Task.Delay(20);
        await Scan(scheduler);
        Check(engine.Calls == 3, "Daily dedup survives completion");
        Check(await runner.StartTaskAsync("scheduled-B", "定时", CancellationToken.None) is null && engine.Calls == 3,
            "Stale scheduled reservation cannot repeat a day even after the prior run finished");
    }

    private static void DailyScheduleRemainsConsumedAfterTimeEdit()
    {
        var now = new DateTimeOffset(2026, 9, 29, 16, 0, 0, TimeSpan.FromHours(8));
        var consumed = Row("edited-time", "fake") with
        { ScheduleType = "daily", ScheduleTime = "15:00", LastAutoRunAt = now.AddHours(-8) };
        var due = typeof(VerifyTaskSchedulerHostedService).GetMethod("IsDue", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check(!(bool)due.Invoke(null, [consumed, now])!, "Moving today's schedule cannot consume the same day twice");
        Check((bool)due.Invoke(null, [consumed, now.AddDays(1)])!, "A new day is still eligible");
    }

    private static Task Scan(VerifyTaskSchedulerHostedService scheduler) => (Task)typeof(VerifyTaskSchedulerHostedService)
        .GetMethod("RunDueTasksAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scheduler, [CancellationToken.None])!;
    private static VerifyTaskDto Row(string id, string device) => new() { Id = id, Name = id, Type = "function", DeviceId = device, CreatedAt = DateTimeOffset.Now, MetricIds = ["file-integrity"] };
    private static MachineDeviceDto Device(string id, int port) => new()
    { Id = id, Name = id, Type = "CNC", Brand = "fake", Model = "fake", Protocol = "FTP", Status = "Offline", Host = "127.0.0.1", Port = port, CreatedAt = DateTimeOffset.Now };
    private static VerifyRunResponse Evidence() => new()
    {
        Status = "completed", Result = "不通过", ProgressPercent = 100,
        Metrics = [new() { MetricId = "file-integrity", Measurement = 0, Score = 0, MeasurementSource = "fake-regression-only", ExecutionStatus = "completed", Evidence = ["fake-evidence-0"] }],
    };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task WaitUntil(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private static async Task WaitForFreeLease(VerifyExecutionLeaseService leases, string device)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            try { using var lease = await leases.AcquireAsync(device, null, timeout.Token); return; }
            catch (VerifyTaskCapacityException) { await Task.Delay(10, timeout.Token); }
        }
    }

    private sealed class Engine : IVerifyAutomationService
    {
        private sealed class Call
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<VerifyRunResponse> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly ConcurrentDictionary<string, Call> _calls = new();
        private int _count;
        public int Calls => _count;
        private Call Get(string id) => _calls.GetOrAdd(id, _ => new());
        public Task Started(string id) => Get(id).Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Finish(string id, VerifyRunResponse? response = null) => Get(id).Complete.SetResult(response ?? new() { Status = "completed" });
        public void Fail(string id) => Get(id).Complete.SetException(new InvalidOperationException("Injected engine failure"));
        public void Reset(string id) => _calls[id] = new();
        public async Task<VerifyRunResponse> RunAsync(VerifyRunRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            var call = Get(request.TaskId ?? request.DeviceId!);
            call.Started.TrySetResult();
            return await call.Complete.Task.WaitAsync(ct);
        }
    }
    private sealed class Store(params VerifyTaskDto[] rows) : IVerifyTaskStore
    {
        private readonly object _gate = new();
        private List<VerifyTaskDto> _rows = [.. rows];
        public int FailFinalWrites { get; set; }
        public int FinalAttempts { get; private set; }
        public List<VerifyTaskDto> ReadAll() { lock (_gate) return [.. _rows]; }
        public void WriteAll(IEnumerable<VerifyTaskDto> values) { lock (_gate) _rows = [.. values]; }
        public T Update<T>(Func<List<VerifyTaskDto>, T> update)
        {
            lock (_gate)
            {
                var copy = _rows.ToList();
                var result = update(copy);
                if (copy.Any(row => row.ActiveRunId is null && row.LastRunJson.Length > 0 && _rows.Any(old => old.Id == row.Id && old.ActiveRunId is not null)))
                {
                    FinalAttempts++;
                    if (FailFinalWrites > 0) { FailFinalWrites--; throw new IOException("Injected final atomic rename failure"); }
                }
                _rows = copy;
                return result;
            }
        }
    }
    private sealed class Devices(params MachineDeviceDto[] rows) : IDeviceStore
    {
        public List<MachineDeviceDto> ReadAll() => [.. rows];
        public void WriteAll(IEnumerable<MachineDeviceDto> values) => throw new NotSupportedException();
        public T Update<T>(Func<List<MachineDeviceDto>, T> update) => throw new NotSupportedException();
    }
    private sealed class Log : ISystemActivityLog
    {
        public List<SystemLogDto> ReadAll() => [];
        public SystemLogDto Append(SystemLogDto entry) => entry;
        public void Write(string type, string action, string detail, string user = "系统", string ip = "-") { }
    }
    private sealed class FakeHttp(MachineDeviceDto[] rows) : HttpMessageHandler, IHttpClientFactory
    {
        public MachineDeviceDto[] Rows { get; set; } = rows;
        public int Requests { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("http://127.0.0.1/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(request.Method == HttpMethod.Get, "Registry inspection must not synchronize/write devices");
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Rows, JsonOptions), System.Text.Encoding.UTF8, "application/json") });
        }
    }
    private sealed class TempJournal : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "verify-runtime-" + Guid.NewGuid().ToString("N"));
        public VerifyTaskCompletionJournal Journal { get; }
        public TempJournal() { Directory.CreateDirectory(_directory); Journal = new(Path.Combine(_directory, "completions.json")); }
        public void Dispose() { Directory.Delete(_directory, recursive: true); }
    }
}
