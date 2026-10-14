using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;

namespace MachineConnectionApi.Tests;

// Integration hook: call RunAll() alongside EvaluationKnowledgeRegressionTests.RunAll().
// This suite only uses in-memory task/device fakes and uniquely named temporary JSON stores.
internal static class EvaluationKnowledgeAutomationRegressionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void RunAll()
    {
        CurrentRunScoresAndSelectedPreview();
        LegacyAndUndeterminedResultsRemainNull();
        ImportOnlyFinishedHistoricalRuns();
        ResyncCreatesANewRecordAndKeepsProvenance();
        ZeroWeightsAndCustomScoresRemainValid();
    }

    private static void CurrentRunScoresAndSelectedPreview()
    {
        using var fixture = new Fixture();
        var run = Run();
        fixture.SetRun(run);
        var preview = fixture.Store.ListTasks().Single();
        Check(preview.Metrics.Count == 2 && preview.TotalScore == 50 && preview.PassRate == 50,
            "preview must average the two selected scores, not a fixed seven or the weighted evaluation");
        Check(preview.Metrics[0].Score == 0 && preview.MachineName == "Historical machine",
            "preview lost zero score or used the current device");
        var draft = fixture.Store.TaskDraft("task");
        Check(draft.Scores["protocol-item"] == 0 && draft.Scores["stability-item"] == 100 && draft.Scores["manual-item"] is null,
            "sync must copy actual scores and leave unselected/manual items unrated");
        Check(draft.TestResults["protocol-item"] == "0" && draft.Remarks["protocol-item"].Contains("current-run")
            && draft.Remarks["protocol-item"].Contains("measured zero"), "sync lost raw evidence or scoring explanation");
        draft.Scores["manual-item"] = 100;
        var saved = fixture.Store.Create(draft with { PartName = "part" });
        Check(saved.TotalScore == 60, "weighted evaluation must remain distinct from the selected-task average of 50");

        run.Metrics[0].Measurement = 100;
        run.Metrics[0].Score = 100;
        run.Metrics[0].Status = "passed";
        fixture.SetRun(run);
        Check(fixture.Store.ListTasks().Single().PassRate == 100, "all passing must produce 100, not 1 or a value above 100");
        run.Metrics[0].Measurement = 0;
        run.Metrics[0].Score = 0;
        run.Metrics[0].Status = "failed";
        run.Metrics[1].Measurement = 0;
        run.Metrics[1].Score = 0;
        run.Metrics[1].Status = "failed";
        fixture.SetRun(run);
        Check(fixture.Store.ListTasks().Single().PassRate == 0 && fixture.Store.ListTasks().Single().TotalScore == 0,
            "zero pass rate and zero total score are valid values");
    }

    private static void LegacyAndUndeterminedResultsRemainNull()
    {
        using var fixture = new Fixture();
        var legacy = Run();
        legacy.Metrics = [new VerifyMetricResult { MetricId = "industrial-protocol", Status = "passed", Value = "246%", Result = "通过" }];
        legacy.TotalScore = 80;
        fixture.SetRun(legacy);
        var preview = fixture.Store.ListTasks().Single();
        Check(preview.TotalScore is null && preview.PassRate is null && fixture.Store.TaskDraft("task").Scores.Values.All(value => value is null),
            "legacy passed/result/value flags must never fabricate 80 points or a pass rate");

        var noPassRule = Run();
        var items = noPassRule.EvaluationSnapshot!.Indicators[0].Children[0].Items;
        items[0] = items[0] with { Automation = items[0].Automation! with { PassRule = null } };
        fixture.SetRun(noPassRule);
        Check(fixture.Store.ListTasks().Single().TotalScore == 50 && fixture.Store.ListTasks().Single().PassRate is null,
            "configured scoring bands must not imply acceptance criteria");

        foreach (var change in new Action<VerifyMetricResult>[]
        {
            metric => metric.MeasurementSource = "configuration",
            metric => metric.Measurement = null,
            metric => metric.ExecutionStatus = "error",
            metric => metric.Status = "unrated",
            metric => metric.Status = "passed", // Contradicts the recorded value/rule.
            metric => metric.Unit = "MB",
        })
        {
            var uncertain = Run();
            change(uncertain.Metrics[0]);
            fixture.SetRun(uncertain);
            Check(fixture.Store.ListTasks().Single().PassRate is null, "uncertain/incompatible evidence must not become a pass rate");
        }
        var missing = Run();
        missing.Metrics[0].Score = null;
        fixture.SetRun(missing);
        Check(fixture.Store.ListTasks().Single().TotalScore is null, "a selected unrated item must null the average");
        missing.Metrics[0].Score = -1;
        fixture.SetRun(missing);
        Check(fixture.Store.TaskDraft("task").Scores["protocol-item"] is null, "out-of-range score must not be synchronized");
        var units = Run();
        units.Metrics[1].Measurement = 1800;
        units.Metrics[1].Unit = "s";
        fixture.SetRun(units);
        Check(fixture.Store.ListTasks().Single().PassRate == 50, "compatible measurement units must use the shared conversion and judging rules");
    }

    private static void ImportOnlyFinishedHistoricalRuns()
    {
        using var fixture = new Fixture();
        var finished = Run();
        fixture.SetRun(finished);
        var active = Run();
        active.RunId = "new-active-run";
        active.Status = "running";
        fixture.Tasks.Rows[0] = fixture.Tasks.Rows[0] with
        {
            Status = "running", ActiveRunId = active.RunId, CurrentRunJson = Serialize(active), DeviceId = "edited-device",
        };
        Check(fixture.Store.TaskDraft("task").SyncRunId == finished.RunId && fixture.Store.ListTasks().Count == 1,
            "an active task must still expose only its last finished run");
        fixture.Tasks.Rows[0] = fixture.Tasks.Rows[0] with { LastRunJson = "" };
        Check(fixture.Store.ListTasks().Count == 0, "CurrentRunJson is not importable evidence");
        Expect<ArgumentException>(() => fixture.Store.TaskDraft("task"));

        foreach (var change in new Action<VerifyRunResponse>[]
        {
            run => run.Status = "running",
            run => run.Status = "pending",
            run => run.Status = "unknown",
            run => run.CompletedAt = "",
            run => run.RunId = "",
            run => run.TaskId = "other-task",
            run => run.EvaluationSnapshot = null,
            run => run.MachineSnapshot = null,
            run => run.Metrics = [],
        })
        {
            var invalid = Run();
            change(invalid);
            fixture.SetRun(invalid);
            Check(fixture.Store.ListTasks().Count == 0, "unfinished or mismatched evidence was listed as importable");
            Expect<ArgumentException>(() => fixture.Store.TaskDraft("task"));
        }
        fixture.SetRun(Run("machining"));
        Check(fixture.Store.TaskDraft("task").Category == "machining" && fixture.Store.ListTasks().Single().Category == "machining",
            "task category must come from the historical snapshot");
        Expect<ArgumentException>(() => fixture.Store.TaskDraft("task", "machine"));
    }

    private static void ResyncCreatesANewRecordAndKeepsProvenance()
    {
        using var fixture = new Fixture();
        var firstRun = Run();
        fixture.SetRun(firstRun);
        var firstDraft = fixture.Store.TaskDraft("task") with { PartName = "part", Conclusion = "historical conclusion" };
        firstDraft.Scores["manual-item"] = 82.125;
        var original = fixture.Store.Create(firstDraft);
        Expect<KnowledgeConflictException>(() => fixture.Store.Create(firstDraft));

        var newRun = Run();
        newRun.RunId = "run-2";
        newRun.EvaluationSnapshot = newRun.EvaluationSnapshot! with { Version = 2 };
        newRun.EvaluationSnapshot.Indicators[0] = newRun.EvaluationSnapshot.Indicators[0] with { Name = "new snapshot category" };
        fixture.SetRun(newRun);
        Expect<KnowledgeConflictException>(() => fixture.Store.Create(firstDraft));
        var nextDraft = fixture.Store.TaskDraft("task") with { PartName = original.PartName, Conclusion = original.Conclusion };
        nextDraft.Scores["manual-item"] = original.Scores["manual-item"];
        var next = fixture.Store.Create(nextDraft);
        Check(next.Id != original.Id && next.SyncRunId == "run-2" && next.Snapshot.Version == 2 && fixture.Store.List().Count == 2,
            "new run import must create a separate record with new provenance");
        Check(fixture.Store.Find(original.Id)!.Snapshot.Version == 1 && fixture.Store.Find(original.Id)!.SyncRunId == "run-1",
            "resync changed the historical record");
        Expect<KnowledgeConflictException>(() => fixture.Store.Create(nextDraft));

        var updated = fixture.Store.Update(original.Id, original with
        {
            Snapshot = next.Snapshot, SyncTaskId = "forged-task", SyncRunId = next.SyncRunId, DataSource = "manual",
        })!;
        Check(updated.Snapshot.Version == 1 && updated.SyncTaskId == "task" && updated.SyncRunId == "run-1" && updated.DataSource == "sync",
            "ordinary update must not overwrite snapshot or source");
        Expect<KnowledgeConflictException>(() => fixture.Store.Update(original.Id, original));
        Expect<ArgumentException>(() => fixture.Store.Create(nextDraft with { DataSource = "manual" }));
        Expect<ArgumentException>(() => EvaluationKnowledgeStore.Prepare(nextDraft with { SyncRunId = null }));
    }

    private static void ZeroWeightsAndCustomScoresRemainValid()
    {
        using var fixture = new Fixture();
        fixture.SetRun(Run());
        var draft = fixture.Store.TaskDraft("task") with { PartName = "part" };
        draft.Scores["protocol-item"] = null;
        draft.Scores["stability-item"] = 100;
        draft.Scores["manual-item"] = 82.125;
        draft.Weights["protocol-item"] = 0;
        draft.Weights["stability-item"] = 80;
        var saved = fixture.Store.Create(draft);
        Check(saved.Scores["manual-item"] == 82.125 && saved.TotalScore == 96.4 && saved.Weights["protocol-item"] == 0,
            "zero weight, nullable score or a custom decimal score was lost");
    }

    private static VerifyRunResponse Run(string category = "machine") => new()
    {
        TaskId = "task", RunId = "run-1", Status = "completed", CompletedAt = "2026-09-29T10:00:00+08:00",
        TotalScore = 999, // Previews derive the selected mean, not an untrusted/stale aggregate.
        MachineSnapshot = new() { Id = "historical-device", Name = "Historical machine", DeviceCode = "history-no", Model = "history-model", ControlSystem = "history-control" },
        EvaluationSnapshot = new()
        {
            Category = category, Version = 1, Indicators =
            [
                new() { Id = "section", Name = "category", Weight = 100, Children =
                [
                    new() { Id = "sub", Name = "subcategory", Weight = 100, Items =
                    [
                        new() { Id = "protocol-item", Name = "protocol", Weight = 40, MetricId = "industrial-protocol",
                            Automation = EvaluationAutomationRules.DefaultFor("industrial-protocol")! with { PassRule = new() { Threshold = 50, Unit = "%" } } },
                        new() { Id = "stability-item", Name = "stability", Weight = 40, MetricId = "communication-stability",
                            Automation = EvaluationAutomationRules.DefaultFor("communication-stability")! with { PassRule = new() { Threshold = 30, Unit = "min" } } },
                        new() { Id = "manual-item", Name = "manual", Weight = 20 },
                    ] },
                ] },
            ],
        },
        Metrics =
        [
            new() { MetricId = "industrial-protocol", Name = "protocol", Status = "failed", ExecutionStatus = "completed",
                Measurement = 0, Unit = "%", Score = 0, Value = "0", ScoreReason = "measured zero", MeasurementSource = "current-run", Evidence = ["fake measured evidence"] },
            new() { MetricId = "communication-stability", Name = "stability", Status = "passed", ExecutionStatus = "completed",
                Measurement = 30, Unit = "min", Score = 100, Value = "30 min", MeasurementSource = "current-run" },
        ],
    };

    private static string Serialize(VerifyRunResponse run) => JsonSerializer.Serialize(run, JsonOptions);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _indicatorFile = $"knowledge-automation-indicators-{Guid.NewGuid():N}.json";
        private readonly string _recordFile = $"knowledge-automation-records-{Guid.NewGuid():N}.json";
        public TaskStore Tasks { get; } = new();
        public EvaluationKnowledgeStore Store { get; }
        public Fixture() => Store = new(new EvaluationIndicatorStore(_indicatorFile), Tasks, new NeverReadDevices(), _recordFile);
        public void SetRun(VerifyRunResponse run) => Tasks.Rows = [new VerifyTaskDto
        {
            Id = "task", Name = "fake task", Type = run.EvaluationSnapshot?.Category ?? "machine",
            CreatedAt = DateTimeOffset.UtcNow, LastRunJson = Serialize(run),
        }];
        public void Dispose()
        {
            foreach (var name in new[] { _indicatorFile, _recordFile })
            {
                var path = Path.Combine(AppContext.BaseDirectory, "App_Data", name);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private sealed class TaskStore : IVerifyTaskStore
    {
        public List<VerifyTaskDto> Rows { get; set; } = [];
        public List<VerifyTaskDto> ReadAll() => Rows;
        public void WriteAll(IEnumerable<VerifyTaskDto> items) => Rows = items.ToList();
        public TResult Update<TResult>(Func<List<VerifyTaskDto>, TResult> update) => update(Rows);
    }

    private sealed class NeverReadDevices : IDeviceStore
    {
        public List<MachineDeviceDto> ReadAll() => throw new InvalidOperationException("Knowledge sync must never contact/read live devices.");
        public void WriteAll(IEnumerable<MachineDeviceDto> items) => throw new NotSupportedException();
        public TResult Update<TResult>(Func<List<MachineDeviceDto>, TResult> update) => throw new NotSupportedException();
    }
}
