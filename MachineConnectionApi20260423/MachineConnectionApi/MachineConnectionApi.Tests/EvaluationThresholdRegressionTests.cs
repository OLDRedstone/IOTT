using MachineConnectionApi.Models;
using MachineConnectionApi.Services;

namespace MachineConnectionApi.Tests;

internal static class EvaluationThresholdRegressionTests
{
    public static void RunAll()
    {
        var name = $"evaluation-threshold-test-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, "App_Data", name);
        var recordName = $"evaluation-threshold-records-{Guid.NewGuid():N}.json";
        var recordPath = Path.Combine(AppContext.BaseDirectory, "App_Data", recordName);
        try
        {
            var store = new EvaluationIndicatorStore(name);
            var config = store.Get("machine");
            var item = Items(config).First(value => value.MetricId is null);
            var criterion = new EvaluationPassRule { Comparison = "lte", Threshold = 0.02, Unit = "mm" };
            var saved = store.Save(Replace(config, item with { ManualPassRule = criterion }));
            var restored = new EvaluationIndicatorStore(name).Get("machine");
            var restoredItem = Items(restored).Single(value => value.Id == item.Id);
            Check(restoredItem.ManualPassRule == criterion && restoredItem.Automation is null,
                "Manual threshold was lost or became an automatic test");
            Check(store.Get("machining").Version == 1, "Threshold update changed the other category");
            var manualOnly = new ManualOnlyStores();
            var knowledge = new EvaluationKnowledgeStore(store, manualOnly, manualOnly, recordName);
            var record = knowledge.Create(new KnowledgeRecord
            {
                MachineName = "Threshold fixture", MachineNo = "threshold-test", MachineModel = "fixture",
                ControlSystem = "fixture", PartName = "fixture", TestDate = "2026-09-29", Snapshot = saved,
            });
            var zero = store.Save(Replace(saved, restoredItem with { ManualPassRule = criterion with { Threshold = 0 } }));
            Check(Items(zero).Single(value => value.Id == item.Id).ManualPassRule!.Threshold == 0, "Zero threshold was cleared");
            var updated = knowledge.Update(record.Id, record with { Snapshot = zero, Conclusion = "Updated notes" })!;
            var snapshot = new EvaluationKnowledgeStore(store, manualOnly, manualOnly, recordName).Find(record.Id)!;
            Check(Items(snapshot.Snapshot).Single(value => value.Id == item.Id).ManualPassRule!.Threshold == 0.02,
                "Editing a live threshold changed the previous snapshot");
            Check(snapshot.Version == updated.Version && snapshot.Conclusion == "Updated notes"
                && snapshot.Scores[item.Id] is null && snapshot.TotalScore is null,
                "Manual threshold produced an automatic score or prevented record updates");
            EvaluationIndicatorStore.Validate(Replace(zero, restoredItem with
            { ManualPassRule = new EvaluationPassRule { Comparison = "gte", Threshold = -10, Unit = "℃" } }));
            var cleared = store.Save(Replace(zero, restoredItem with { ManualPassRule = null }));
            Check(Items(cleared).Single(value => value.Id == item.Id).ManualPassRule is null, "Threshold clear was not persisted");

            foreach (var invalid in new[]
            {
                criterion with { Comparison = "unknown" }, criterion with { Threshold = double.NaN },
                criterion with { Threshold = double.PositiveInfinity }, criterion with { Unit = " " },
                criterion with { Unit = "mm\n" }, criterion with { Unit = new string('m', 51) },
            }) ExpectInvalid(() => EvaluationIndicatorStore.Validate(Replace(cleared, restoredItem with { ManualPassRule = invalid })));
            var automated = Items(cleared).First(value => value.MetricId is not null);
            ExpectInvalid(() => EvaluationIndicatorStore.Validate(Replace(cleared, automated with { ManualPassRule = criterion })));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(recordPath)) File.Delete(recordPath);
        }
    }

    private sealed class ManualOnlyStores : IVerifyTaskStore, IDeviceStore
    {
        List<VerifyTaskDto> IVerifyTaskStore.ReadAll() => throw new InvalidOperationException("Manual thresholds must not access tasks");
        List<MachineDeviceDto> IDeviceStore.ReadAll() => throw new InvalidOperationException("Manual thresholds must not access devices");
        public void WriteAll(IEnumerable<VerifyTaskDto> items) => throw new NotSupportedException();
        public void WriteAll(IEnumerable<MachineDeviceDto> items) => throw new NotSupportedException();
        public TResult Update<TResult>(Func<List<VerifyTaskDto>, TResult> update) => throw new NotSupportedException();
        public TResult Update<TResult>(Func<List<MachineDeviceDto>, TResult> update) => throw new NotSupportedException();
    }

    private static IEnumerable<EvaluationItem> Items(EvaluationConfig config) =>
        config.Indicators.SelectMany(section => section.Children).SelectMany(child => child.Items);

    private static EvaluationConfig Replace(EvaluationConfig config, EvaluationItem replacement) => config with
    {
        Indicators = config.Indicators.Select(section => section with
        {
            Children = section.Children.Select(child => child with
            { Items = child.Items.Select(item => item.Id == replacement.Id ? replacement : item).ToList() }).ToList(),
        }).ToList(),
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid threshold was accepted");
    }
}
